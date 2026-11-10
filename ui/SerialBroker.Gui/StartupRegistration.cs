using System.IO;
using Microsoft.Win32;

namespace SerialBroker.Gui;

internal interface IStartupRegistration
{
    bool IsEnabled { get; }
    void SetEnabled(bool enabled);
}

internal sealed class StartupRegistration(string executablePath, string dataDirectory,
    string registryPath = @"Software\Microsoft\Windows\CurrentVersion\Run") : IStartupRegistration
{
    private const string ValueName = "SerialBroker";
    internal string CommandLine { get; } = $"{QuotePath(executablePath)} --start-in-tray --data-directory {QuotePath(dataDirectory)}";

    private static string QuotePath(string path)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (path.Contains('"')) throw new ArgumentException("自启动路径不能包含引号");
        // 根目录保留末尾反斜杠；Windows命令行中的闭合引号前需要双写。
        return "\"" + path + (path.EndsWith('\\') ? "\\" : "") + "\"";
    }
    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(registryPath);
            return string.Equals(key?.GetValue(ValueName) as string, CommandLine, StringComparison.OrdinalIgnoreCase);
        }
    }
    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(registryPath, true);
            key.SetValue(ValueName, CommandLine, RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(registryPath, true);
            key?.DeleteValue(ValueName, false);
        }
    }
}
