using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace SerialBroker.Core;

internal static class WindowsSerialDevices
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DeviceInstance;
        public UIntPtr Reserved;
    }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr devices, uint index, ref DeviceInfo device);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiOpenDevRegKey(IntPtr devices, ref DeviceInfo device, uint scope, uint profile, uint keyType, uint access);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr devices, ref DeviceInfo device, uint property, out uint type, byte[] buffer, uint capacity, out uint required);

    internal static IReadOnlyList<PortInfo> Enumerate()
    {
        var result = new List<PortInfo>();
        // 与设备管理器相同的数据源：只查询在场设备的注册信息，不打开COM、不改变DTR/RTS。
        // 遍历所有设备类，避免遗漏被厂商驱动放在自定义类别下的串口。
        IntPtr devices = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, 0x02 | 0x04);
        if (devices == new IntPtr(-1)) return result;
        try
        {
            for (uint index = 0; ; index++)
            {
                var device = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
                if (!SetupDiEnumDeviceInfo(devices, index, ref device)) break;
                IntPtr keyHandle = SetupDiOpenDevRegKey(devices, ref device, 1, 0, 1, 1);
                if (keyHandle == new IntPtr(-1)) continue;
                try
                {
                    using var handle = new SafeRegistryHandle(keyHandle, true);
                    using var key = RegistryKey.FromHandle(handle);
                    if (key.GetValue("PortName") is not string name) continue;
                    string port;
                    try { port = ConnectionProfile.NormalizePort(name); } catch (ArgumentException) { continue; }
                    // SPDRP_FRIENDLYNAME优先，缺失时退回设备描述；保留驱动提供的名称原文。
                    string description = Property(devices, ref device, 0x0c);
                    if (string.IsNullOrWhiteSpace(description)) description = Property(devices, ref device, 0x00);
                    result.Add(new(port, description, true));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(devices); }
        return result;
    }
    private static string Property(IntPtr devices, ref DeviceInfo device, uint property)
    {
        byte[] data = new byte[1024];
        if (!SetupDiGetDeviceRegistryPropertyW(devices, ref device, property, out _, data, (uint)data.Length, out uint required))
        {
            if (Marshal.GetLastWin32Error() != 122 || required > 65536) return "";
            data = new byte[required];
            if (!SetupDiGetDeviceRegistryPropertyW(devices, ref device, property, out _, data, (uint)data.Length, out required)) return "";
        }
        return Encoding.Unicode.GetString(data, 0, (int)Math.Min(required, (uint)data.Length)).TrimEnd('\0');
    }
}
