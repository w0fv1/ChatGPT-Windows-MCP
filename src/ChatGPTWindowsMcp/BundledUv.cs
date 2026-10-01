using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace ChatGPTWindowsMcp;

internal static class BundledUv
{
    public static bool TryInstall(string destination)
    {
        var assembly = typeof(BundledUv).Assembly;
        using var manifest = assembly.GetManifestResourceStream("BundledUv.manifest.json");
        if (manifest is null) return false; // Development builds may use the existing download fallback.
        using var json = JsonDocument.Parse(manifest);
        var hashes = json.RootElement.GetProperty("files").EnumerateObject()
            .ToDictionary(file => file.Name, file => file.Value.GetString()!, StringComparer.Ordinal);
        Extract(destination, hashes, name => assembly.GetManifestResourceStream("BundledUv." + name));
        return true;
    }

    internal static void Extract(string destination, IReadOnlyDictionary<string, string> hashes,
        Func<string, Stream?> resource)
    {
        var names = new[] { "uv.exe", "uvx.exe", "LICENSE-MIT", "LICENSE-APACHE" };
        if (hashes.Count != names.Length || names.Any(name => !hashes.TryGetValue(name, out var hash) ||
            hash.Length != 64 || !hash.All(Uri.IsHexDigit)))
            throw new InvalidDataException("内置 uv 校验清单不完整。");
        Directory.CreateDirectory(destination);
        foreach (var name in names)
        {
            var target = Path.Combine(destination, name);
            if (File.Exists(target) && Matches(target, hashes[name])) continue;
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var input = resource(name) ?? throw new InvalidDataException($"缺少内置 uv 文件：{name}"))
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    input.CopyTo(output);
                if (!Matches(temporary, hashes[name])) throw new InvalidDataException($"内置 uv 文件校验失败：{name}");
                File.Move(temporary, target, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private static bool Matches(string path, string expected)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}
