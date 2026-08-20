using System.Net.Http;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;

namespace ChatGPTWindowsMcp;

internal sealed class Bootstrapper
{
    private readonly LogSink _log;

    public Bootstrapper(LogSink log)
    {
        _log = log;
    }

    public string? FindUv() => CommandRunner.FindOnPath("uv.exe") ?? CommandRunner.FindOnPath("uv");

    public string? FindUvx() => CommandRunner.FindOnPath("uvx.exe") ?? CommandRunner.FindOnPath("uvx");

    public (string FileName, string[] PrefixArgs)? FindPythonToolRunner()
    {
        var uvx = FindUvx();
        if (uvx is not null)
            return (uvx, Array.Empty<string>());

        var uv = FindUv();
        if (uv is not null)
            return (uv, new[] { "tool", "run" });

        return null;
    }

    public async Task InstallUvWithWingetAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        var winget = CommandRunner.FindOnPath("winget.exe") ?? CommandRunner.FindOnPath("winget");
        if (winget is null)
            throw new InvalidOperationException("未找到 WinGet。请先安装 App Installer，或手动安装 uv。");

        _log.Write("正在通过 WinGet 安装 uv…");
        var proxy = ProxyResolver.ResolveControlPlaneProxy(config);
        var environment = ProxyResolver.BuildNetworkEnvironment(config);
        var arguments = new List<string>
        {
            "install",
            "--id", "astral-sh.uv",
            "-e",
            "--accept-package-agreements",
            "--accept-source-agreements",
            "--silent"
        };
        if (proxy.HasProxy)
        {
            arguments.Add("--proxy");
            arguments.Add(proxy.ProxyUrl!);
        }

        var result = await CommandRunner.RunAsync(
            winget,
            arguments,
            environment: environment,
            log: _log,
            source: "winget",
            cancellationToken: cancellationToken);

        if (result.ExitCode != 0)
            throw new InvalidOperationException($"uv 安装失败，WinGet 退出码 {result.ExitCode}。");

        _log.Write("uv 安装完成。若当前进程尚未刷新 PATH，重新启动本程序即可。");
    }

    public async Task<string> EnsureTunnelClientAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        AppPaths.EnsureDirectories();

        if (File.Exists(AppPaths.TunnelClientExe))
            return AppPaths.TunnelClientExe;

        if (!config.AutoDownloadTunnelClient)
            throw new FileNotFoundException("未找到 tunnel-client.exe，且自动下载已关闭。", AppPaths.TunnelClientExe);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException("tunnel-client 自动安装仅支持 Windows。");

        if (RuntimeInformation.OSArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException($"当前仅自动下载 Windows x64 tunnel-client。检测到架构：{RuntimeInformation.OSArchitecture}");

        using var http = CreateHttpClient(config);
        var (tag, assetUrl) = await ResolveTunnelClientReleaseAsync(http, config.TunnelClientVersion, cancellationToken);

        _log.Write($"正在下载 OpenAI tunnel-client {tag}…");
        var tempZip = Path.Combine(Path.GetTempPath(), $"tunnel-client-{Guid.NewGuid():N}.zip");
        var tempDir = Path.Combine(Path.GetTempPath(), $"tunnel-client-{Guid.NewGuid():N}");

        try
        {
            await using (var input = await http.GetStreamAsync(assetUrl, cancellationToken))
            await using (var output = File.Create(tempZip))
                await input.CopyToAsync(output, cancellationToken);

            Directory.CreateDirectory(tempDir);
            ZipFile.ExtractToDirectory(tempZip, tempDir, overwriteFiles: true);

            var exe = Directory.GetFiles(tempDir, "tunnel-client.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (exe is null)
                throw new InvalidDataException("下载包中没有找到 tunnel-client.exe。");

            Directory.CreateDirectory(AppPaths.TunnelClientDirectory);
            File.Copy(exe, AppPaths.TunnelClientExe, overwrite: true);

            foreach (var optional in new[] { "cloudflared.exe", "cloudflared-manifest.json", "LICENSE" })
            {
                var match = Directory.GetFiles(tempDir, optional, SearchOption.AllDirectories).FirstOrDefault();
                if (match is not null)
                    File.Copy(match, Path.Combine(AppPaths.TunnelClientDirectory, optional), overwrite: true);
            }

            _log.Write($"tunnel-client 已安装：{AppPaths.TunnelClientExe}");
            return AppPaths.TunnelClientExe;
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    public async Task CreateOrRefreshProfileAsync(
        AppConfig config,
        string tunnelClientExe,
        CancellationToken cancellationToken = default)
    {
        AppPaths.EnsureDirectories();
        var mcpUrl = $"http://127.0.0.1:{config.McpPort}/mcp";

        _log.Write($"正在生成 Tunnel Profile：{config.ProfileName}");
        var result = await CommandRunner.RunAsync(
            tunnelClientExe,
            new[]
            {
                "init",
                "--sample", "sample_mcp_remote_no_auth",
                "--profile", config.ProfileName,
                "--profile-dir", AppPaths.ProfilesDirectory,
                "--tunnel-id", config.TunnelId,
                "--mcp-server-url", mcpUrl,
                "--health-listen-addr", "127.0.0.1:0",
                "--force"
            },
            log: _log,
            source: "tunnel-init",
            cancellationToken: cancellationToken);

        if (result.ExitCode != 0)
            throw new InvalidOperationException($"创建 tunnel profile 失败，退出码 {result.ExitCode}。");
    }

    private async Task<(string Tag, string Url)> ResolveTunnelClientReleaseAsync(
        HttpClient http,
        string requestedVersion,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(requestedVersion, "latest", StringComparison.OrdinalIgnoreCase))
        {
            var fixedTag = NormalizeTag(requestedVersion);
            return (fixedTag, BuildTunnelClientAssetUrl(fixedTag));
        }

        // Avoid api.github.com here. Unauthenticated API calls can be rate-limited on shared IPs,
        // which previously caused Install/Start/Doctor to all fail with HTTP 403.
        // GitHub's normal releases/latest page redirects to /releases/tag/<version>, which is enough.
        const string latestPage = "https://github.com/openai/tunnel-client/releases/latest";
        const string fallbackTag = "v0.0.11";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, latestPage);
            using var response = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            var finalUri = response.RequestMessage?.RequestUri;
            var resolvedTag = ExtractReleaseTag(finalUri);
            if (string.IsNullOrWhiteSpace(resolvedTag))
                throw new InvalidDataException($"无法从 GitHub Release 最终地址解析版本：{finalUri}");

            _log.Write($"已解析 tunnel-client 最新版本：{resolvedTag}（未调用 GitHub API）。");
            return (resolvedTag, BuildTunnelClientAssetUrl(resolvedTag));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or TaskCanceledException)
        {
            _log.Write($"解析 tunnel-client 最新版本失败：{ex.Message}");
            _log.Write($"改用兼容回退版本 {fallbackTag}。可在 config.json 的 TunnelClientVersion 中指定其他版本。");
            return (fallbackTag, BuildTunnelClientAssetUrl(fallbackTag));
        }
    }

    private HttpClient CreateHttpClient(AppConfig config)
    {
        var proxy = ProxyResolver.ResolveControlPlaneProxy(config);
        _log.Write(proxy.Description);
        var http = ProxyResolver.CreateHttpClient(config);
        http.Timeout = TimeSpan.FromMinutes(5);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ChatGPT-Windows-MCP", "0.1.1"));
        return http;
    }

    private static string NormalizeTag(string version) =>
        version.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? version : "v" + version;

    private static string BuildTunnelClientAssetUrl(string tag) =>
        $"https://github.com/openai/tunnel-client/releases/download/{tag}/tunnel-client-{tag}-windows-amd64.zip";

    private static string? ExtractReleaseTag(Uri? uri)
    {
        if (uri is null)
            return null;

        const string marker = "/releases/tag/";
        var absolute = uri.AbsolutePath;
        var index = absolute.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return null;

        var tag = Uri.UnescapeDataString(absolute[(index + marker.Length)..]).Trim('/');
        return string.IsNullOrWhiteSpace(tag) ? null : tag;
    }
}
