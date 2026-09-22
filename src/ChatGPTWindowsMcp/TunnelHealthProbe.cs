using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace ChatGPTWindowsMcp;

internal sealed record TunnelHealthObservation(
    bool McpPortOpen, bool? TunnelReady, string Detail, DateTimeOffset ObservedAt);

/// <summary>
/// Passive liveness/readiness only. Never initializes MCP, deletes a session,
/// lists tools, or replays a tool call. /readyz is not end-to-end evidence.
/// </summary>
internal sealed class TunnelHealthProbe : IDisposable
{
    private readonly HttpClient _http;
    private readonly Func<int, CancellationToken, Task<bool>> _checkPort;

    public TunnelHealthProbe(
        HttpMessageHandler? handler = null,
        Func<int, CancellationToken, Task<bool>>? checkPort = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false
        }) { Timeout = TimeSpan.FromSeconds(2) };
        _checkPort = checkPort ?? IsPortOpenAsync;
    }

    public async Task<TunnelHealthObservation> CheckAsync(
        int mcpPort, string healthUrlFile, CancellationToken cancellationToken)
    {
        var localReady = await _checkPort(mcpPort, cancellationToken).ConfigureAwait(false);
        bool? tunnelReady = null;
        var detail = "未取得本次运行的隧道健康地址；旧版本可能不支持 HEALTH_URL_FILE。";

        try
        {
            // A fresh, unpredictable file is used for each run, never an old URL.
            // Bound file input and refuse remote URLs, credentials and redirects.
            if (File.Exists(healthUrlFile))
            {
                if (new FileInfo(healthUrlFile).Length > 4096)
                    throw new InvalidDataException("健康地址文件过大。");
                var text = await File.ReadAllTextAsync(healthUrlFile, cancellationToken)
                    .ConfigureAwait(false);
                if (!TryGetReadyUri(text, out var uri))
                    throw new InvalidDataException("健康地址必须是本机回环 HTTP 地址。");

                using var response = await _http.GetAsync(
                    uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    detail = "隧道未提供 /readyz；健康状态未验证。";
                }
                else
                {
                    tunnelReady = response.StatusCode == HttpStatusCode.OK;
                    detail = tunnelReady.Value
                        ? "隧道 /readyz 检查通过；这不是控制平面持续可达或 ChatGPT 会话可用的证明。"
                        : $"隧道 /readyz 返回 HTTP {(int)response.StatusCode}；未就绪。";
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            tunnelReady = false;
            detail = "隧道健康检查超时。";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException)
        {
            tunnelReady = false;
            // Avoid logging a hostile URL or filesystem content from the file.
            detail = $"隧道健康检查失败（{ex.GetType().Name}）。";
        }

        cancellationToken.ThrowIfCancellationRequested();
        var localDetail = localReady
            ? "本地 TCP 端口可达（不等同于完整 MCP 验证）。"
            : "本地 MCP TCP 端口不可达。";
        return new(localReady, tunnelReady, localDetail + " " + detail, DateTimeOffset.UtcNow);
    }

    internal static bool TryGetReadyUri(string? text, out Uri? readyUri)
    {
        readyUri = null;
        if (text is null || text.Length > 4096 ||
            !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            uri.Port < 1 ||
            (uri.AbsolutePath != "/" && uri.AbsolutePath != "/healthz" && uri.AbsolutePath != "/readyz"))
            return false;

        // Numeric loopback only: no DNS or system proxy can redirect this probe.
        if (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) || !IPAddress.IsLoopback(address))
            return false;

        readyUri = new UriBuilder(uri) { Path = "/readyz", Query = "", Fragment = "" }.Uri;
        return true;
    }

    private static async Task<bool> IsPortOpenAsync(int port, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch (SocketException) { return false; }
    }

    public void Dispose() => _http.Dispose();
}
