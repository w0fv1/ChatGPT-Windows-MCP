using System.Diagnostics;
using System.Net.Sockets;

namespace ChatGPTWindowsMcp;

internal sealed class RuntimeManager : IDisposable
{
    private readonly LogSink _log;
    private readonly Bootstrapper _bootstrapper;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _processGate = new();
    private readonly RuntimeHealthState _health = new();

    private Process? _mcpProcess;
    private Process? _tunnelProcess;
    private bool _ownsMcp;
    private int? _ownedMcpPort;
    private CancellationTokenSource? _lifetimeCts;
    private TaskCompletionSource<bool>? _tunnelReadySignal;
    private OwnedProcessJob? _processJob;
    private Task? _monitorTask;
    private string? _healthUrlFile;
    private volatile bool _disposed;
    private string? _activeTunnelId;
    public string? ActiveTunnelId => Volatile.Read(ref _activeTunnelId);

    public RuntimeHealthSnapshot Health => _health.Snapshot;
    public RuntimeState State => Health.State;
    public bool McpReady => Health.McpReady;
    public bool TunnelReady => Health.TunnelReady == true;
    public event Action<RuntimeState>? StateChanged;
    public event Action? HealthChanged;

    public RuntimeManager(LogSink log)
    {
        _log = log;
        _bootstrapper = new Bootstrapper(log);
    }

    public async Task InstallDependenciesAsync(AppConfig config, CancellationToken cancellationToken = default,
        IProgress<(int Percent, string Detail)>? progress = null)
    {
        _log.ProtectSecret(config.RuntimeApiKey);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var phase = 5;
        void OnActivity(string detail) => progress?.Report((phase, detail));
        void OnBundleProgress(int percent, string detail) => progress?.Report((35 + percent * 20 / 100, detail));
        _bootstrapper.ActivityChanged += OnActivity;
        _bootstrapper.BundleProgressChanged += OnBundleProgress;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            progress?.Report((5, "正在检查运行环境…"));
            if (State is RuntimeState.Running or RuntimeState.Starting)
                throw new InvalidOperationException("请先停止服务，再安装或检查依赖。");
            var existingRunner = await Task.Run(_bootstrapper.FindPythonToolRunner, cancellationToken).ConfigureAwait(false);
            if (existingRunner is null)
            {
                phase = 10;
                progress?.Report((10, "正在安装 uv 运行工具…"));
                await _bootstrapper.EnsureUvAsync(config, cancellationToken).ConfigureAwait(false);
            }
            else
                _log.Write($"uv/uvx 已存在：{existingRunner.Value.FileName}");
            var runner = _bootstrapper.FindPythonToolRunner();
            if (runner is null)
                throw new InvalidOperationException("uv 安装完成后仍找不到 uvx.exe 或 uv tool run。请查看安装日志。");
            progress?.Report((30, "uv 运行工具已就绪"));
            progress?.Report((35, "正在校验并释放内置运行环境…"));
            phase = 35;
            await Task.Run(_bootstrapper.PrepareBundledRuntime, cancellationToken).ConfigureAwait(false);
            var tunnelClient = await _bootstrapper.EnsureTunnelClientAsync(config, cancellationToken).ConfigureAwait(false);
            _log.Write($"tunnel-client 已可用：{tunnelClient}");
            progress?.Report((55, "运行环境已准备好"));
        }
        finally { _bootstrapper.ActivityChanged -= OnActivity; _bootstrapper.BundleProgressChanged -= OnBundleProgress; _lifecycleGate.Release(); }
    }

    public async Task StartAsync(AppConfig config, CancellationToken cancellationToken = default,
        IProgress<(int Percent, string Detail)>? progress = null)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (State is RuntimeState.Starting or RuntimeState.Running) return;
            var errors = config.Validate();
            if (errors.Count != 0) throw new ArgumentException(string.Join(Environment.NewLine, errors));

            // Fence old callbacks before cleanup. A concurrent Stop invalidates
            // this generation even if no new lifetime CTS has been assigned yet.
            var generation = _health.BeginStart();
            PublishState();
            try
            {
                await Task.Run(StopInternalAsync).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(AppPaths.RuntimeDirectory);
                var healthFile = Path.Combine(AppPaths.RuntimeDirectory, $"tunnel-health-{Guid.NewGuid():N}.txt");
                _log.Write("startup", $"健康地址文件：{healthFile}");
                CancellationToken ct;
                lock (_processGate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (_health.Generation != generation || State != RuntimeState.Starting)
                        throw new OperationCanceledException("启动已被停止请求取消。");
                    _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    ct = _lifetimeCts.Token;
                    _processJob = OwnedProcessJob.TryCreate(_log);
                    _healthUrlFile = healthFile;
                }
                var runner = _bootstrapper.FindPythonToolRunner();
                if (runner is null) throw new InvalidOperationException("未找到 uvx.exe 或 uv.exe。请先安装依赖。");
                var tunnelClient = await _bootstrapper.EnsureTunnelClientAsync(config, ct).ConfigureAwait(false);
                progress?.Report((60, "正在启动 Windows-MCP 服务…"));

                if (await IsPortOpenAsync(config.McpPort, TimeSpan.FromMilliseconds(500)).ConfigureAwait(false))
                {
                    if (!config.ReuseExistingMcp)
                        throw new InvalidOperationException($"端口 {config.McpPort} 已被占用。请停止现有服务或开启复用。");
                    _ownsMcp = false;
                    _log.Write($"复用 127.0.0.1:{config.McpPort}；端口开放不是服务身份验证，随后仍需 doctor 检查。");
                }
                else
                {
                    _ownsMcp = true;
                    _ownedMcpPort = config.McpPort;
                    _mcpProcess = StartWindowsMcp(runner.Value.FileName, runner.Value.PrefixArgs, config, ct, progress);
                    await WaitForPortAsync(config.McpPort, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
                    _log.Write($"本地 TCP 已监听 127.0.0.1:{config.McpPort}。");
                }

                progress?.Report((70, "正在验证 MCP 工具列表…"));
                using (var mcpProbe = new McpConnectionProbe())
                {
                    var toolCount = await mcpProbe.CheckAsync(config.McpPort, ct).ConfigureAwait(false);
                    _log.Write("mcp-discovery", $"本机 MCP initialize 和 tools/list 成功；工具数={toolCount}；未执行工具。");
                }
                progress?.Report((80, "正在准备 Tunnel 配置…"));
                await _bootstrapper.CreateOrRefreshProfileAsync(config, tunnelClient, ct).ConfigureAwait(false);
                progress?.Report((85, "正在检查 Tunnel 连接…"));
                var doctorExit = await DoctorAsync(config, tunnelClient, ct).ConfigureAwait(false);
                if (doctorExit != 0)
                    throw new InvalidOperationException($"Tunnel doctor 检查失败，退出码 {doctorExit}。请查看日志。");

                _tunnelReadySignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _tunnelProcess = StartTunnel(tunnelClient, config, ct);
                progress?.Report((90, "正在等待 Tunnel 就绪…"));
                await WaitForTunnelReadyAsync(config.McpPort, healthFile, generation, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (!_health.MarkRunning(generation))
                    throw new InvalidOperationException("启动期间运行状态已改变；未将失效运行标记为成功。");
                Volatile.Write(ref _activeTunnelId, config.TunnelId.Trim());
                PublishState();
                _monitorTask = Task.Run(() => MonitorHealthAsync(config.McpPort, healthFile, generation, ct));
                _log.Write("本地启动流程完成；健康检查不代表当前 ChatGPT 会话或写入工具已可用。不会自动重放工具调用。");
                progress?.Report((100, "运行环境和本机服务已就绪"));
            }
            catch (Exception ex)
            {
                _log.Error("startup", ex);
                _health.Fail(generation, cancellationToken.IsCancellationRequested
                    ? "启动已取消。" : $"启动失败：{ex.Message}");
                PublishState();
                await Task.Run(StopInternalAsync).ConfigureAwait(false);
                if (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested && !_disposed)
                    throw new InvalidOperationException(Health.Detail, ex);
                throw;
            }
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task<int> DoctorAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (State is RuntimeState.Running or RuntimeState.Starting or RuntimeState.Stopping)
                throw new InvalidOperationException("运行时请查看健康状态；请先停止服务，再执行会重新生成 Profile 的 doctor 诊断。");
            var tunnelClient = await _bootstrapper.EnsureTunnelClientAsync(config, cancellationToken).ConfigureAwait(false);
            await _bootstrapper.CreateOrRefreshProfileAsync(config, tunnelClient, cancellationToken).ConfigureAwait(false);
            return await DoctorAsync(config, tunnelClient, cancellationToken).ConfigureAwait(false);
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task<string> CheckRegistrationAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State != RuntimeState.Running || _healthUrlFile is null)
                throw new InvalidOperationException("本机服务尚未运行，请在插件页点击“创建 MCP 应用”自动启动并重试。");
            if (!string.Equals(ActiveTunnelId, config.TunnelId.Trim(), StringComparison.Ordinal))
                throw new InvalidOperationException($"配置中的 Tunnel 与正在运行的 Tunnel 不一致。当前运行：{ActiveTunnelId}。请停止后重新启动，并使用同一个 ID 创建连接。");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts!.Token);
            using var healthProbe = new TunnelHealthProbe();
            var observation = await healthProbe.CheckAsync(config.McpPort, _healthUrlFile, linked.Token).ConfigureAwait(false);
            if (!observation.McpPortOpen || observation.TunnelReady != true)
                throw new InvalidOperationException($"Tunnel 尚未确认就绪：{observation.Detail}");
            using var mcpProbe = new McpConnectionProbe();
            var toolCount = await mcpProbe.CheckAsync(config.McpPort, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            var summary = $"本机 MCP 初始化和工具列表检查通过（{toolCount} 个工具）；当前 Tunnel：{ActiveTunnelId}。";
            _log.Write("registration-check", summary);
            return summary;
        }
        catch (Exception ex) { _log.Error("registration-check", ex); throw; }
        finally { _lifecycleGate.Release(); }
    }

    private async Task<int> DoctorAsync(AppConfig config, string tunnelClient, CancellationToken cancellationToken)
    {
        _log.Write("执行 tunnel-client doctor…");
        var result = await CommandRunner.RunAsync(tunnelClient,
            new[] { "doctor", "--profile", config.ProfileName, "--profile-dir", AppPaths.ProfilesDirectory, "--explain" },
            environment: BuildTunnelEnvironment(config), log: _log, source: "doctor",
            cancellationToken: cancellationToken, timeout: TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        return result.ExitCode;
    }

    public async Task StopAsync()
    {
        // Invalidate in-flight observations before waiting for a starting run.
        _health.BeginStop();
        PublishState();
        CancelLifetime();
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(StopInternalAsync).ConfigureAwait(false);
            _health.MarkStopped();
            PublishState();
            _log.Write("服务已停止。");
        }
        finally { _lifecycleGate.Release(); }
    }

    private async Task StopInternalAsync()
    {
        Volatile.Write(ref _activeTunnelId, null);
        var lifetime = Interlocked.Exchange(ref _lifetimeCts, null);
        try { lifetime?.Cancel(); } catch (ObjectDisposedException) { }
        var monitor = Interlocked.Exchange(ref _monitorTask, null);
        if (monitor is not null)
        {
            try { await monitor.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        Interlocked.Exchange(ref _processJob, null)?.Dispose();
        await StopProcessAsync(Interlocked.Exchange(ref _tunnelProcess, null)).ConfigureAwait(false);
        _tunnelReadySignal = null;
        if (_ownsMcp)
            await StopProcessAsync(Interlocked.Exchange(ref _mcpProcess, null)).ConfigureAwait(false);
        _mcpProcess = null;

        // A port is not proof of ownership. Never discover an arbitrary PID and
        // kill it just because it now owns the old port.
        if (_ownsMcp && _ownedMcpPort is int port &&
            await IsPortOpenAsync(port, TimeSpan.FromMilliseconds(150)).ConfigureAwait(false))
            _log.Write($"停止后端口 {port} 仍在监听；未终止无法确认归属的进程，请手动核对。");
        _ownsMcp = false;
        _ownedMcpPort = null;
        lifetime?.Dispose();
        var healthFile = Interlocked.Exchange(ref _healthUrlFile, null);
        try { if (healthFile is not null) File.Delete(healthFile); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        AppPaths.RemoveEmptyWorkingDirectories();
    }

    private async Task WaitForTunnelReadyAsync(int port, string healthFile, long generation, CancellationToken ct)
    {
        using var probe = new TunnelHealthProbe();
        var timer = Stopwatch.StartNew();
        string? lastDetail = null;
        while (timer.Elapsed < TimeSpan.FromSeconds(60))
        {
            ct.ThrowIfCancellationRequested();
            var observation = await probe.CheckAsync(port, healthFile, ct).ConfigureAwait(false);
            if (lastDetail != observation.Detail)
            {
                _log.Write("startup-health", observation.Detail);
                lastDetail = observation.Detail;
            }
            if (_health.Observe(generation, observation)) HealthChanged?.Invoke();
            if (observation.McpPortOpen && observation.TunnelReady == true) return;
            if (observation.McpPortOpen && observation.TunnelReady is null &&
                _tunnelReadySignal?.Task.IsCompletedSuccessfully == true)
            {
                _log.Write("已观察到隧道元数据日志，但无可用健康接口；以未验证状态运行，不显示已连接。");
                return;
            }
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        throw new TimeoutException("隧道启动就绪检查超时；请查看本地端口、代理及 tunnel 日志。");
    }

    private async Task MonitorHealthAsync(int port, string healthFile, long generation, CancellationToken ct)
    {
        using var probe = new TunnelHealthProbe();
        string? lastDetail = null;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var observation = await probe.CheckAsync(port, healthFile, ct).ConfigureAwait(false);
                if (!_health.Observe(generation, observation)) return;
                HealthChanged?.Invoke();
                if (lastDetail != observation.Detail)
                {
                    _log.Write("health", observation.Detail);
                    lastDetail = observation.Detail;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log.Error("health", ex);
            if (_health.Fail(generation, $"健康监测异常（{ex.GetType().Name}），状态不再视为正常。"))
                PublishState();
        }
    }

    private Process StartWindowsMcp(string runnerFileName, IReadOnlyList<string> runnerPrefixArgs,
        AppConfig config, CancellationToken ct, IProgress<(int Percent, string Detail)>? progress)
    {
        var args = new List<string>(runnerPrefixArgs);
        var bundled = BundledRuntime.SupportsMcp(config.PythonVersion, config.WindowsMcpSpec) ? _bootstrapper.PrepareBundledRuntime() : null;
        if (bundled is not null)
        {
            runnerFileName = Path.Combine(bundled, "python", "python.exe");
            args.Clear();
            args.Add(Path.Combine(bundled, "launch-mcp.py"));
            _log.Write("windows-mcp", $"使用内置 Python {BundledRuntime.PythonVersion} / Windows-MCP {BundledRuntime.McpVersion}；不会下载依赖。");
        }
        else args.AddRange(new[] { "--python", config.PythonVersion, config.WindowsMcpSpec });
        args.AddRange(new[]
        {
            "serve",
            "--transport", "streamable-http", "--host", "127.0.0.1", "--port", config.McpPort.ToString()
        });
        _log.Write("完整工具模式：不传递 --exclude-tools；ChatGPT 侧实际可用工具仍需单独核对。");
        var environment = ProxyResolver.BuildNetworkEnvironment(config);
        environment["PYTHONUNBUFFERED"] = "1";
        environment["NO_COLOR"] = "1";
        if (bundled is not null)
        {
            // Ignore global Python configuration and packages on the user's computer.
            environment["PYTHONHOME"] = Path.Combine(bundled, "python");
            environment["PYTHONPATH"] = null;
            environment["PYTHONNOUSERSITE"] = "1";
            environment["PYTHONDONTWRITEBYTECODE"] = "1";
        }
        return StartLongRunningProcess(runnerFileName, args, environment, "windows-mcp", ct, progress);
    }

    private Dictionary<string, string?> BuildTunnelEnvironment(AppConfig config)
    {
        var env = new Dictionary<string, string?> { ["CONTROL_PLANE_API_KEY"] = config.RuntimeApiKey };
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
        else env["CONTROL_PLANE_HTTP_PROXY"] = null;
        return env;
    }

    private Process StartTunnel(string tunnelClient, AppConfig config, CancellationToken ct)
    {
        var env = BuildTunnelEnvironment(config);
        // init already sets a loopback ephemeral health listener. Newer clients
        // write its base URL here; older clients may ignore this environment key.
        env["HEALTH_URL_FILE"] = _healthUrlFile;
        return StartLongRunningProcess(tunnelClient,
            new[] { "run", "--profile", config.ProfileName, "--profile-dir", AppPaths.ProfilesDirectory },
            env, "tunnel", ct);
    }

    private Process StartLongRunningProcess(string fileName, IEnumerable<string> arguments,
        IDictionary<string, string?>? environment, string source, CancellationToken ct,
        IProgress<(int Percent, string Detail)>? progress = null)
    {
        ct.ThrowIfCancellationRequested();
        var generation = _health.Generation;
        var lifetime = _lifetimeCts;
        var readySignal = _tunnelReadySignal;
        var duration = Stopwatch.StartNew();
        var psi = new ProcessStartInfo
        {
            FileName = fileName, WorkingDirectory = AppPaths.BaseDirectory,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);
        if (environment is not null)
            foreach (var kv in environment)
                if (kv.Value is null) psi.Environment.Remove(kv.Key);
                else psi.Environment[kv.Key] = kv.Value;

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        void ReportActivity(string? line)
        {
            if (source != "windows-mcp" || line is null) return;
            if (line.Contains("cpython", StringComparison.OrdinalIgnoreCase) && line.Contains("Downloading", StringComparison.OrdinalIgnoreCase))
                progress?.Report((60, "正在下载 Python 运行环境…"));
            else if (line.Contains("Downloading", StringComparison.OrdinalIgnoreCase))
                progress?.Report((60, "正在下载 Windows-MCP 及依赖包…"));
            else if (line.Contains("Installed ", StringComparison.OrdinalIgnoreCase))
                progress?.Report((60, "依赖安装完成，正在启动 Windows-MCP…"));
            else if (line.Contains("Building ", StringComparison.OrdinalIgnoreCase))
                progress?.Report((60, "正在构建 Windows-MCP 依赖…"));
            else if (line.Contains("Uvicorn running", StringComparison.OrdinalIgnoreCase) || line.Contains("Application startup complete", StringComparison.OrdinalIgnoreCase))
                progress?.Report((60, "Windows-MCP 已启动，正在检查本机接口…"));
        }
        _log.Command(source, psi);
        void OnLine(object? sender, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            _log.Write(source + "/stdout", e.Data);
            ReportActivity(e.Data);
            if (source == "tunnel" && e.Data.Contains("tunnel metadata fetched", StringComparison.OrdinalIgnoreCase))
                readySignal?.TrySetResult(true); // Compatibility only, not a green health signal.
        }
        process.OutputDataReceived += OnLine;
        process.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data is not null) _log.Write(source + "/stderr", e.Data);
            ReportActivity(e.Data);
            if (source == "tunnel" && e.Data?.Contains("tunnel metadata fetched", StringComparison.OrdinalIgnoreCase) == true)
                readySignal?.TrySetResult(true);
        };
        process.Exited += (_, _) =>
        {
            var message = $"{source} 进程已退出。";
            try { message = $"{source} 进程已退出，退出码 {process.ExitCode}。"; }
            catch (InvalidOperationException) { }
            _log.Write(source, $"{message} 运行耗时={duration.Elapsed.TotalSeconds:F2} 秒。");
            if (_health.Fail(generation, message))
            {
                // Cancel only the lifetime captured by this process, not a new run.
                try { lifetime?.Cancel(); } catch (ObjectDisposedException) { }
                readySignal?.TrySetCanceled();
                PublishState();
            }
        };
        try
        {
            lock (_processGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                ct.ThrowIfCancellationRequested();
                if (!process.Start()) throw new InvalidOperationException($"无法启动 {fileName}");
                _log.Write(source, $"进程已启动；PID={process.Id}。");
                // Register the owned handle before ForceStop can collect it.
                if (source == "tunnel") _tunnelProcess = process;
                else _mcpProcess = process;
                // Best-effort immediate job assignment; tracked process trees remain
                // the fallback if the OS rejects assignment. Do not infer PIDs by port.
                _processJob?.TryAssign(process, source);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                return process;
            }
        }
        catch (Exception ex)
        {
            _log.Error(source, ex);
            EmergencyKill(process);
            process.Dispose();
            throw;
        }
    }

    private static async Task WaitForPortAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            if (await IsPortOpenAsync(port, TimeSpan.FromMilliseconds(500)).ConfigureAwait(false)) return;
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        throw new TimeoutException($"等待 Windows-MCP 监听 127.0.0.1:{port} 超时。");
    }

    private static async Task<bool> IsPortOpenAsync(int port, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (SocketException) { return false; }
        catch (OperationCanceledException) { return false; }
    }

    private static async Task StopProcessAsync(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException) { }
        finally { process.Dispose(); }
    }

    private void PublishState()
    {
        StateChanged?.Invoke(State);
        HealthChanged?.Invoke();
    }

    private void CancelLifetime()
    {
        try { Volatile.Read(ref _lifetimeCts)?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Terminal emergency cleanup. Call off the WPF dispatcher.</summary>
    public void ForceStop()
    {
        OwnedProcessJob? job;
        Process? tunnel;
        Process? mcp;
        lock (_processGate)
        {
            _disposed = true;
            _health.BeginStop();
            job = Interlocked.Exchange(ref _processJob, null);
            tunnel = Interlocked.Exchange(ref _tunnelProcess, null);
            mcp = Interlocked.Exchange(ref _mcpProcess, null);
        }
        CancelLifetime();
        try { job?.Dispose(); } catch { }
        // No port-to-PID fallback: never terminate a process of unknown ownership.
        EmergencyKill(tunnel);
        EmergencyKill(mcp);
        _health.MarkStopped();
    }

    public void Dispose() => ForceStop();

    private static void EmergencyKill(Process? process)
    {
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        finally { process.Dispose(); }
    }
}
