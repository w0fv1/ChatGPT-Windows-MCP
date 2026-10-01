using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ChatGPTWindowsMcp;

internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static async Task<int> Main(string[] args)
    {
        await Test("runtime paths cannot escape extraction directory", () =>
        {
            using var temp = new TempDirectory();
            foreach (var name in new[] { "../outside", "python/../../outside", "C:/outside", "/outside", "python\\outside", "python//outside" })
                ThrowsSync<InvalidDataException>(() => BundledRuntime.SafePath(temp.Path, name));
            Equal(Path.Combine(temp.Path, "python", "python.exe"), BundledRuntime.SafePath(temp.Path, "python/python.exe"));
        });
        await Test("only matching Python and MCP versions use the pinned bundle", () =>
        {
            Require(BundledRuntime.SupportsMcp("3.13", "windows-mcp"));
            Require(BundledRuntime.SupportsMcp(BundledRuntime.PythonVersion, "windows-mcp==" + BundledRuntime.McpVersion));
            Require(!BundledRuntime.SupportsMcp("3.14", "windows-mcp"));
            Require(!BundledRuntime.SupportsMcp("3.13", "windows-mcp==0.7.0"));
        });
        if (args.Length == 1 && args[0] == "--mcp-discovery-smoke")
        {
            using var probe = new McpConnectionProbe();
            var count = await probe.CheckAsync(8000, CancellationToken.None);
            Console.WriteLine($"Live loopback MCP initialize and tools/list passed: {count} tools; no tools executed.");
            return 0;
        }
        if (args.Length == 1 && args[0] == "--uv-download-smoke")
        {
            using var temp = new TempDirectory();
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var destination = temp.File("uv");
            await UvInstallation.InstallAsync(http, destination, Architecture.X64, deadline.Token);
            foreach (var name in new[] { "uv.exe", "uvx.exe" })
            {
                var executable = UvInstallation.FindExecutable(destination, name)!;
                Equal(System.IO.Path.Combine(destination, name), executable);
                var result = await CommandRunner.RunAsync(executable, new[] { "--version" },
                    cancellationToken: deadline.Token, timeout: TimeSpan.FromSeconds(10));
                Equal(0, result.ExitCode);
                Console.WriteLine(result.Output.Trim());
            }
            Console.WriteLine("Official uv download, checksum, and immediate execution passed.");
            return 0;
        }
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

        await Test("uv fallback installs both executables without PATH or WinGet", async () =>
        {
            using var temp = new TempDirectory();
            var destination = temp.File("uv");
            using var http = new HttpClient(new UvReleaseHandler(UvArchive("bin/uv.exe", "bin/uvx.exe")));
            await UvInstallation.InstallAsync(http, destination, Architecture.X64);
            Equal(System.IO.Path.Combine(destination, "uv.exe"), UvInstallation.FindExecutable(destination, "uv.exe")!);
            Equal(System.IO.Path.Combine(destination, "uvx.exe"), UvInstallation.FindExecutable(destination, "uvx.exe")!);
            Equal(2, Directory.GetFiles(destination).Length);
            Equal(0, Directory.GetDirectories(temp.Path, ".uv-install-*").Length);
        });
        await Test("uv checksum failure publishes no installation and cleans staging", async () =>
        {
            using var temp = new TempDirectory();
            using var http = new HttpClient(new UvReleaseHandler(UvArchive("uv.exe", "uvx.exe"), new string('0', 64)));
            await Throws<InvalidDataException>(() => UvInstallation.InstallAsync(http, temp.File("uv"), Architecture.X64));
            Require(!Directory.Exists(temp.File("uv")));
            Equal(0, Directory.GetDirectories(temp.Path, ".uv-install-*").Length);
        });
        await Test("uv incomplete or duplicate executables cannot leave a partial install", async () =>
        {
            using var temp = new TempDirectory();
            foreach (var entries in new[] { new[] { "uv.exe" }, new[] { "uv.exe", "other/uv.exe", "uvx.exe" } })
            {
                using var http = new HttpClient(new UvReleaseHandler(UvArchive(entries)));
                await Throws<InvalidDataException>(() => UvInstallation.InstallAsync(http, temp.File("uv"), Architecture.X64));
                Require(!Directory.Exists(temp.File("uv")));
                Equal(0, Directory.GetDirectories(temp.Path, ".uv-install-*").Length);
            }
        });
        await Test("uv cancellation during download cleans staging", async () =>
        {
            using var temp = new TempDirectory();
            using var cts = new CancellationTokenSource();
            using var http = new HttpClient(new UvReleaseHandler(UvArchive("uv.exe", "uvx.exe"), cancelDownload: cts.Cancel));
            await Throws<OperationCanceledException>(() => UvInstallation.InstallAsync(http, temp.File("uv"), Architecture.X64, cts.Token));
            Require(!Directory.Exists(temp.File("uv")));
            Equal(0, Directory.GetDirectories(temp.Path, ".uv-install-*").Length);
        });
        await Test("uv pre-cancellation makes no network requests", async () =>
        {
            using var temp = new TempDirectory();
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            using var http = new HttpClient(new UvReleaseHandler(UvArchive("uv.exe", "uvx.exe")));
            await Throws<OperationCanceledException>(() => UvInstallation.InstallAsync(http, temp.File("uv"), Architecture.X64, cts.Token));
            Require(!Directory.Exists(temp.File("uv")));
        });
        await Test("uv concurrent publication preserves an existing installation", async () =>
        {
            using var temp = new TempDirectory();
            var destination = temp.File("uv");
            Directory.CreateDirectory(destination);
            File.WriteAllText(System.IO.Path.Combine(destination, "uv.exe"), "existing uv");
            File.WriteAllText(System.IO.Path.Combine(destination, "uvx.exe"), "existing uvx");
            using var http = new HttpClient(new UvReleaseHandler(UvArchive("uv.exe", "uvx.exe")));
            await UvInstallation.InstallAsync(http, destination, Architecture.X64);
            Equal("existing uv", File.ReadAllText(System.IO.Path.Combine(destination, "uv.exe")));
            Equal(0, Directory.GetDirectories(temp.Path, ".uv-install-*").Length);
        });
        await Test("uv download chooses supported Windows architecture", () =>
        {
            Require(UvInstallation.AssetName(Architecture.X64).Contains("x86_64"));
            Require(UvInstallation.AssetName(Architecture.Arm64).Contains("aarch64"));
            Require(UvInstallation.AssetName(Architecture.X86).Contains("i686"));
            ThrowsSync<PlatformNotSupportedException>(() => UvInstallation.AssetName(Architecture.Arm));
        });

        await Test("logs are readable immediately and redact credentials in files and UI", () =>
        {
            using var temp = new TempDirectory();
            var path = temp.File("app.log");
            var log = new LogSink(() => path);
            log.ProtectSecret("fixture-runtime-key");
            string? displayed = null;
            log.LineReceived += value => displayed = value;
            using var reader = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite);
            log.Write("fixture", "fixture-runtime-key https://user:password@localhost:7890 sk-fixtureToken123");
            var contents = File.ReadAllText(path);
            Require(contents.Contains("[fixture]") && contents.Contains("[pid:"));
            Require(!contents.Contains("fixture-runtime-key") && !contents.Contains("password") && !contents.Contains("sk-fixtureToken123"));
            Require(displayed is not null && displayed == contents.TrimEnd('\r', '\n'));
        });
        await Test("logs preserve exception stack, inner cause and HResult", () =>
        {
            using var temp = new TempDirectory();
            var path = temp.File("exception.log");
            var log = new LogSink(() => path);
            try { throw new InvalidOperationException("outer failure", new IOException("inner failure")); }
            catch (Exception exception) { log.Error("failure", exception); }
            var contents = File.ReadAllText(path);
            Require(contents.Contains("outer failure") && contents.Contains("inner failure"));
            Require(contents.Contains("HResult=0x") && contents.Contains("Program.cs"));
            Require(contents.Split('\n', StringSplitOptions.RemoveEmptyEntries).All(line => line.StartsWith("[")));
        });
        await Test("concurrent log callbacks retain every line", async () =>
        {
            using var temp = new TempDirectory();
            var path = temp.File("concurrent.log");
            var log = new LogSink(() => path);
            await Task.WhenAll(Enumerable.Range(0, 60).Select(index => Task.Run(() => log.Write("fixture", $"line-{index}"))));
            Equal(60, File.ReadAllLines(path).Length);
        });
        await Test("log file failure still reports useful UI information", () =>
        {
            using var temp = new TempDirectory();
            var log = new LogSink(() => temp.Path); // A directory cannot be opened as the log file.
            string? displayed = null;
            log.LineReceived += value => displayed = value;
            log.Write("fixture", "diagnostic retained");
            Require(displayed is not null && displayed.Contains("日志文件写入失败") && displayed.Contains("diagnostic retained"));
        });
        await Test("command logs capture execution, streams and completion", async () =>
        {
            using var temp = new TempDirectory();
            var path = temp.File("process.log");
            var log = new LogSink(() => path);
            var child = Child("--child-emit");
            var result = await CommandRunner.RunAsync(child.Executable, child.Arguments, log: log, source: "fixture", timeout: TimeSpan.FromSeconds(10));
            Equal(0, result.ExitCode);
            var contents = File.ReadAllText(path);
            Require(contents.Contains("启动命令") && contents.Contains("PID=") && contents.Contains("退出码=0") && contents.Contains("耗时="));
            Require(contents.Contains("[fixture/stdout]") && contents.Contains("stdout-complete"));
            Require(contents.Contains("[fixture/stderr]") && contents.Contains("stderr-complete"));
        });

        await Test("plugin prompt encodes name and verified connection as data", () =>
        {
            var name = "我的 \"Windows\" 助手";
            var prompt = PluginCreationPrompt.Build(name, "tunnel_fixture", "plugin_asdk_app_fixture");
            var start = prompt.IndexOf('{');
            var end = prompt.IndexOf("\n}", start) + 2;
            using var json = System.Text.Json.JsonDocument.Parse(prompt[start..end]);
            Equal(name, json.RootElement.GetProperty("displayName").GetString()!);
            Equal("tunnel_fixture", json.RootElement.GetProperty("tunnelId").GetString()!);
            Equal("plugin_asdk_app_fixture", json.RootElement.GetProperty("registeredMcpAppId").GetString()!);
            Require(prompt.Contains("@Plugin Creator") && prompt.Contains("复用这个 Tunnel"));
            Require(!prompt.Contains("sk-") && !prompt.Contains("http://127.0.0.1"));
        });
        await Test("plugin prompt supports AI connection creation and rejects unrelated IDs", () =>
        {
            var prompt = PluginCreationPrompt.Build("Windows 助手", "tunnel_fixture", "");
            Require(prompt.Contains("\"registeredMcpAppId\": null") && prompt.Contains("添加 → 创建 MCP 应用"));
            ThrowsSync<ArgumentException>(() => PluginCreationPrompt.Build("", "tunnel_fixture", "plugin_asdk_app_fixture"));
            ThrowsSync<ArgumentException>(() => PluginCreationPrompt.Build("a\nb", "tunnel_fixture", "plugin_asdk_app_fixture"));
            ThrowsSync<ArgumentException>(() => PluginCreationPrompt.Build("助手", "https://example.com", "plugin_asdk_app_fixture"));
            ThrowsSync<ArgumentException>(() => PluginCreationPrompt.Build("助手", "tunnel_fixture", "tunnel_fixture"));
            ThrowsSync<ArgumentException>(() => PluginCreationPrompt.Build("助手", "tunnel_fixture", "plugins_fixture"));
        });
        await Test("connection links extract only a real ChatGPT app path ID", () =>
        {
            Require(PluginCreationPrompt.TryGetConnectionId(" https://chatgpt.com/plugins/plugin_asdk_app_fixture?tab=tools ", out var id));
            Equal("plugin_asdk_app_fixture", id);
            foreach (var input in new[] { "https://evil.example/plugin_asdk_app_fixture", "https://chatgpt.com/plugins/plugins_fixture", "https://chatgpt.com/plugins?app=plugin_asdk_app_fixture", "https://chatgpt.com/plugin_asdk_app_one/plugin_asdk_app_two", "https://chatgpt.com.evil.example/plugin_asdk_app_fixture", "https://user@chatgpt.com/plugin_asdk_app_fixture", "http://chatgpt.com/plugin_asdk_app_fixture" })
                Require(!PluginCreationPrompt.TryGetConnectionId(input, out _));
            var prompt = PluginCreationPrompt.Build("Windows 助手", "tunnel_fixture", "https://chatgpt.com/plugins/plugin_asdk_app_fixture");
            Require(prompt.Contains("不要创建占位插件") && prompt.Contains("其他连接的工具调用成功不算本插件验证成功"));
        });

        await Test("MCP discovery supports SSE initialization, JSON pagination and session cleanup without tool calls", async () =>
        {
            var handler = new McpProbeHandler();
            using var probe = new McpConnectionProbe(handler);
            Equal(2, await probe.CheckAsync(8000, CancellationToken.None));
            Require(handler.Methods.SequenceEqual(new[] { "initialize", "notifications/initialized", "tools/list", "tools/list", "DELETE" }));
        });
        await Test("MCP discovery rejects protocol errors, empty tools and duplicate definitions", async () =>
        {
            foreach (var mode in new[] { "rpc-error", "empty", "duplicate", "bad-schema", "wrong-id", "http-error" })
            {
                using var probe = new McpConnectionProbe(new McpProbeHandler(mode));
                await Throws<InvalidDataException>(() => probe.CheckAsync(8000, CancellationToken.None));
            }
        });

        await Test("bundled uv extracts verified files and repairs a changed executable", () =>
        {
            using var temp = new TempDirectory();
            var files = new[] { "uv.exe", "uvx.exe", "LICENSE-MIT", "LICENSE-APACHE" }
                .ToDictionary(name => name, name => System.Text.Encoding.UTF8.GetBytes("fixture-" + name));
            var hashes = files.ToDictionary(pair => pair.Key, pair => Convert.ToHexString(SHA256.HashData(pair.Value)));
            Stream Open(string name) => new MemoryStream(files[name]);
            BundledUv.Extract(temp.Path, hashes, Open);
            File.WriteAllText(temp.File("uv.exe"), "changed");
            BundledUv.Extract(temp.Path, hashes, Open);
            foreach (var file in files) Require(File.ReadAllBytes(temp.File(file.Key)).SequenceEqual(file.Value));
            Require(!Directory.GetFiles(temp.Path, "*.tmp").Any());
        });
        await Test("bundled uv rejects invalid manifest and mismatched binary before publication", () =>
        {
            using var temp = new TempDirectory();
            ThrowsSync<InvalidDataException>(() => BundledUv.Extract(temp.Path, new Dictionary<string, string>(), _ => new MemoryStream()));
            var hashes = new[] { "uv.exe", "uvx.exe", "LICENSE-MIT", "LICENSE-APACHE" }.ToDictionary(name => name, _ => new string('0', 64));
            ThrowsSync<InvalidDataException>(() => BundledUv.Extract(temp.Path, hashes, _ => new MemoryStream(new byte[] { 1, 2, 3 })));
            Require(!File.Exists(temp.File("uv.exe")) && !Directory.GetFiles(temp.Path, "*.tmp").Any());
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

    private static byte[] UvArchive(params string[] entries)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var name in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write("fixture executable, never run");
            }
        return bytes.ToArray();
    }

    private sealed class UvReleaseHandler(byte[] archive, string? checksum = null, Action? cancelDownload = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            Require(uri.Host == "github.com");
            if (uri.AbsolutePath == "/astral-sh/uv/releases/latest")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://github.com/astral-sh/uv/releases/tag/0.12.21")
                });
            }
            const string asset = "/astral-sh/uv/releases/download/0.12.21/uv-x86_64-pc-windows-msvc.zip";
            Require(uri.AbsolutePath == asset || uri.AbsolutePath == asset + ".sha256");
            if (uri.AbsolutePath == asset) cancelDownload?.Invoke();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = uri.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)
                    ? new StringContent((checksum ?? Convert.ToHexString(SHA256.HashData(archive))) + "  uv.zip\n")
                    : new ByteArrayContent(archive)
            });
        }
    }

    private sealed class McpProbeHandler(string mode = "ok") : HttpMessageHandler
    {
        public List<string> Methods { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Require(request.RequestUri!.ToString() == "http://127.0.0.1:8000/mcp");
            if (request.Method == HttpMethod.Delete)
            {
                Require(request.Headers.GetValues("Mcp-Session-Id").Single() == "fixture-session");
                Methods.Add("DELETE");
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            using var document = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = document.RootElement;
            var method = root.GetProperty("method").GetString()!;
            Methods.Add(method);
            Require(method != "tools/call");
            if (method == "initialize")
            {
                var init = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("data: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/message\"}\n\ndata: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"protocolVersion\":\"2025-03-26\"}}\n\n", System.Text.Encoding.UTF8, "text/event-stream")
                };
                init.Headers.Add("Mcp-Session-Id", "fixture-session");
                return init;
            }
            Require(request.Headers.GetValues("Mcp-Session-Id").Single() == "fixture-session");
            if (method == "notifications/initialized") return new HttpResponseMessage(HttpStatusCode.Accepted);
            Require(method == "tools/list");
            var id = root.GetProperty("id").GetInt32();
            if (mode == "http-error") return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            var json = mode switch
            {
                "rpc-error" => $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"error\":{{\"code\":-32601}}}}",
                "empty" => $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"tools\":[]}}}}",
                "bad-schema" => $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"tools\":[{{\"name\":\"one\",\"inputSchema\":null}}]}}}}",
                "wrong-id" => "{\"jsonrpc\":\"2.0\",\"id\":99,\"result\":{\"tools\":[]}}",
                _ => System.Text.Json.JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0", id, result = new
                    {
                        tools = new[] { new { name = id == 2 || mode == "duplicate" ? "one" : "two", inputSchema = new { type = "object" } } },
                        nextCursor = id == 2 ? "page2" : null
                    }
                })
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        }
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
