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

    public async Task InstallDependenciesAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (State is RuntimeState.Running or RuntimeState.Starting)
                throw new InvalidOperationException("请先停止服务，再安装或检查依赖。");
            var uv = _bootstrapper.FindUv();
            if (uv is null)
                await _bootstrapper.InstallUvWithWingetAsync(config, cancellationToken).ConfigureAwait(false);
            else
                _log.Write($"uv 已存在：{uv}");
            var runner = _bootstrapper.FindPythonToolRunner();
            if (runner is null)
                throw new InvalidOperationException("找不到 uvx.exe 或 uv tool run。安装 uv 后请重新启动程序。");
            var tunnelClient = await _bootstrapper.EnsureTunnelClientAsync(config, cancellationToken).ConfigureAwait(false);
            _log.Write($"tunnel-client 已可用：{tunnelClient}");
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task StartAsync(AppConfig config, CancellationToken cancellationToken = default)
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
                var healthFile = Path.Combine(AppPaths.RuntimeDirectory, $"tunnel-health-{Guid.NewGuid():N}.txt");
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
                    _mcpProcess = StartWindowsMcp(runner.Value.FileName, runner.Value.PrefixArgs, config, ct);
                    await WaitForPortAsync(config.McpPort, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
                    _log.Write($"本地 TCP 已监听 127.0.0.1:{config.McpPort}。");
                }

                await _bootstrapper.CreateOrRefreshProfileAsync(config, tunnelClient, ct).ConfigureAwait(false);
                var doctorExit = await DoctorAsync(config, tunnelClient, ct).ConfigureAwait(false);
                if (doctorExit != 0)
                    throw new InvalidOperationException($"Tunnel doctor 检查失败，退出码 {doctorExit}。请查看日志。");

                _tunnelReadySignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _tunnelProcess = StartTunnel(tunnelClient, config, ct);
                await WaitForTunnelReadyAsync(config.McpPort, healthFile, generation, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (!_health.MarkRunning(generation))
                    throw new InvalidOperationException("启动期间运行状态已改变；未将失效运行标记为成功。");
                PublishState();
                _monitorTask = Task.Run(() => MonitorHealthAsync(config.McpPort, healthFile, generation, ct));
                _log.Write("本地启动流程完成；健康检查不代表当前 ChatGPT 会话或写入工具已可用。不会自动重放工具调用。");
            }
            catch (Exception ex)
            {
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
    }

    private async Task WaitForTunnelReadyAsync(int port, string healthFile, long generation, CancellationToken ct)
    {
        using var probe = new TunnelHealthProbe();
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(60))
        {
            ct.ThrowIfCancellationRequested();
            var observation = await probe.CheckAsync(port, healthFile, ct).ConfigureAwait(false);
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
            if (_health.Fail(generation, $"健康监测异常（{ex.GetType().Name}），状态不再视为正常。"))
                PublishState();
        }
    }

    private Process StartWindowsMcp(string runnerFileName, IReadOnlyList<string> runnerPrefixArgs,
        AppConfig config, CancellationToken ct)
    {
        var args = new List<string>(runnerPrefixArgs);
        args.AddRange(new[]
        {
            "--python", config.PythonVersion, config.WindowsMcpSpec, "serve",
            "--transport", "streamable-http", "--host", "127.0.0.1", "--port", config.McpPort.ToString()
        });
        _log.Write("完整工具模式：不传递 --exclude-tools；ChatGPT 侧实际可用工具仍需单独核对。");
        return StartLongRunningProcess(runnerFileName, args,
            ProxyResolver.BuildNetworkEnvironment(config), "windows-mcp", ct);
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
        IDictionary<string, string?>? environment, string source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var generation = _health.Generation;
        var lifetime = _lifetimeCts;
        var readySignal = _tunnelReadySignal;
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
        void OnLine(object? sender, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            _log.Write(source, e.Data);
            if (source == "tunnel" && e.Data.Contains("tunnel metadata fetched", StringComparison.OrdinalIgnoreCase))
                readySignal?.TrySetResult(true); // Compatibility only, not a green health signal.
        }
        process.OutputDataReceived += OnLine;
        process.ErrorDataReceived += OnLine;
        process.Exited += (_, _) =>
        {
            var message = $"{source} 进程已退出。";
            try { message = $"{source} 进程已退出，退出码 {process.ExitCode}。"; }
            catch (InvalidOperationException) { }
            if (_health.Fail(generation, message))
            {
                // Cancel only the lifetime captured by this process, not a new run.
                try { lifetime?.Cancel(); } catch (ObjectDisposedException) { }
                readySignal?.TrySetCanceled();
                PublishState();
                _log.Write(message);
            }
        };
        try
        {
            lock (_processGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                ct.ThrowIfCancellationRequested();
                if (!process.Start()) throw new InvalidOperationException($"无法启动 {fileName}");
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
        catch
        {
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
