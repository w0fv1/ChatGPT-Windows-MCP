using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ChatGPTWindowsMcp;

internal sealed class LogSink
{
    private readonly object _gate = new();
    private readonly HashSet<string> _secrets = new(StringComparer.Ordinal);
    private readonly Func<string> _logPath;

    public LogSink(Func<string>? logPath = null) => _logPath = logPath ?? (() => AppPaths.AppLogPath);
    public event Action<string>? LineReceived;

    public void ProtectSecret(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return;
        lock (_gate) _secrets.Add(secret);
    }

    public void Write(string line) => Write("app", line);

    public void Write(string source, string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        string stamped;
        lock (_gate)
        {
            var safe = Redact(line);
            var prefix = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] [pid:{Environment.ProcessId}] [{source}] ";
            stamped = string.Join(Environment.NewLine, safe.ReplaceLineEndings("\n").Split('\n')
                .Select(part => prefix + part));
            try
            {
                var path = _logPath();
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(file, new UTF8Encoding(false)) { AutoFlush = true };
                writer.WriteLine(stamped);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must not crash an Exited/stdout callback or block a
                // lifecycle transition when disk space/permissions change.
                stamped = $"[日志文件写入失败：{ex.GetType().Name}] {stamped}";
            }
        }

        LineReceived?.Invoke(stamped);
    }

    public void Error(string source, Exception exception) =>
        Write(source, $"ERROR {exception.GetType().Name}; HResult=0x{exception.HResult:X8}\n{exception}");

    public void Command(string source, ProcessStartInfo info) =>
        Write(source, $"启动命令：{info.FileName} {string.Join(" ", info.ArgumentList.Select(argument => "\"" + argument + "\""))}\n工作目录：{info.WorkingDirectory}");

    private string Redact(string value)
    {
        foreach (var secret in _secrets.OrderByDescending(secret => secret.Length))
            value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        value = Regex.Replace(value, @"\bsk-[A-Za-z0-9_-]{8,}", "[REDACTED]");
        return Regex.Replace(value, @"(https?://)[^\s/@]+:[^\s/@]+@", "$1[REDACTED]@", RegexOptions.IgnoreCase);
    }
}
