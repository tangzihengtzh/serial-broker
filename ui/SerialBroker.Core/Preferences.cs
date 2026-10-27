using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SerialBroker.Core;

public sealed record ConnectionProfile
{
    public string Port { get; set; } = "COM3";
    public uint Baud { get; set; } = 115200;
    public string Name { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public int TelnetPort { get; set; } = 7000;
    public bool TelnetEnabled { get; set; } = true;
    public bool Dtr { get; set; }
    public bool Rts { get; set; }
    public string LogRoot { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SerialBroker", "logs");
    public void Validate()
    {
        Port = NormalizePort(Port);
        if (Baud < 50 || Baud > 4000000) throw new ArgumentException("波特率需在50到4000000之间");
        if (TelnetPort < 1 || TelnetPort > 65535) throw new ArgumentException("Telnet端口需在1到65535之间");
        if (string.IsNullOrWhiteSpace(LogRoot)) throw new ArgumentException("请选择日志根目录");
        LogRoot = Path.GetFullPath(LogRoot);
        if (Name.Length > 200) throw new ArgumentException("备注名称不能超过200个字符");
    }
    public static string NormalizePort(string value)
    {
        var match = Regex.Match(value.Trim(), "^COM([0-9]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out int n) || n is < 1 or > 65535)
            throw new ArgumentException("请输入有效串口，例如COM3");
        return $"COM{n}";
    }
}
public sealed record WindowGeometry(double Width, double Height, double Left, double Top, bool Maximized);
public sealed class Preferences
{
    public List<ConnectionProfile> History { get; set; } = [];
    public string Theme { get; set; } = "Light";
    public WindowGeometry? Window { get; set; }
    public static string NormalizeTheme(string? theme) => theme is "Light" or "Dark" or "Warm" or "Blue" ? theme : "Light";
    public void Remember(ConnectionProfile profile)
    {
        profile.Validate();
        History.RemoveAll(x => string.Equals(x.Port, profile.Port, StringComparison.OrdinalIgnoreCase));
        History.Insert(0, profile with { });
        if (History.Count > 200) History.RemoveRange(200, History.Count - 200);
    }
}
public sealed class PreferenceStore(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public string? LastError { get; private set; }
    public Preferences Load()
    {
        try
        {
            var file = Path.Combine(DirectoryPath, "preferences.json");
            if (!File.Exists(file)) return new();
            var value = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(file)) ?? new();
            value.Theme = Preferences.NormalizeTheme(value.Theme);
            var clean = new List<ConnectionProfile>();
            foreach (var profile in value.History ?? [])
            {
                try { profile.Validate(); if (!clean.Any(x => x.Port == profile.Port)) clean.Add(profile); }
                catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException) { }
            }
            value.History = clean.Take(200).ToList();
            return value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { LastError = ex.Message; return new(); }
    }
    public void Save(Preferences preferences)
    {
        // 先写临时文件再替换，退出时不把活动连接或监视内容写进历史。
        Directory.CreateDirectory(DirectoryPath);
        string file = Path.Combine(DirectoryPath, "preferences.json"), temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(preferences, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, file, true);
    }
}
