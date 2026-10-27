using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SerialBroker.Core;

public sealed record PortInfo(string Port, string DeviceName, bool IsPresent)
{
    public string FriendlyName => string.IsNullOrWhiteSpace(DeviceName) ? Port
        : Regex.IsMatch(DeviceName, $@"\b{Regex.Escape(Port)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            ? DeviceName : $"{DeviceName} ({Port})";
    public string DisplayName => IsPresent ? FriendlyName : $"{FriendlyName} · 未连接（历史）";
    public override string ToString() => DisplayName;
}

public static class PortCatalog
{
    public static IReadOnlyList<PortInfo> Enumerate(IEnumerable<ConnectionProfile> history)
    {
        var ports = new List<string>();
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
        if (key != null) foreach (string name in key.GetValueNames())
            if (key.GetValue(name) is string port) ports.Add(port);
        return Merge(WindowsSerialDevices.Enumerate(), history, ports);
    }
    internal static IReadOnlyList<PortInfo> Merge(IEnumerable<PortInfo> devices, IEnumerable<ConnectionProfile> history, IEnumerable<string> registered)
    {
        var ports = new Dictionary<string, PortInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in history) ports[profile.Port] = new(profile.Port, profile.DeviceName, false);
        foreach (string value in registered)
        {
            string port;
            try { port = ConnectionProfile.NormalizePort(value); } catch (ArgumentException) { continue; }
            ports[port] = new(port, "", true);
        }
        foreach (var device in devices) ports[device.Port] = device;
        return ports.Values.OrderBy(x => int.Parse(x.Port.AsSpan(3))).ToArray();
    }
    public static string ResolvePort(string value, IEnumerable<PortInfo> devices)
    {
        // 展示名称只在UI层映射，不能把整段友好名称交给CreateFile或拼进管道名。
        var item = devices.FirstOrDefault(x => string.Equals(x.DisplayName, value, StringComparison.OrdinalIgnoreCase));
        return item?.Port ?? ConnectionProfile.NormalizePort(value);
    }
    public static int Suggest(IEnumerable<ConnectionProfile> history, IEnumerable<int> drafts, IEnumerable<int>? listeners = null)
    {
        var used = history.Select(x => x.TelnetPort).Concat(drafts).Concat(listeners ?? IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(x => x.Port)).ToHashSet();
        for (int candidate = 7000; candidate <= 65535; candidate++) if (!used.Contains(candidate)) return candidate;
        throw new InvalidOperationException("没有可建议的Telnet端口，请手动规划端口");
    }
}
