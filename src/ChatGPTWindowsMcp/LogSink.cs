namespace ChatGPTWindowsMcp;

internal sealed class LogSink
{
    private readonly object _gate = new();

    public event Action<string>? LineReceived;

    public void Write(string line)
    {
        var stamped = $"[{DateTime.Now:HH:mm:ss}] {line}";
        lock (_gate)
        {
            try
            {
                AppPaths.EnsureDirectories();
                File.AppendAllText(AppPaths.AppLogPath, stamped + Environment.NewLine);
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

    public void Write(string source, string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        Write($"[{source}] {line}");
    }
}
