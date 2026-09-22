using System.Text.Json;

namespace ChatGPTWindowsMcp;

internal sealed class AppConfig
{
    public string TunnelId { get; set; } = "";
    public string RuntimeApiKey { get; set; } = "";
    public int McpPort { get; set; } = 8000;
    public string ProfileName { get; set; } = "windows-mcp";
    public string WindowsMcpSpec { get; set; } = "windows-mcp";
    public string PythonVersion { get; set; } = "3.13";
    public bool ReuseExistingMcp { get; set; } = false;
    public bool AutoOpenChatGptConnectors { get; set; } = true;
    public bool AutoDownloadTunnelClient { get; set; } = true;
    public string TunnelClientVersion { get; set; } = "latest";
    public bool AutoDetectSystemProxy { get; set; } = true;
    public string ControlPlaneHttpProxy { get; set; } = "";

    public static AppConfig Load()
    {
        AppPaths.EnsureDirectories();

        if (!File.Exists(AppPaths.ConfigPath))
        {
            var config = new AppConfig();
            config.Save();
            return config;
        }

        var json = File.ReadAllText(AppPaths.ConfigPath);
        return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions()) ?? new AppConfig();
    }

    public void Save()
    {
        AppPaths.EnsureDirectories();
        var json = JsonSerializer.Serialize(this, JsonOptions());
        File.WriteAllText(AppPaths.ConfigPath, json);
    }

    public List<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(TunnelId) || !TunnelId.StartsWith("tunnel_", StringComparison.OrdinalIgnoreCase))
            errors.Add("Tunnel ID 无效，应类似 tunnel_xxxxxxxxx。");

        if (string.IsNullOrWhiteSpace(RuntimeApiKey))
            errors.Add("Runtime API Key 不能为空。");

        if (McpPort is < 1024 or > 65535)
            errors.Add("MCP 端口必须在 1024–65535 之间。");

        if (string.IsNullOrWhiteSpace(ProfileName))
            errors.Add("Tunnel Profile 名称不能为空。");

        if (string.IsNullOrWhiteSpace(WindowsMcpSpec))
            errors.Add("Windows-MCP 包名不能为空。");

        if (string.IsNullOrWhiteSpace(PythonVersion))
            errors.Add("Python 版本不能为空。");

        if (!string.IsNullOrWhiteSpace(ControlPlaneHttpProxy) &&
            !ProxyResolver.TryNormalizeProxyUrl(ControlPlaneHttpProxy, out _))
            errors.Add("代理地址无效，必须以 http:// 或 https:// 开头，例如 http://127.0.0.1:7890。");

        try { _ = TunnelVersionPolicy.Normalize(TunnelClientVersion); }
        catch (ArgumentException ex) { errors.Add(ex.Message); }

        return errors;
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
}
