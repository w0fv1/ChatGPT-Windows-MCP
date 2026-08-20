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
            AppPaths.EnsureDirectories();
            File.AppendAllText(AppPaths.AppLogPath, stamped + Environment.NewLine);
        }

        LineReceived?.Invoke(stamped);
    }

    public void Write(string source, string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        Write($"[{source}] {line}");
    }
}
