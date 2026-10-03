using Microsoft.Win32;
using System.Runtime.Versioning;

namespace ChatGPTWindowsMcp;

[SupportedOSPlatform("windows")]
internal sealed class WindowsStartup
{
    internal const string StartupArgument = "--startup";
    private const string ValueName = "ChatGPT-Windows-MCP";
    private readonly string _keyPath;
    private readonly string _command;

    public WindowsStartup(string executablePath,
        string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run")
    {
        if (!Path.IsPathFullyQualified(executablePath) || executablePath.Contains('"') ||
            !string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileName(executablePath), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请从已编译的应用程序 .exe 设置开机自启。", nameof(executablePath));

        _keyPath = keyPath;
        _command = $"\"{executablePath}\" {StartupArgument}";
    }

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
        return string.Equals(key?.GetValue(ValueName) as string, _command, StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
            key.SetValue(ValueName, _command, RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
            // Another portable copy may have taken ownership of the startup entry.
            if (string.Equals(key?.GetValue(ValueName) as string, _command, StringComparison.OrdinalIgnoreCase))
                key!.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
