using System.Diagnostics;
using System.Text;

namespace ChatGPTWindowsMcp;

internal static class CommandRunner
{
    public static async Task<(int ExitCode, string Output)> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        IDictionary<string, string?>? environment = null,
        string? workingDirectory = null,
        LogSink? log = null,
        string source = "cmd",
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromMinutes(10));
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? AppPaths.BaseDirectory,
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

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        log?.Command(source, psi);
        var duration = Stopwatch.StartNew();
        var output = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (output) output.AppendLine(e.Data);
            log?.Write(source + "/stdout", e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (output) output.AppendLine(e.Data);
            log?.Write(source + "/stderr", e.Data);
        };

        try
        {
            if (!process.Start()) throw new InvalidOperationException($"无法启动 {fileName}");
            log?.Write(source, $"进程已启动；PID={process.Id}；超时上限={(timeout ?? TimeSpan.FromMinutes(10)).TotalSeconds:0} 秒。");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            log?.Write(source, $"进程结束；退出码={process.ExitCode}；耗时={duration.Elapsed.TotalSeconds:F2} 秒。");
            lock (output) return (process.ExitCode, output.ToString());
        }
        catch (OperationCanceledException)
        {
            log?.Write(source, cancellationToken.IsCancellationRequested ? "命令已取消；请求结束子进程树。" : "命令超时；请求结束子进程树。");
            // Cancelling WaitForExitAsync alone does not terminate the child.
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await process.WaitForExitAsync(cleanupDeadline.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                log?.Write(source, "命令取消后的进程清理未能完成；请检查残留进程。");
            }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException($"{source} 执行超时，已请求结束所启动的进程树。");
        }
        catch (Exception ex)
        {
            log?.Error(source, ex);
            throw;
        }
    }

    public static string? FindOnPath(string command, string? searchPath = null)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        // No synchronous where.exe/ReadToEnd() call can wedge the UI thread.
        var names = OperatingSystem.IsWindows() && !Path.HasExtension(command)
            ? new[] { command + ".exe", command }
            : new[] { command };
        foreach (var directory in (searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                try
                {
                    var path = Path.GetFullPath(Path.Combine(
                        Environment.ExpandEnvironmentVariables(directory.Trim().Trim('"')), name));
                    if (File.Exists(path)) return path;
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
            }
        }
        return null;
    }
}
