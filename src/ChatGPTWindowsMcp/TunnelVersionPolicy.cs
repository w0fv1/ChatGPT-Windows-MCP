using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChatGPTWindowsMcp;

/// <summary>
/// Pins installed releases by tag and checks cache integrity. The receipt is not
/// a publisher signature and does not replace verification of upstream releases.
/// </summary>
internal static class TunnelVersionPolicy
{
    private const string ReceiptName = "launcher-install.json";
    private static readonly string[] AllowedFiles =
        { "tunnel-client.exe", "cloudflared.exe", "cloudflared-manifest.json", "LICENSE" };

    internal sealed record Receipt(string Tag, Dictionary<string, string> Sha256);

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("TunnelClientVersion 不能为空：请使用 latest 或明确版本号。");
        value = value.Trim();
        if (value.Equals("latest", StringComparison.OrdinalIgnoreCase))
            return "latest";
        if (!Regex.IsMatch(value, @"\Av?\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?\z",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new ArgumentException("TunnelClientVersion 格式无效：请使用 latest 或 vX.Y.Z。");
        return "v" + value.TrimStart('v', 'V');
    }

    public static string ReleaseDirectory(string root, string tag)
    {
        tag = Normalize(tag);
        if (tag == "latest")
            throw new ArgumentException("安装目录必须使用已解析的明确版本号。", nameof(tag));
        return Path.Combine(root, "versions", tag);
    }

    public static void WriteReceipt(string directory, string tag)
    {
        tag = Normalize(tag);
        if (tag == "latest") throw new ArgumentException("安装记录必须使用明确版本号。", nameof(tag));
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in AllowedFiles)
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) hashes.Add(name, Hash(path));
        }
        if (!hashes.ContainsKey("tunnel-client.exe"))
            throw new InvalidDataException("安装包缺少 tunnel-client.exe。");
        File.WriteAllText(Path.Combine(directory, ReceiptName),
            JsonSerializer.Serialize(new Receipt(tag, hashes)));
    }

    public static bool IsValidInstall(string directory, string tag)
    {
        try
        {
            var receiptPath = Path.Combine(directory, ReceiptName);
            if (!File.Exists(receiptPath) || new FileInfo(receiptPath).Length > 16384)
                return false;
            var receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(receiptPath));
            if (receipt is null || receipt.Tag != Normalize(tag) || receipt.Sha256 is null ||
                !receipt.Sha256.ContainsKey("tunnel-client.exe"))
                return false;
            foreach (var item in receipt.Sha256)
            {
                if (!AllowedFiles.Contains(item.Key, StringComparer.Ordinal)) return false;
                var path = Path.Combine(directory, item.Key);
                if (!File.Exists(path) || !string.Equals(Hash(path), item.Value, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            // Do not execute an unrecorded companion dropped into an existing cache.
            return AllowedFiles.All(name =>
                !File.Exists(Path.Combine(directory, name)) || receipt.Sha256.ContainsKey(name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
