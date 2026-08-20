using System.Diagnostics;
using System.Net.Sockets;

namespace ChatGPTWindowsMcp;

internal enum RuntimeState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Faulted
}

internal sealed class RuntimeManager : IDisposable
{
    private readonly LogSink _log;
    private readonly Bootstrapper _bootstrapper;

    private Process? _mcpProcess;
    private Process? _tunnelProcess;
    private bool _ownsMcp;
    private int? _ownedMcpPort;
    private int? _mcpListenerPid;
    private CancellationTokenSource? _lifetimeCts;
    private TaskCompletionSource<bool>? _tunnelReadySignal;
    private OwnedProcessJob? _processJob;

    public RuntimeState State { get; private set; } = RuntimeState.Stopped;
    public bool McpReady { get; private set; }
    public bool TunnelReady { get; private set; }

    public event Action<RuntimeState>? StateChanged;
    public event Action? HealthChanged;

    public RuntimeManager(LogSink log)
    {
        _log = log;
        _bootstrapper = new Bootstrapper(log);
    }

    public async Task InstallDependenciesAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        var uv = _bootstrapper.FindUv();
        if (uv is null)
            await _bootstrapper.InstallUvWithWingetAsync(config, cancellationToken);
        else
            _log.Write($"uv 已存在：{uv}");

        var runner = _bootstrapper.FindPythonToolRunner();
        if (runner is null)
            throw new InvalidOperationException("uv 已安装，但找不到 uvx.exe 或 uv tool run。请重新启动程序后再试。");

        _log.Write(runner.Value.PrefixArgs.Length == 0
            ? $"Python 工具运行器：{runner.Value.FileName}（uvx 模式）"
            : $"Python 工具运行器：{runner.Value.FileName} tool run（uv 兼容模式）");

        var tunnelClient = await _bootstrapper.EnsureTunnelClientAsync(config, cancellationToken);
        _log.Write($"tunnel-client 已可用：{tunnelClient}");
    }

    public async Task StartAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        if (State is RuntimeState.Starting or RuntimeState.Running)
            return;

        SetState(RuntimeState.Starting);
        McpReady = false;
        TunnelReady = false;
        HealthChanged?.Invoke();

        _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _lifetimeCts.Token;

        _processJob?.Dispose();
        _processJob = OwnedProcessJob.TryCreate(_log);

        try
        {
            var runner = _bootstrapper.FindPythonToolRunner();
            if (runner is null)
                throw new InvalidOperationException("未找到 uvx.exe 或 uv.exe。请先点击“安装/检查依赖”。");

            var tunnelClient = await _bootstrapper.EnsureTunnelClientAsync(config, ct);

            if (await IsPortOpenAsync(config.McpPort, TimeSpan.FromMilliseconds(500)))
            {
                if (!config.ReuseExistingMcp)
                    throw new InvalidOperationException($"端口 {config.McpPort} 已被占用。请停止现有服务或开启“复用已运行的 Windows-MCP”。");

                _ownsMcp = false;
                _ownedMcpPort = null;
                _mcpListenerPid = null;
                McpReady = true;
                _log.Write($"检测到 127.0.0.1:{config.McpPort} 已有服务，复用现有 MCP。");
                HealthChanged?.Invoke();
            }
            else
            {
                _ownsMcp = true;
                _ownedMcpPort = config.McpPort;
                _mcpProcess = StartWindowsMcp(runner.Value.FileName, runner.Value.PrefixArgs, config, ct);
                await WaitForPortAsync(config.McpPort, TimeSpan.FromSeconds(60), ct);
                _mcpListenerPid = FindListeningProcessId(config.McpPort);
                McpReady = true;
                _log.Write($"Windows-MCP 已监听 http://127.0.0.1:{config.McpPort}/mcp" +
                           (_mcpListenerPid is int pid ? $"（PID {pid}）" : ""));
                HealthChanged?.Invoke();
            }

            await _bootstrapper.CreateOrRefreshProfileAsync(config, tunnelClient, ct);

            var doctorExit = await DoctorAsync(config, tunnelClient, ct);
            if (doctorExit != 0)
                throw new InvalidOperationException($"Tunnel doctor 检查失败，退出码 {doctorExit}。请查看日志。");

            _tunnelReadySignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _tunnelProcess = StartTunnel(tunnelClient, config, ct);

            var readyTask = _tunnelReadySignal.Task;
            var readinessTimeout = Task.Delay(TimeSpan.FromSeconds(60), ct);
            var completed = await Task.WhenAny(readyTask, readinessTimeout);

            if (_tunnelProcess.HasExited)
                throw new InvalidOperationException($"tunnel-client 启动后退出，退出码 {_tunnelProcess.ExitCode}。");

            if (completed != readyTask)
                throw new TimeoutException("等待 OpenAI Tunnel Control Plane 建立连接超时。请检查代理、DNS/IPv6 和下方 tunnel 日志。");

            await readyTask;
            TunnelReady = true;
            HealthChanged?.Invoke();
            SetState(RuntimeState.Running);
            _log.Write("服务已启动。现在可以在 ChatGPT 中创建/使用对应 Tunnel 的连接器。");
        }
        catch
        {
            SetState(RuntimeState.Faulted);
            await StopInternalAsync();
            throw;
        }
    }

    public async Task<int> DoctorAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        var tunnelClient = await _bootstrapper.EnsureTunnelClientAsync(config, cancellationToken);
        await _bootstrapper.CreateOrRefreshProfileAsync(config, tunnelClient, cancellationToken);
        return await DoctorAsync(config, tunnelClient, cancellationToken);
    }

    private async Task<int> DoctorAsync(
        AppConfig config,
        string tunnelClient,
        CancellationToken cancellationToken)
    {
        _log.Write("执行 tunnel-client doctor…");

        var env = BuildTunnelEnvironment(config);

        var result = await CommandRunner.RunAsync(
            tunnelClient,
            new[]
            {
                "doctor",
                "--profile", config.ProfileName,
                "--profile-dir", AppPaths.ProfilesDirectory,
                "--explain"
            },
            environment: env,
            log: _log,
            source: "doctor",
            cancellationToken: cancellationToken);

        return result.ExitCode;
    }

    public async Task StopAsync()
    {
        if (State == RuntimeState.Stopped)
            return;

        SetState(RuntimeState.Stopping);

        // Process.Kill(entireProcessTree: true), Job Object teardown and PID
        // discovery are OS calls that may occasionally block. Never execute
        // them on the WPF dispatcher thread.
        await Task.Run(StopInternalAsync).ConfigureAwait(false);

        SetState(RuntimeState.Stopped);
        _log.Write("服务已停止。");
    }

    private async Task StopInternalAsync()
    {
        _lifetimeCts?.Cancel();

        // Closing the Job Object is a synchronous OS-level safety net.
        _processJob?.Dispose();
        _processJob = null;

        await StopProcessAsync(_tunnelProcess, "tunnel-client").ConfigureAwait(false);
        _tunnelProcess = null;
        _tunnelReadySignal = null;
        TunnelReady = false;

        if (_ownsMcp)
        {
            await StopProcessAsync(_mcpProcess, "Windows-MCP 启动器").ConfigureAwait(false);
            await StopOwnedMcpListenerAsync(_ownedMcpPort, _mcpListenerPid).ConfigureAwait(false);
        }

        _mcpProcess = null;
        _ownsMcp = false;
        _ownedMcpPort = null;
        _mcpListenerPid = null;
        McpReady = false;
        HealthChanged?.Invoke();

        _lifetimeCts?.Dispose();
        _lifetimeCts = null;
    }

    private Process StartWindowsMcp(
        string runnerFileName,
        IReadOnlyList<string> runnerPrefixArgs,
        AppConfig config,
        CancellationToken cancellationToken)
    {
        var args = new List<string>();
        args.AddRange(runnerPrefixArgs);
        args.AddRange(new[]
        {
            "--python", config.PythonVersion,
            config.WindowsMcpSpec,
            "serve",
            "--transport", "streamable-http",
            "--host", "127.0.0.1",
            "--port", config.McpPort.ToString()
        });

        _log.Write("完整工具模式：不传递 --exclude-tools，Windows-MCP 将暴露其全部可用工具。");
        _log.Write(runnerPrefixArgs.Count == 0
            ? $"使用 uvx 启动 Windows-MCP：{runnerFileName}"
            : $"使用 uv tool run 启动 Windows-MCP：{runnerFileName}");
        _log.Write("正在启动 Windows-MCP…");
        var env = ProxyResolver.BuildNetworkEnvironment(config);
        return StartLongRunningProcess(runnerFileName, args, env, "windows-mcp", cancellationToken);
    }

    private Dictionary<string, string?> BuildTunnelEnvironment(AppConfig config)
    {
        var env = new Dictionary<string, string?>
        {
            ["CONTROL_PLANE_API_KEY"] = config.RuntimeApiKey
        };

        var proxy = ProxyResolver.ResolveControlPlaneProxy(config);
        _log.Write(proxy.Description);

        if (proxy.HasProxy)
        {
            env["CONTROL_PLANE_HTTP_PROXY"] = proxy.ProxyUrl;
            env["HTTP_PROXY"] = proxy.ProxyUrl;
            env["HTTPS_PROXY"] = proxy.ProxyUrl;
            env["http_proxy"] = proxy.ProxyUrl;
            env["https_proxy"] = proxy.ProxyUrl;
        }
        else
            env["CONTROL_PLANE_HTTP_PROXY"] = null;

        return env;
    }
    private Process StartTunnel(string tunnelClient, AppConfig config, CancellationToken cancellationToken)
    {
        var env = BuildTunnelEnvironment(config);

        _log.Write("正在启动 OpenAI Secure MCP Tunnel…");
        return StartLongRunningProcess(
            tunnelClient,
            new[]
            {
                "run",
                "--profile", config.ProfileName,
                "--profile-dir", AppPaths.ProfilesDirectory
            },
            env,
            "tunnel",
            cancellationToken);
    }

    private Process StartLongRunningProcess(
        string fileName,
        IEnumerable<string> arguments,
        IDictionary<string, string?>? environment,
        string source,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = AppPaths.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        if (environment is not null)
        {
            foreach (var kv in environment)
            {
                if (kv.Value is null) psi.Environment.Remove(kv.Key);
                else psi.Environment[kv.Key] = kv.Value;
            }
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;

            _log.Write(source, e.Data);

            if (source == "tunnel" &&
                e.Data.Contains("tunnel metadata fetched", StringComparison.OrdinalIgnoreCase))
            {
                _tunnelReadySignal?.TrySetResult(true);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;

            _log.Write(source, e.Data);

            if (source == "tunnel" &&
                e.Data.Contains("tunnel metadata fetched", StringComparison.OrdinalIgnoreCase))
            {
                _tunnelReadySignal?.TrySetResult(true);
            }
        };

        process.Exited += (_, _) =>
        {
            if (State is RuntimeState.Running or RuntimeState.Starting)
            {
                _log.Write($"{source} 进程已退出，退出码 {process.ExitCode}。");
                if (source == "tunnel") TunnelReady = false;
                if (source == "windows-mcp") McpReady = false;
                HealthChanged?.Invoke();
            }
        };

        if (!process.Start())
            throw new InvalidOperationException($"无法启动 {fileName}");

        // Assign the direct child before it can create long-lived descendants.
        // Windows propagates Job membership to child processes by default, so
        // uvx -> python/windows-mcp and tunnel-client descendants are covered.
        _processJob?.TryAssign(process, source);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();


        return process;
    }

    private async Task StopOwnedMcpListenerAsync(int? port, int? expectedPid)
    {
        if (port is null)
            return;

        var listenerPid = FindListeningProcessId(port.Value);
        if (listenerPid is null)
            return;

        if (expectedPid is int expected && listenerPid.Value != expected)
        {
            _log.Write($"端口 {port.Value} 的监听 PID 已从 {expected} 变为 {listenerPid.Value}，为避免误杀其他程序，不自动终止该进程。");
            return;
        }

        try
        {
            using var listener = Process.GetProcessById(listenerPid.Value);
            _log.Write($"正在停止 Windows-MCP 监听进程 PID {listenerPid.Value}…");
            listener.Kill(entireProcessTree: true);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await listener.WaitForExitAsync(cts.Token).ConfigureAwait(false); } catch { }
        }
        catch (ArgumentException)
        {
            // Process already exited.
        }
        catch (Exception ex)
        {
            _log.Write($"停止 Windows-MCP 监听进程时出现警告：{ex.Message}");
        }

        for (var i = 0; i < 20; i++)
        {
            if (!await IsPortOpenAsync(port.Value, TimeSpan.FromMilliseconds(150)).ConfigureAwait(false))
                return;
            await Task.Delay(100).ConfigureAwait(false);
        }

        _log.Write($"警告：停止后端口 {port.Value} 仍在监听，请检查是否有其他 Windows-MCP 实例。");
    }

    private static int? FindListeningProcessId(int port, int timeoutMilliseconds = 3000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netstat.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-ano");
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add("tcp");

            using var process = Process.Start(psi);
            if (process is null)
                return null;

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(timeoutMilliseconds))
            {
                try { process.Kill(); } catch { }
                return null;
            }

            foreach (var rawLine in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = rawLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5 || !parts[0].Equals("TCP", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!parts[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase))
                    continue;

                var localEndpoint = parts[1];
                var colon = localEndpoint.LastIndexOf(':');
                if (colon < 0 || !int.TryParse(localEndpoint[(colon + 1)..], out var localPort) || localPort != port)
                    continue;

                if (int.TryParse(parts[^1], out var pid) && pid > 0)
                    return pid;
            }
        }
        catch
        {
            // Best effort PID discovery. Port readiness checks remain the source of truth.
        }

        return null;
    }
    private static async Task WaitForPortAsync(int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var end = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsPortOpenAsync(port, TimeSpan.FromMilliseconds(500)))
                return;

            await Task.Delay(500, cancellationToken);
        }

        throw new TimeoutException($"等待 Windows-MCP 监听 127.0.0.1:{port} 超时。");
    }

    private static async Task<bool> IsPortOpenAsync(int port, TimeSpan timeout)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(timeout);
            await client.ConnectAsync("127.0.0.1", port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task StopProcessAsync(Process? process, string name)
    {
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await process.WaitForExitAsync(cts.Token).ConfigureAwait(false); } catch { }
            }
        }
        catch
        {
            // Best effort shutdown.
        }
        finally
        {
            process.Dispose();
        }
    }

    private void SetState(RuntimeState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    /// <summary>
    /// Synchronous emergency shutdown. This method never waits for child-process
    /// exit and is safe to call from the WPF UI thread when graceful cleanup
    /// exceeded its deadline.
    /// </summary>
    public void ForceStop()
    {
        try { _lifetimeCts?.Cancel(); } catch { }

        // KILL_ON_JOB_CLOSE is the primary guarantee: closing the handle asks the
        // Windows kernel to terminate every process in the owned process tree.
        try { _processJob?.Dispose(); } catch { }
        _processJob = null;

        EmergencyKill(_tunnelProcess);
        EmergencyKill(_mcpProcess);

        // Best-effort listener cleanup for environments where assigning the
        // launcher process to a Job Object was not permitted.
        if (_ownsMcp && _ownedMcpPort is int port)
        {
            var listenerPid = FindListeningProcessId(port, timeoutMilliseconds: 350);
            if (listenerPid is int pid && (_mcpListenerPid is null || pid == _mcpListenerPid))
            {
                try
                {
                    using var listener = Process.GetProcessById(pid);
                    listener.Kill(entireProcessTree: true);
                }
                catch { }
            }
        }

        try { _tunnelProcess?.Dispose(); } catch { }
        try { _mcpProcess?.Dispose(); } catch { }
        try { _lifetimeCts?.Dispose(); } catch { }

        _tunnelProcess = null;
        _mcpProcess = null;
        _lifetimeCts = null;
        _tunnelReadySignal = null;
        _ownsMcp = false;
        _ownedMcpPort = null;
        _mcpListenerPid = null;
        TunnelReady = false;
        McpReady = false;
    }

    public void Dispose() => ForceStop();

    private static void EmergencyKill(Process? process)
    {
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { }
    }}
