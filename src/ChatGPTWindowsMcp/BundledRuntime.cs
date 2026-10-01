using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace ChatGPTWindowsMcp;

internal static class BundledRuntime
{
    public const string PythonVersion = "3.13.14";
    public const string McpVersion = "0.8.5";
    public const string TunnelVersion = "v0.0.15";
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string?> Installed = new(StringComparer.OrdinalIgnoreCase);

    public static bool SupportsMcp(string pythonVersion, string package) =>
        (pythonVersion is "3.13" or PythonVersion) &&
        (package is "windows-mcp" or "windows-mcp==" + McpVersion);

    public static string? TryInstall(string destination) => Install(destination, null);

    public static string? Install(string destination, Action<int, string>? progress)
    {
        lock (Gate)
        {
            if (Installed.TryGetValue(destination, out var previous)) return previous;
            var assembly = typeof(BundledRuntime).Assembly;
            using var manifest = assembly.GetManifestResourceStream("BundledRuntime.manifest.json");
            if (manifest is null) { Installed[destination] = null; return null; }
            using var json = JsonDocument.Parse(manifest);
            var data = json.RootElement;
            if (data.GetProperty("pythonVersion").GetString() != PythonVersion ||
                data.GetProperty("mcpVersion").GetString() != McpVersion ||
                data.GetProperty("tunnelVersion").GetString() != TunnelVersion)
                throw new InvalidDataException("内置运行环境版本与启动器不一致。");
            var archiveHash = data.GetProperty("archiveSha256").GetString()!;
            if (archiveHash.Length != 64 || !archiveHash.All(Uri.IsHexDigit))
                throw new InvalidDataException("内置运行环境校验清单无效。");
            var root = Path.GetFullPath(Path.Combine(destination, archiveHash[..16]));
            var files = data.GetProperty("files").EnumerateObject().ToDictionary(f => f.Name, f => f.Value.GetString()!);
            foreach (var (name, hash) in files)
            {
                if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("无效文件校验值。");
                SafePath(root, name);
            }
            var missing = new Dictionary<string, string>();
            var checkedCount = 0;
            var lastPercent = -1;
            void Report(int percent, string detail)
            {
                if (percent == lastPercent) return;
                lastPercent = percent;
                progress?.Invoke(percent, detail);
            }
            foreach (var (name, hash) in files)
            {
                Report(checkedCount * 20 / files.Count, $"正在检查本地运行文件（{checkedCount}/{files.Count}）…");
                if (!Matches(SafePath(root, name), hash)) missing.Add(name, hash);
                checkedCount++;
            }
            if (missing.Count > 0)
            {
                Directory.CreateDirectory(destination);
                var temporary = Path.Combine(destination, Guid.NewGuid().ToString("N") + ".zip.tmp");
                try
                {
                    Report(20, "正在读取内置运行环境压缩包…");
                    using (var input = assembly.GetManifestResourceStream("BundledRuntime.runtime.zip") ??
                        throw new InvalidDataException("缺少内置运行环境压缩包。"))
                    using (var output = File.Create(temporary)) input.CopyTo(output);
                    Report(25, "正在校验内置运行环境压缩包…");
                    if (!Matches(temporary, archiveHash)) throw new InvalidDataException("内置运行环境压缩包校验失败。");
                    using var archive = ZipFile.OpenRead(temporary);
                    var extracted = 0;
                    foreach (var (name, hash) in missing)
                    {
                        var component = name.StartsWith("tunnel/") ? "Tunnel 客户端" : name.Contains("site-packages/") ? "Windows-MCP 依赖" : "Python";
                        Report(30 + extracted * 69 / missing.Count, $"正在释放{component}（{extracted}/{missing.Count} 个文件）…");
                        var entry = archive.GetEntry(name) ?? throw new InvalidDataException($"缺少内置文件：{name}");
                        var target = SafePath(root, name);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        var staging = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        try
                        {
                            using (var source = entry.Open())
                            using (var output = File.Create(staging)) source.CopyTo(output);
                            if (!Matches(staging, hash)) throw new InvalidDataException($"内置文件校验失败：{name}");
                            File.Move(staging, target, overwrite: true);
                            extracted++;
                        }
                        finally { if (File.Exists(staging)) File.Delete(staging); }
                    }
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            Installed[destination] = root;
            Report(100, "内置运行环境已准备好");
            return root;
        }
    }

    internal static string SafePath(string root, string name)
    {
        if (name.Contains('\\') || name.Contains(':') || name.StartsWith('/') ||
            name.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("内置压缩包包含不安全路径。");
        var path = Path.GetFullPath(Path.Combine(root, name));
        if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("内置压缩包路径越界。");
        return path;
    }

    private static bool Matches(string path, string hash)
    {
        if (!File.Exists(path)) return false;
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).Equals(hash, StringComparison.OrdinalIgnoreCase);
    }
}
