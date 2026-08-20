using Microsoft.Win32;

namespace ChatGPTWindowsMcp;

internal sealed record ProxyResolution(string? ProxyUrl, string Source, string Description)
{
    public bool HasProxy => !string.IsNullOrWhiteSpace(ProxyUrl);
}

internal static class ProxyResolver
{
    private const string InternetSettingsPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    public static ProxyResolution ResolveControlPlaneProxy(AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.ControlPlaneHttpProxy))
        {
            var normalized = NormalizeProxyUrl(config.ControlPlaneHttpProxy);
            return new ProxyResolution(normalized, "config", $"使用配置中的 Control Plane 代理：{FormatForLog(normalized)}");
        }

        var environmentProxy = FirstEnvironmentProxy();
        if (environmentProxy is not null)
        {
            var normalized = NormalizeProxyUrl(environmentProxy.Value.Value);
            return new ProxyResolution(normalized, environmentProxy.Value.Name,
                $"检测到环境变量 {environmentProxy.Value.Name}：{FormatForLog(normalized)}");
        }

        if (!config.AutoDetectSystemProxy)
            return new ProxyResolution(null, "direct", "未配置代理，且已关闭 Windows 系统代理自动检测；Control Plane 将直连。");

        var systemProxy = TryReadWindowsSystemProxy();
        if (!string.IsNullOrWhiteSpace(systemProxy))
        {
            var normalized = NormalizeProxyUrl(systemProxy);
            return new ProxyResolution(normalized, "windows-system-proxy",
                $"检测到 Windows 系统代理：{FormatForLog(normalized)}");
        }

        return new ProxyResolution(null, "direct", "未检测到可用的 HTTP(S) 代理；Control Plane 将直连。");
    }

    public static bool TryNormalizeProxyUrl(string value, out string normalized)
    {
        try
        {
            normalized = NormalizeProxyUrl(value);
            return true;
        }
        catch
        {
            normalized = "";
            return false;
        }
    }

    private static (string Name, string Value)? FirstEnvironmentProxy()
    {
        foreach (var name in new[]
                 {
                     "CONTROL_PLANE_HTTP_PROXY",
                     "TUNNEL_CLIENT_HTTP_PROXY",
                     "HTTPS_PROXY",
                     "HTTP_PROXY"
                 })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
                return (name, value.Trim());
        }

        return null;
    }

    private static string? TryReadWindowsSystemProxy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable: false);
            if (key is null)
                return null;

            var proxyEnabled = Convert.ToInt32(key.GetValue("ProxyEnable", 0)) != 0;
            if (!proxyEnabled)
                return null;

            var proxyServer = key.GetValue("ProxyServer") as string;
            if (string.IsNullOrWhiteSpace(proxyServer))
                return null;

            return SelectHttpsProxy(proxyServer.Trim());
        }
        catch
        {
            return null;
        }
    }

    private static string? SelectHttpsProxy(string proxyServer)
    {
        if (!proxyServer.Contains('=', StringComparison.Ordinal))
            return proxyServer;

        string? http = null;
        string? https = null;

        foreach (var item in proxyServer.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = item.IndexOf('=');
            if (equals <= 0 || equals == item.Length - 1)
                continue;

            var scheme = item[..equals].Trim();
            var address = item[(equals + 1)..].Trim();

            if (scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                https = address;
            else if (scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
                http = address;
        }

        return https ?? http;
    }

    private static string NormalizeProxyUrl(string value)
    {
        var candidate = value.Trim();
        if (candidate.Length == 0)
            throw new FormatException("代理地址为空。");

        if (!candidate.Contains("://", StringComparison.Ordinal))
            candidate = "http://" + candidate;

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            throw new FormatException("代理地址格式无效。");

        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Control Plane 代理仅支持 http:// 或 https:// URL。");

        return uri.AbsoluteUri.TrimEnd('/');
    }

    private static string FormatForLog(string proxyUrl)
    {
        if (!Uri.TryCreate(proxyUrl, UriKind.Absolute, out var uri))
            return "<invalid proxy>";

        var builder = new UriBuilder(uri)
        {
            UserName = string.IsNullOrEmpty(uri.UserInfo) ? "" : "***",
            Password = ""
        };
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }
}
