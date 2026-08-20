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
        CancellationToken cancellationToken = default)
    {
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
        var output = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (output) output.AppendLine(e.Data);
            log?.Write(source, e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (output) output.AppendLine(e.Data);
            log?.Write(source, e.Data);
        };

        if (!process.Start())
            throw new InvalidOperationException($"无法启动 {fileName}");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, output.ToString());
    }

    public static string? FindOnPath(string command)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = command,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var p = Process.Start(psi);
            if (p is null) return null;

            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);

            return p.ExitCode == 0
                ? output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                : null;
        }
        catch
        {
            return null;
        }
    }
}
