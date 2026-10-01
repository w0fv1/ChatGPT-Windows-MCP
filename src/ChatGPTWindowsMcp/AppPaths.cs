namespace ChatGPTWindowsMcp;

internal static class AppPaths
{
    public static string BaseDirectory => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    public static string ConfigPath => Path.Combine(BaseDirectory, "config.json");
    public static string ToolsDirectory => Path.Combine(BaseDirectory, "tools");
    public static string UvDirectory => Path.Combine(ToolsDirectory, "uv");
    public static string BundledUvDirectory => Path.Combine(UvDirectory, "bundled");
    public static string BundledRuntimeDirectory => Path.Combine(ToolsDirectory, "bundled-runtime");
    public static string TunnelClientDirectory => Path.Combine(ToolsDirectory, "tunnel-client");
    public static string TunnelClientExe => Path.Combine(TunnelClientDirectory, "tunnel-client.exe");
    public static string ProfilesDirectory => Path.Combine(BaseDirectory, "profiles");
    public static string LogsDirectory => Path.Combine(BaseDirectory, "logs");
    public static string RuntimeDirectory => Path.Combine(BaseDirectory, "runtime");
    public static string AppLogPath => Path.Combine(LogsDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(ToolsDirectory);
        Directory.CreateDirectory(TunnelClientDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    public static void RemoveEmptyWorkingDirectories()
    {
        foreach (var directory in new[] { ProfilesDirectory, RuntimeDirectory })
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: false); }
            catch (IOException) { } // Keep non-empty or in-use directories.
            catch (UnauthorizedAccessException) { }
        }
    }
}
