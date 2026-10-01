using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ChatGPTWindowsMcp;

internal static class UvInstallation
{
    public static string? FindExecutable(string installationDirectory, string name)
    {
        var local = Path.Combine(installationDirectory, name);
        if (File.Exists(local)) return local;
        var existing = CommandRunner.FindOnPath(name);
        if (existing is not null) return existing;
        // WinGet can update the persisted PATH without updating this process.
        if (OperatingSystem.IsWindows())
        {
            foreach (var target in new[] { EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            {
                existing = CommandRunner.FindOnPath(name,
                    Environment.GetEnvironmentVariable("PATH", target) ?? "");
                if (existing is not null) return existing;
            }
        }
        return null;
    }

    public static string AssetName(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "uv-x86_64-pc-windows-msvc.zip",
        Architecture.Arm64 => "uv-aarch64-pc-windows-msvc.zip",
        Architecture.X86 => "uv-i686-pc-windows-msvc.zip",
        _ => throw new PlatformNotSupportedException($"uv 自动安装不支持架构：{architecture}")
    };

    public static async Task InstallAsync(HttpClient http, string destination, Architecture architecture,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var asset = AssetName(architecture);
        // Resolve once so the archive and checksum always refer to the same release.
        using var release = await http.GetAsync("https://github.com/astral-sh/uv/releases/latest",
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        release.EnsureSuccessStatusCode();
        var releaseUri = release.RequestMessage?.RequestUri;
        const string tagPrefix = "/astral-sh/uv/releases/tag/";
        if (releaseUri is null || releaseUri.Host != "github.com" ||
            !releaseUri.AbsolutePath.StartsWith(tagPrefix, StringComparison.Ordinal))
            throw new InvalidDataException("无法解析 uv 官方最新版本。");
        var tag = releaseUri.AbsolutePath[tagPrefix.Length..];
        if (!Regex.IsMatch(tag, @"^\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.-]+)?$"))
            throw new InvalidDataException("uv 官方版本号格式无效。");
        var url = $"https://github.com/astral-sh/uv/releases/download/{tag}/{asset}";
        var checksumText = await http.GetStringAsync(url + ".sha256", cancellationToken).ConfigureAwait(false);
        var checksum = checksumText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (checksum is null || !Regex.IsMatch(checksum, "^[A-Fa-f0-9]{64}$"))
            throw new InvalidDataException("uv 官方 SHA-256 校验文件格式无效。");

        var parent = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".uv-install-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        var archivePath = Path.Combine(staging, "uv.zip");
        try
        {
            await using (var input = await http.GetStreamAsync(url, cancellationToken).ConfigureAwait(false))
            await using (var output = File.Create(archivePath))
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            await using (var input = File.OpenRead(archivePath))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
                if (!actual.Equals(checksum, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("uv 下载包 SHA-256 校验失败；未安装。");
            }

            using (var zip = ZipFile.OpenRead(archivePath))
            {
                foreach (var name in new[] { "uv.exe", "uvx.exe" })
                {
                    var entries = zip.Entries.Where(entry => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (entries.Length != 1 || entries[0].Length == 0)
                        throw new InvalidDataException($"uv 下载包必须包含唯一且非空的 {name}。");
                    await using var input = entries[0].Open();
                    await using var output = File.Create(Path.Combine(staging, name));
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                }
            }
            File.Delete(archivePath);
            cancellationToken.ThrowIfCancellationRequested();
            // Publish the pair together; a failed download never leaves a partial install.
            try { Directory.Move(staging, destination); }
            catch (IOException) when (File.Exists(Path.Combine(destination, "uv.exe")) &&
                                      File.Exists(Path.Combine(destination, "uvx.exe")))
            {
                // Another launcher completed the same installation first.
            }
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }
}
