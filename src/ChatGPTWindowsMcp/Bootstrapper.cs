using System.Net.Http;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;

namespace ChatGPTWindowsMcp;

internal sealed class Bootstrapper
{
    private readonly LogSink _log;
    private readonly SemaphoreSlim _installGate = new(1, 1);

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
        await _installGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await EnsureTunnelClientCoreAsync(config, cancellationToken).ConfigureAwait(false);
        }
        finally { _installGate.Release(); }
    }

    private async Task<string> EnsureTunnelClientCoreAsync(AppConfig config, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AppPaths.EnsureDirectories();
        var requested = TunnelVersionPolicy.Normalize(config.TunnelClientVersion);
        var root = AppPaths.TunnelClientDirectory;
        var pointer = Path.Combine(root, "current-version.txt");
        string? tag = requested == "latest" ? null : requested;

        // latest means use the selected cache; it never silently replaces a
        // working installation. Fixed versions must never reuse the legacy EXE.
        if (requested == "latest" && File.Exists(pointer))
        {
            if (new FileInfo(pointer).Length > 128)
                throw new InvalidDataException("当前版本记录过大；请检查 current-version.txt。");
            tag = TunnelVersionPolicy.Normalize(
                await File.ReadAllTextAsync(pointer, cancellationToken).ConfigureAwait(false));
            if (tag == "latest") throw new InvalidDataException("当前版本记录必须包含明确版本号。");
        }
        else if (requested == "latest" && File.Exists(AppPaths.TunnelClientExe))
        {
            _log.Write("TunnelClientVersion=latest：使用旧版布局缓存，实际版本未核对；不会自动检查更新。指定明确版本可安装到独立目录。");
            return AppPaths.TunnelClientExe;
        }

        if (tag is not null)
        {
            var cachedDirectory = TunnelVersionPolicy.ReleaseDirectory(root, tag);
            if (Directory.Exists(cachedDirectory))
            {
                if (!TunnelVersionPolicy.IsValidInstall(cachedDirectory, tag))
                    throw new InvalidDataException($"tunnel-client {tag} 缓存不完整或完整性校验失败；请在停止服务后检查该版本目录。");
                _log.Write($"tunnel-client 安装来源：{tag}；缓存完整性检查通过（不是发布者签名验证）。");
                return Path.Combine(cachedDirectory, "tunnel-client.exe");
            }
        }

        if (!config.AutoDownloadTunnelClient)
            throw new FileNotFoundException($"没有匹配 {requested} 的已验证缓存，且自动下载已关闭。");
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException("tunnel-client 自动安装仅支持 Windows。");
        if (RuntimeInformation.OSArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException($"当前仅自动下载 Windows x64 tunnel-client。检测到架构：{RuntimeInformation.OSArchitecture}");

        using var http = CreateHttpClient(config);
        var release = await ResolveTunnelClientReleaseAsync(http, tag ?? requested, cancellationToken)
            .ConfigureAwait(false);
        tag = TunnelVersionPolicy.Normalize(release.Tag);
        var destination = TunnelVersionPolicy.ReleaseDirectory(root, tag);
        var selectedExe = Path.Combine(destination, "tunnel-client.exe");
        var tempZip = Path.Combine(Path.GetTempPath(), $"tunnel-client-{Guid.NewGuid():N}.zip");
        var tempDir = Path.Combine(Path.GetTempPath(), $"tunnel-client-{Guid.NewGuid():N}");
        // Staging on the destination volume allows an atomic directory rename.
        var staging = Path.Combine(root, $".install-{Guid.NewGuid():N}");
        var pointerTemp = Path.Combine(root, $".version-{Guid.NewGuid():N}.tmp");

        try
        {
            if (!Directory.Exists(destination))
            {
                _log.Write($"正在下载 OpenAI tunnel-client {tag}…");
                await using (var input = await http.GetStreamAsync(release.Url, cancellationToken).ConfigureAwait(false))
                await using (var output = File.Create(tempZip))
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);

                Directory.CreateDirectory(tempDir);
                ZipFile.ExtractToDirectory(tempZip, tempDir);
                var executables = Directory.GetFiles(tempDir, "tunnel-client.exe", SearchOption.AllDirectories);
                if (executables.Length != 1)
                    throw new InvalidDataException("下载包必须包含唯一的 tunnel-client.exe。");

                Directory.CreateDirectory(staging);
                File.Copy(executables[0], Path.Combine(staging, "tunnel-client.exe"));
                foreach (var optional in new[] { "cloudflared.exe", "cloudflared-manifest.json", "LICENSE" })
                {
                    var matches = Directory.GetFiles(tempDir, optional, SearchOption.AllDirectories);
                    if (matches.Length > 1)
                        throw new InvalidDataException($"下载包包含重复的 {optional}。");
                    if (matches.Length == 1) File.Copy(matches[0], Path.Combine(staging, optional));
                }
                TunnelVersionPolicy.WriteReceipt(staging, tag);
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                try { Directory.Move(staging, destination); }
                catch (IOException) when (TunnelVersionPolicy.IsValidInstall(destination, tag))
                {
                    // Another launcher installed the same release; never overwrite it.
                }
            }

            if (!TunnelVersionPolicy.IsValidInstall(destination, tag))
                throw new InvalidDataException("安装目录未通过完整性校验；未切换当前版本。");
            cancellationToken.ThrowIfCancellationRequested();
            if (requested == "latest")
            {
                await File.WriteAllTextAsync(pointerTemp, tag, cancellationToken).ConfigureAwait(false);
                File.Move(pointerTemp, pointer, overwrite: true);
            }
            _log.Write($"tunnel-client 安装来源 {tag}：{selectedExe}");
            return selectedExe;
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            try { if (File.Exists(pointerTemp)) File.Delete(pointerTemp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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
            resolvedTag = NormalizeTag(resolvedTag);
            return (resolvedTag, BuildTunnelClientAssetUrl(resolvedTag));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or OperationCanceledException)
        {
            throw new InvalidOperationException(
                "无法解析 tunnel-client 最新版本；未回退到旧版本。请检查网络或显式指定 TunnelClientVersion。", ex);
        }
    }

    private HttpClient CreateHttpClient(AppConfig config)
    {
        var proxy = ProxyResolver.ResolveControlPlaneProxy(config);
        _log.Write(proxy.Description);
        var http = ProxyResolver.CreateHttpClient(config);
        http.Timeout = TimeSpan.FromMinutes(5);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ChatGPT-Windows-MCP", "0.1.2"));
        return http;
    }

    private static string NormalizeTag(string version) => TunnelVersionPolicy.Normalize(version);

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
