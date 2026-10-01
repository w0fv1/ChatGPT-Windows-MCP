using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ChatGPTWindowsMcp;

// Explicit startup/registration diagnostic only. No tools/call or periodic sessions.
internal sealed class McpConnectionProbe : IDisposable
{
    private readonly HttpClient _http;
    public McpConnectionProbe(HttpMessageHandler? handler = null) =>
        _http = new HttpClient(handler ?? new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });

    public async Task<int> CheckAsync(int port, CancellationToken cancellationToken)
    {
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        string? session = null;
        var protocol = "2025-03-26";
        HttpRequestMessage Request(HttpMethod method, object? body = null)
        {
            var request = new HttpRequestMessage(method, endpoint);
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", protocol);
            if (session is not null) request.Headers.Add("Mcp-Session-Id", session);
            if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            return request;
        }
        async Task<JsonElement> Rpc(int id, string method, object parameters)
        {
            using var request = Request(HttpMethod.Post, new { jsonrpc = "2.0", id, method, @params = parameters });
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidDataException($"MCP {method} 返回 HTTP {(int)response.StatusCode}；请检查 /mcp 地址、服务认证和本机日志。");
            if (method == "initialize" && response.Headers.TryGetValues("Mcp-Session-Id", out var values))
                session = values.Single();
            var root = await ReadResponseAsync(response, id, ct).ConfigureAwait(false);
            if (root.TryGetProperty("error", out var error))
                throw new InvalidDataException($"MCP {method} 返回协议错误：{error.GetRawText()}");
            if (!root.TryGetProperty("result", out var result)) throw new InvalidDataException($"MCP {method} 缺少 result。");
            return result;
        }
        try
        {
            var init = await Rpc(1, "initialize", new
            {
                protocolVersion = protocol, capabilities = new { },
                clientInfo = new { name = "ChatGPT-Windows-MCP-diagnostic", version = "1.0.0" }
            }).ConfigureAwait(false);
            if (!init.TryGetProperty("protocolVersion", out var version) || version.GetString() is not
                ("2025-03-26" or "2025-06-18" or "2025-11-25"))
                throw new InvalidDataException("MCP 服务未返回受支持的 Streamable HTTP 协议版本。");
            protocol = version.GetString()!;
            using (var notification = Request(HttpMethod.Post, new { jsonrpc = "2.0", method = "notifications/initialized" }))
            using (var accepted = await _http.SendAsync(notification, ct).ConfigureAwait(false))
                accepted.EnsureSuccessStatusCode();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            for (var page = 0; page < 32; page++)
            {
                var result = await Rpc(page + 2, "tools/list", cursor is null ? new { } : (object)new { cursor }).ConfigureAwait(false);
                if (!result.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("MCP tools/list 未返回有效的工具列表。");
                foreach (var tool in tools.EnumerateArray())
                {
                    if (!tool.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(name.GetString()) || !names.Add(name.GetString()!) ||
                        !tool.TryGetProperty("inputSchema", out var schema) || schema.ValueKind != JsonValueKind.Object)
                        throw new InvalidDataException("MCP 工具名称或 inputSchema 不正确；请检查服务版本和工具定义。");
                }
                cursor = result.TryGetProperty("nextCursor", out var next) && next.ValueKind != JsonValueKind.Null ? next.GetString() : null;
                if (string.IsNullOrEmpty(cursor))
                {
                    if (names.Count == 0) throw new InvalidDataException("MCP 服务没有返回任何工具，无法创建 Windows 操作连接。");
                    return names.Count;
                }
                if (!cursors.Add(cursor)) throw new InvalidDataException("MCP 工具分页游标重复。");
            }
            throw new InvalidDataException("MCP 工具分页过多，未能完成工具发现。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("MCP 初始化或工具发现超过 20 秒；请检查本机服务日志。");
        }
        finally
        {
            if (session is not null)
            {
                // End only the diagnostic's own session, never another client's session.
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    using var request = Request(HttpMethod.Delete);
                    using var response = await _http.SendAsync(request, cleanup.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { }
            }
        }
    }

    private static async Task<JsonElement> ReadResponseAsync(HttpResponseMessage response, int id, CancellationToken ct)
    {
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var sse = response.Content.Headers.ContentType?.MediaType == "text/event-stream";
        var data = new StringBuilder();
        var size = 0;
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            size += line.Length;
            if (size > 2_000_000) throw new InvalidDataException("MCP 响应超过诊断大小限制。");
            if (!sse) { data.AppendLine(line); continue; }
            if (line.StartsWith("data:", StringComparison.Ordinal)) data.AppendLine(line[5..].TrimStart(' '));
            else if (line.Length == 0 && data.Length > 0)
            {
                using var document = JsonDocument.Parse(data.ToString());
                data.Clear();
                if (Matches(document.RootElement, id)) return document.RootElement.Clone();
            }
        }
        if (data.Length > 0)
        {
            using var document = JsonDocument.Parse(data.ToString());
            if (Matches(document.RootElement, id)) return document.RootElement.Clone();
        }
        throw new InvalidDataException("MCP 未返回匹配的 JSON-RPC 响应。");
    }

    private static bool Matches(JsonElement root, int id) => root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("jsonrpc", out var rpc) && rpc.GetString() == "2.0" &&
        root.TryGetProperty("id", out var value) && value.TryGetInt32(out var actual) && actual == id;

    public void Dispose() => _http.Dispose();
}
