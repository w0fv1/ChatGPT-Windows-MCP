using System.IO;
using ChatGPTWindowsMcp;
class BundleSmoke
{
    static void Main()
    {
            var assembly = typeof(MainWindow).Assembly;
            var type = assembly.GetType("ChatGPTWindowsMcp.BundledRuntime")!;
            var destination = Path.Combine(AppContext.BaseDirectory, "full-runtime-smoke-" + Guid.NewGuid().ToString("N"));
            var updates = new List<int>();
            Action<int, string> progress = (percent, detail) => { updates.Add(percent); if (string.IsNullOrWhiteSpace(detail)) throw new Exception("Missing progress detail"); };
            var root = (string?)type.GetMethod("Install")!.Invoke(null, new object[] { destination, progress }) ?? throw new Exception("Missing embedded runtime");
            if (updates.Count < 20 || updates[^1] != 100 || !updates.SequenceEqual(updates.OrderBy(x => x))) throw new Exception("Extraction progress must advance monotonically to completion");
            Console.WriteLine($"Extraction reported {updates.Count} real progress updates.");
            // A new launcher process must repair damaged bundled files before executing them.
            File.WriteAllText(Path.Combine(root, "launch-mcp.py"), "damaged fixture");
            ((System.Collections.IDictionary)type.GetField("Installed", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!).Clear();
            type.GetMethod("TryInstall")!.Invoke(null, new object[] { destination });
            Console.WriteLine("Corrupted embedded launch script repaired before startup.");
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start(); var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var info = new System.Diagnostics.ProcessStartInfo(Path.Combine(root, "python", "python.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var item in new[] { Path.Combine(root, "launch-mcp.py"), "serve", "--transport", "streamable-http", "--host", "127.0.0.1", "--port", port.ToString() }) info.ArgumentList.Add(item);
            info.Environment["PATH"] = "";
            info.Environment["PYTHONHOME"] = Path.Combine(root, "python");
            info.Environment["PYTHONPATH"] = "";
            info.Environment["PYTHONNOUSERSITE"] = "1";
            info.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
            info.Environment["HTTP_PROXY"] = "http://127.0.0.1:1";
            info.Environment["HTTPS_PROXY"] = "http://127.0.0.1:1";
            info.Environment["NO_PROXY"] = "127.0.0.1,localhost";
            using var process = System.Diagnostics.Process.Start(info)!;
            var errors = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEndAsync();
            try
            {
                var probeType = assembly.GetType("ChatGPTWindowsMcp.McpConnectionProbe")!;
                using var probe = (IDisposable)Activator.CreateInstance(probeType, new object?[] { null })!;
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (true)
                {
                    if (process.HasExited) throw new Exception(errors.GetAwaiter().GetResult());
                    try
                    {
                        var task = (Task<int>)probeType.GetMethod("CheckAsync")!.Invoke(probe, new object[] { port, CancellationToken.None })!;
                        var count = task.GetAwaiter().GetResult();
                        if (count < 1) throw new Exception("No tools");
                        Console.WriteLine($"Relocated embedded Python and MCP started with empty PATH and blocked download proxy: {count} tools; no tools called.");
                        break;
                    }
                    catch when (DateTime.UtcNow < deadline) { Thread.Sleep(250); }
                }
            }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); process.WaitForExit(); }
            var tunnel = new System.Diagnostics.ProcessStartInfo(Path.Combine(root, "tunnel", "tunnel-client.exe"), "--version")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            tunnel.Environment["PATH"] = "";
            using var tunnelProcess = System.Diagnostics.Process.Start(tunnel)!;
            Console.WriteLine(tunnelProcess.StandardOutput.ReadToEnd().Trim()); tunnelProcess.WaitForExit();
            if (tunnelProcess.ExitCode != 0) throw new Exception("Bundled Tunnel failed");
            return;

    }
}
