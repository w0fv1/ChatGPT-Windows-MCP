using System.Diagnostics;
using System.Net;
using System.Reflection;
using ChatGPTWindowsMcp;

internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static async Task<int> Main(string[] args)
    {
        // Children created only by the process-cleanup regression tests.
        if (args.Length > 0 && args[0] == "--child-emit")
        {
            Console.Out.WriteLine("stdout-complete");
            Console.Error.WriteLine("stderr-complete");
            return 0;
        }
        if (args.Length == 2 && args[0] == "--child-sleep")
        {
            await File.WriteAllTextAsync(args[1], Environment.ProcessId.ToString());
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }

        await Test("exit clears readiness and transitions Running to Faulted", () =>
        {
            var state = Running();
            Require(state.Fail(state.Generation, "test child exited"));
            Equal(RuntimeState.Faulted, state.Snapshot.State);
            Require(!state.Snapshot.McpReady && state.Snapshot.TunnelReady == false);
        });
        await Test("exit during startup cannot be overwritten by MarkRunning", () =>
        {
            var state = new RuntimeHealthState();
            var generation = state.BeginStart();
            Require(state.Fail(generation, "startup exit"));
            Require(!state.MarkRunning(generation));
        });
        await Test("late probe after Stop cannot revive the run", () =>
        {
            var state = Running();
            var generation = state.Generation;
            state.BeginStop();
            state.MarkStopped();
            Require(!state.Observe(generation, Observation(true, true)));
            Equal(RuntimeState.Stopped, state.Snapshot.State);
        });
        await Test("late old Exited callback cannot fault a new generation", () =>
        {
            var state = Running();
            var old = state.Generation;
            state.BeginStop();
            state.MarkStopped();
            var current = state.BeginStart();
            Require(state.MarkRunning(current));
            Require(!state.Fail(old, "stale process"));
            Equal(RuntimeState.Running, state.Snapshot.State);
        });
        await Test("Stop during startup invalidates MarkRunning", () =>
        {
            var state = new RuntimeHealthState();
            var generation = state.BeginStart();
            state.BeginStop();
            Require(!state.MarkRunning(generation));
            Require(!state.Observe(generation, Observation(true, true)));
        });
        await Test("unknown health is not displayed as connected", () =>
        {
            var state = Running();
            state.Observe(state.Generation, Observation(true, null));
            var title = RuntimeHealthState.RunningTitle(state.Snapshot);
            Require(title.Contains("未验证") && !title.Contains("已连接"));
        });
        await Test("local port failure overrides a successful readyz", () =>
        {
            var state = Running();
            state.Observe(state.Generation, Observation(false, true));
            Require(RuntimeHealthState.RunningTitle(state.Snapshot).Contains("异常"));
        });
        await Test("successful observation retains its timestamp", () =>
        {
            var state = Running();
            var observation = Observation(true, true);
            state.Observe(state.Generation, observation);
            Equal(observation.ObservedAt, state.Snapshot.ObservedAt!.Value);
        });
        await Test("ready URL accepts numeric IPv4/IPv6 loopback", () =>
        {
            foreach (var value in new[] { "http://127.0.0.1:1234/", "http://127.0.0.1:1234/healthz", "http://[::1]:1234/readyz" })
            {
                Require(TunnelHealthProbe.TryGetReadyUri(value, out var uri));
                Equal("/readyz", uri!.AbsolutePath);
            }
        });
        await Test("ready URL refuses remote hosts, credentials, query, fragments and paths", () =>
        {
            foreach (var value in new[]
            {
                "https://127.0.0.1:1234/", "http://192.0.2.10:1234/", "http://example.invalid/",
                "http://user:secret@127.0.0.1:1234/", "http://127.0.0.1:1234/?secret=1",
                "http://127.0.0.1:1234/#fragment", "http://127.0.0.1:1234/mcp", "not-a-url"
            }) Require(!TunnelHealthProbe.TryGetReadyUri(value, out _), value);
        });
        await Test("missing health file stays unknown without HTTP requests", async () =>
        {
            using var temp = new TempDirectory();
            var handler = new FakeHandler(HttpStatusCode.OK);
            using var probe = Probe(handler);
            var result = await probe.CheckAsync(8000, temp.File("missing.txt"), CancellationToken.None);
            Require(result.TunnelReady is null);
            Equal(0, handler.Requests);
        });
        await Test("readyz 200 is readiness, not ChatGPT session proof", async () =>
        {
            var result = await ProbeResponse(HttpStatusCode.OK);
            Require(result.TunnelReady == true && result.Detail.Contains("不是"));
        });
        await Test("readyz 503 is degraded", async () =>
        {
            var result = await ProbeResponse(HttpStatusCode.ServiceUnavailable);
            Require(result.TunnelReady == false);
        });
        await Test("unsupported readyz 404 stays unknown", async () =>
        {
            var result = await ProbeResponse(HttpStatusCode.NotFound);
            Require(result.TunnelReady is null);
        });
        await Test("readyz redirect is not success", async () =>
        {
            var result = await ProbeResponse(HttpStatusCode.Redirect);
            Require(result.TunnelReady == false);
        });
        await Test("untrusted health URL is rejected before HTTP", async () =>
        {
            using var temp = new TempDirectory();
            var path = temp.File("health.txt");
            await File.WriteAllTextAsync(path, "http://example.invalid/readyz");
            var handler = new FakeHandler(HttpStatusCode.OK);
            using var probe = Probe(handler);
            var result = await probe.CheckAsync(8000, path, CancellationToken.None);
            Require(result.TunnelReady == false);
            Equal(0, handler.Requests);
        });
        await Test("health cancellation propagates instead of reporting a timeout", async () =>
        {
            using var temp = new TempDirectory();
            var path = temp.File("health.txt");
            await File.WriteAllTextAsync(path, "http://127.0.0.1:1234/");
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            using var probe = Probe(new FakeHandler(HttpStatusCode.OK));
            await Throws<OperationCanceledException>(() => probe.CheckAsync(8000, path, cts.Token));
        });
        await Test("version selection is trimmed and normalized", () =>
        {
            Equal("latest", TunnelVersionPolicy.Normalize(" LATEST "));
            Equal("v1.2.3", TunnelVersionPolicy.Normalize("1.2.3"));
            Equal("v1.2.3-rc.1", TunnelVersionPolicy.Normalize("V1.2.3-rc.1"));
        });
        await Test("invalid version strings cannot become paths or download URLs", () =>
        {
            foreach (var value in new[] { "", " ", "../../evil", "v1.2.3/../evil", "v1.2.3?x", "latest\nother", "1.2" })
                ThrowsSync<ArgumentException>(() => TunnelVersionPolicy.Normalize(value));
        });
        await Test("fixed releases use isolated version directories", () =>
        {
            using var temp = new TempDirectory();
            var one = TunnelVersionPolicy.ReleaseDirectory(temp.Path, "v1.2.3");
            var two = TunnelVersionPolicy.ReleaseDirectory(temp.Path, "v1.2.4");
            Require(one != two && one != temp.Path && two != temp.Path);
            ThrowsSync<ArgumentException>(() => TunnelVersionPolicy.ReleaseDirectory(temp.Path, "latest"));
        });
        await Test("new receipt validates only the matching release", () =>
        {
            using var temp = Installed();
            Require(TunnelVersionPolicy.IsValidInstall(temp.Path, "v1.2.3"));
            Require(!TunnelVersionPolicy.IsValidInstall(temp.Path, "v1.2.4"));
        });
        await Test("changed EXE fails cache integrity", () =>
        {
            using var temp = Installed();
            File.AppendAllText(temp.File("tunnel-client.exe"), "changed");
            Require(!TunnelVersionPolicy.IsValidInstall(temp.Path, "v1.2.3"));
        });
        await Test("unrecorded companion fails cache integrity", () =>
        {
            using var temp = Installed();
            File.WriteAllText(temp.File("cloudflared.exe"), "unexpected companion");
            Require(!TunnelVersionPolicy.IsValidInstall(temp.Path, "v1.2.3"));
        });
        await Test("a bare EXE is not a verified versioned install", () =>
        {
            using var temp = new TempDirectory();
            File.WriteAllText(temp.File("tunnel-client.exe"), "fixture, not an executable");
            Require(!TunnelVersionPolicy.IsValidInstall(temp.Path, "v1.2.3"));
        });
        await Test("bad receipt JSON fails closed", () =>
        {
            using var temp = Installed();
            File.WriteAllText(temp.File("launcher-install.json"), "{not valid JSON");
            Require(!TunnelVersionPolicy.IsValidInstall(temp.Path, "v1.2.3"));
        });
        await Test("stdout and stderr are drained before returning", async () =>
        {
            var child = Child("--child-emit");
            var result = await CommandRunner.RunAsync(child.Executable, child.Arguments,
                timeout: TimeSpan.FromSeconds(10));
            Equal(0, result.ExitCode);
            Require(result.Output.Contains("stdout-complete") && result.Output.Contains("stderr-complete"));
        });
        await Test("pre-cancelled command does not start a child", async () =>
        {
            using var temp = new TempDirectory();
            var pidFile = temp.File("pid.txt");
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var child = Child("--child-sleep", pidFile);
            await Throws<OperationCanceledException>(() =>
                CommandRunner.RunAsync(child.Executable, child.Arguments, cancellationToken: cts.Token));
            Require(!File.Exists(pidFile));
        });
        await Test("cancelling an active command terminates its child", async () =>
        {
            using var temp = new TempDirectory();
            using var cts = new CancellationTokenSource();
            var pidFile = temp.File("pid.txt");
            var child = Child("--child-sleep", pidFile);
            var task = CommandRunner.RunAsync(child.Executable, child.Arguments,
                cancellationToken: cts.Token, timeout: TimeSpan.FromSeconds(15));
            try
            {
                var pid = await WaitForPid(pidFile);
                cts.Cancel();
                await Throws<OperationCanceledException>(() => task);
                await RequireExited(pid);
            }
            finally
            {
                cts.Cancel();
                try { await task; } catch (OperationCanceledException) { } catch (TimeoutException) { }
            }
        });
        await Test("command deadline is distinct from user cancellation", async () =>
        {
            using var temp = new TempDirectory();
            var pidFile = temp.File("pid.txt");
            var child = Child("--child-sleep", pidFile);
            var task = CommandRunner.RunAsync(child.Executable, child.Arguments,
                timeout: TimeSpan.FromSeconds(4));
            var pid = await WaitForPid(pidFile);
            await Throws<TimeoutException>(() => task);
            await RequireExited(pid);
        });
        await Test("PATH lookup does not launch where.exe", () =>
        {
            using var temp = new TempDirectory();
            var oldPath = Environment.GetEnvironmentVariable("PATH");
            var name = "launcher-fixture.exe";
            File.WriteAllText(temp.File(name), "fixture, never executed");
            try
            {
                Environment.SetEnvironmentVariable("PATH", temp.Path);
                Equal(System.IO.Path.GetFullPath(temp.File(name)), CommandRunner.FindOnPath(name)!);
            }
            finally { Environment.SetEnvironmentVariable("PATH", oldPath); }
        });

        Console.WriteLine($"Regression result: {_passed} passed; {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    private static RuntimeHealthState Running()
    {
        var state = new RuntimeHealthState();
        var generation = state.BeginStart();
        Require(state.Observe(generation, Observation(true, true)));
        Require(state.MarkRunning(generation));
        return state;
    }

    private static TunnelHealthObservation Observation(bool local, bool? tunnel) =>
        new(local, tunnel, "fixture", DateTimeOffset.UtcNow);

    private static TunnelHealthProbe Probe(FakeHandler handler) =>
        new(handler, (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult(true); });

    private static async Task<TunnelHealthObservation> ProbeResponse(HttpStatusCode status)
    {
        using var temp = new TempDirectory();
        var file = temp.File("health.txt");
        await File.WriteAllTextAsync(file, "http://127.0.0.1:1234/");
        var handler = new FakeHandler(status);
        using var probe = Probe(handler);
        var result = await probe.CheckAsync(8000, file, CancellationToken.None);
        Equal(1, handler.Requests);
        return result;
    }

    private static TempDirectory Installed()
    {
        var temp = new TempDirectory();
        File.WriteAllText(temp.File("tunnel-client.exe"), "fixture, not an executable");
        TunnelVersionPolicy.WriteReceipt(temp.Path, "v1.2.3");
        return temp;
    }

    private static (string Executable, List<string> Arguments) Child(params string[] args)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
        var list = new List<string>();
        if (System.IO.Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            list.Add(Assembly.GetExecutingAssembly().Location);
        list.AddRange(args);
        return (executable, list);
    }

    private static async Task<int> WaitForPid(string file)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(8))
        {
            if (File.Exists(file))
            {
                try { if (int.TryParse(await File.ReadAllTextAsync(file), out var pid)) return pid; }
                catch (IOException) { }
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("Regression child did not report a PID.");
    }

    private static async Task RequireExited(int pid)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            try { using var process = Process.GetProcessById(pid); if (process.HasExited) return; }
            catch (ArgumentException) { return; }
            await Task.Delay(50);
        }
        throw new InvalidOperationException($"Regression child {pid} survived cancellation.");
    }

    private static Task Test(string name, Action action) => Test(name, () => { action(); return Task.CompletedTask; });
    private static async Task Test(string name, Func<Task> action)
    {
        try { await action(); _passed++; Console.WriteLine($"PASS {name}"); }
        catch (Exception ex) { _failed++; Console.Error.WriteLine($"FAIL {name}\n{ex}"); }
    }
    private static void Require(bool condition, string? message = null)
    {
        if (!condition) throw new InvalidOperationException(message ?? "Assertion failed.");
    }
    private static void Equal<T>(T expected, T actual) =>
        Require(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
    private static void ThrowsSync<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class FakeHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Require(request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/readyz");
            Requests++;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "launcher-regression-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public string File(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }
}
