using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using SerialBroker.Core;

namespace SerialBroker.Gui;

public sealed record MonitorLine(string Time, string Direction, string Text, Brush Color);

public partial class SessionView : UserControl
{
    private readonly WorkspaceWindow _workspace;
    private readonly NativeSession _native;
    private readonly DispatcherTimer _timer;
    private readonly Queue<MonitorEntry> _history = new();
    private readonly ObservableCollection<MonitorLine> _rows = [];
    private IReadOnlyList<PortInfo> _ports = [];
    private PortInfo? _displayedPort;
    private int _historyBytes;
    private bool _loading = true, _telnetEdited, _busy, _started, _closing;
    private string _lastPort = "";
    private Task _operation = Task.CompletedTask;
    private Task? _closeTask;
    public event Action? TitleChanged;
    private string ResolvePort(string value) => _displayedPort is { } displayed && string.Equals(value, displayed.DisplayName, StringComparison.OrdinalIgnoreCase)
        ? displayed.Port : PortCatalog.ResolvePort(value, _ports);
    internal string CurrentPort { get { try { return ResolvePort(PortBox.Text); } catch (ArgumentException) { return PortBox.Text; } } }
    public string Title => string.IsNullOrWhiteSpace(NameBox.Text) ? (string.IsNullOrWhiteSpace(PortBox.Text) ? "新串口" : CurrentPort) : $"{NameBox.Text.Trim()} · {CurrentPort}";
    public bool IsRunning { get; private set; }
    internal Snapshot Snapshot => _native.GetSnapshot();
    internal int? DraftTelnetPort => TelnetEnabledBox.IsChecked == true && int.TryParse(TelnetBox.Text, out int port) ? port : null;

    internal SessionView(WorkspaceWindow workspace, ConnectionProfile? profile = null)
    {
        _workspace = workspace;
        InitializeComponent();
        _native = new();
        MonitorBox.ItemsSource = _rows;
        BaudBox.ItemsSource = new[] { "9600", "19200", "38400", "57600", "115200", "230400", "460800", "921600", "1500000" };
        RefreshPorts();
        if (profile == null)
        {
            string candidate = _ports.OrderByDescending(x => x.IsPresent).FirstOrDefault(x => !_workspace.Sessions.Any(s => s.CurrentPort == x.Port))?.Port ?? "";
            profile = _workspace.Preferences.History.FirstOrDefault(x => x.Port == candidate) is { } known
                ? known with { } : new ConnectionProfile { Port = candidate, TelnetPort = SuggestPort() };
        }
        LoadProfile(profile);
        PortBox.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(PortTextChanged));
        PortBox.LostKeyboardFocus += (_, _) => { if (!PortBox.IsKeyboardFocusWithin && !_busy && !IsRunning) ShowPort(CurrentPort); };
        _loading = false;
        _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += Tick; _timer.Start();
    }
    private int SuggestPort() => PortCatalog.Suggest(_workspace.Preferences.History, _workspace.Sessions.Where(x => x != this).Select(x => x.DraftTelnetPort).OfType<int>());
    private void LoadProfile(ConnectionProfile profile, bool updatePort = true)
    {
        bool previous = _loading; _loading = true;
        if (!_ports.Any(x => x.Port == profile.Port) && !string.IsNullOrWhiteSpace(profile.Port))
        {
            _ports = _ports.Append(new PortInfo(profile.Port, profile.DeviceName, false)).ToArray();
            PortBox.ItemsSource = _ports;
        }
        if (updatePort) ShowPort(profile.Port);
        NameBox.Text = profile.Name; BaudBox.Text = profile.Baud.ToString();
        TelnetBox.Text = profile.TelnetPort.ToString(); TelnetEnabledBox.IsChecked = profile.TelnetEnabled;
        DtrBox.IsChecked = profile.Dtr; RtsBox.IsChecked = profile.Rts; LogBox.Text = profile.LogRoot;
        _lastPort = profile.Port; _telnetEdited = false; _loading = previous;
        TitleChanged?.Invoke();
    }
    internal void RefreshPorts()
    {
        bool previous = _loading; _loading = true;
        string text = CurrentPort;
        try { _ports = PortCatalog.Enumerate(_workspace.Preferences.History); PortBox.ItemsSource = _ports; ShowPort(text); }
        catch (Exception ex) { StatusText.Text = "端口列表读取失败：" + ex.Message; }
        _loading = previous;
    }
    private void ShowPort(string value)
    {
        bool previous = _loading; _loading = true;
        // 清除历史后保留当前标签的显示身份，但不把已清除的设备放回下拉候选。
        var item = _ports.FirstOrDefault(x => x.Port == value) ?? (_displayedPort?.Port == value ? _displayedPort : null);
        _displayedPort = item;
        PortBox.SelectedItem = item;
        PortBox.Text = item?.DisplayName ?? value;
        PortDetails.Text = item?.DisplayName ?? "";
        _loading = previous;
    }
    private void RefreshClick(object sender, RoutedEventArgs e) => RefreshPorts();
    private void NameChanged(object sender, TextChangedEventArgs e) { if (!_loading) TitleChanged?.Invoke(); }
    private void TelnetEdited(object sender, TextChangedEventArgs e) { if (!_loading) _telnetEdited = true; }
    private void PortSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PortBox.SelectedItem is not PortInfo port) return;
        ApplyPort(port.Port, true);
    }
    private void PortTextChanged(object sender, TextChangedEventArgs e) { if (!_loading) ApplyPort(PortBox.Text); }
    private void ApplyPort(string value, bool showDisplay = false)
    {
        if (_busy || IsRunning) return;
        string normalized;
        try { normalized = ResolvePort(value); } catch (ArgumentException) { PortDetails.Text = ""; TitleChanged?.Invoke(); return; }
        var item = _ports.FirstOrDefault(x => x.Port == normalized);
        PortDetails.Text = item?.DisplayName ?? "";
        if (normalized == _lastPort) { if (showDisplay) ShowPort(normalized); TitleChanged?.Invoke(); return; }
        _lastPort = normalized;
        var known = _workspace.Preferences.History.FirstOrDefault(x => x.Port == normalized);
        if (known != null) LoadProfile(known, showDisplay);
        else if (!_telnetEdited)
        {
            try { _loading = true; TelnetBox.Text = SuggestPort().ToString(); }
            catch (Exception ex) { StatusText.Text = ex.Message; }
            finally { _loading = false; }
        }
        if (showDisplay) ShowPort(normalized);
        TitleChanged?.Invoke();
    }
    internal ConnectionProfile ReadProfile()
    {
        if (!uint.TryParse(BaudBox.Text, out uint baud)) throw new ArgumentException("请输入有效波特率");
        if (!int.TryParse(TelnetBox.Text, out int tcp)) throw new ArgumentException("请输入有效Telnet端口");
        string port = ResolvePort(PortBox.Text);
        var value = new ConnectionProfile { Port = port, DeviceName = _ports.FirstOrDefault(x => x.Port == port)?.DeviceName ?? (_displayedPort?.Port == port ? _displayedPort.DeviceName : ""), Name = NameBox.Text, Baud = baud, TelnetPort = tcp,
            TelnetEnabled = TelnetEnabledBox.IsChecked == true, Dtr = DtrBox.IsChecked == true, Rts = RtsBox.IsChecked == true, LogRoot = LogBox.Text };
        value.Validate(); return value;
    }
    private async void StartClick(object sender, RoutedEventArgs e)
    {
        try { await ConnectAsync(ReadProfile()); } catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    internal Task ConnectAsync(ConnectionProfile profile, string? testPipe = null)
    {
        if (_closing || _busy || IsRunning) return Task.CompletedTask;
        return _operation = ConnectCoreAsync(profile, testPipe);
    }
    private async Task ConnectCoreAsync(ConnectionProfile profile, string? testPipe)
    {
        _busy = true; UpdateControls(); StatusText.Text = "正在连接…";
        try
        {
            profile.Validate();
            if (_ports.FirstOrDefault(x => x.Port == profile.Port && x.IsPresent) is { } device) profile.DeviceName = device.DeviceName;
            LoadProfile(profile);
            _workspace.Preferences.Remember(profile); _workspace.SavePreferences();
            // GUI没有模拟设备选项；生产原生库也会拒绝非空测试路径。
            if (testPipe == null) await _native.StartAsync(profile); else await _native.StartAsync(profile, testPipe);
            _loading = true;
            _ports = _ports.Select(x => x.Port == profile.Port ? x with { IsPresent = true } : x).ToArray();
            PortBox.ItemsSource = _ports; ShowPort(profile.Port); _loading = false;
            _started = true; IsRunning = true;
            StatusText.Text = "已连接";
            EndpointText.Text = $"{profile.Port} · {profile.Baud}" + (profile.TelnetEnabled ? $" · Telnet {profile.TelnetPort}" : "");
        }
        catch (Exception ex) { StatusText.Text = "连接失败：" + ex.Message; _started = false; IsRunning = false; }
        finally { _busy = false; UpdateControls(); _workspace.UpdateSummary(); }
    }
    private async void StopClick(object sender, RoutedEventArgs e) => await DisconnectAsync();
    internal Task DisconnectAsync(bool faulted = false)
    {
        if (_busy || _closing) return Task.CompletedTask;
        return _operation = DisconnectCoreAsync(faulted);
    }
    private async Task DisconnectCoreAsync(bool faulted)
    {
        _busy = true; UpdateControls();
        try { await _native.StopAsync(); }
        catch (Exception ex) { StatusText.Text = "停止失败：" + ex.Message; }
        finally
        {
            _busy = false; _started = false; IsRunning = false;
            StatusText.Text = faulted ? "连接已中断，请检查设备后手动重连。" : "已断开";
            ClientsBox.ItemsSource = null; UpdateControls(); _workspace.UpdateSummary();
        }
    }
    private void UpdateControls()
    {
        bool editable = !_busy && !IsRunning && !_closing;
        ConfigFields.IsEnabled = editable; LogBox.IsEnabled = editable;
        StartButton.IsEnabled = editable;
        StopButton.IsEnabled = !_busy && _started && !_closing;
        PolicyBox.IsEnabled = DisconnectButton.IsEnabled = !_busy && IsRunning && !_closing;
    }
    private void PolicyChanged(object sender, SelectionChangedEventArgs e) { if (!_loading && !_busy && IsRunning) _native.SetPolicy(PolicyBox.SelectedIndex); }
    private void DisconnectClick(object sender, RoutedEventArgs e) { if (ClientsBox.SelectedItem is ClientInfo client) _native.Disconnect(client.Id); }
    private void OpenLogsClick(object sender, RoutedEventArgs e)
    {
        try { string path = Path.GetFullPath(LogBox.Text); Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = "无法打开日志目录：" + ex.Message; }
    }
    private void Tick(object? sender, EventArgs e)
    {
        if (_closing) return;
        try
        {
            var state = _native.GetSnapshot();
            if (!_busy)
            {
                if (_started && !state.Running) { _ = DisconnectAsync(true); }
                else IsRunning = state.Running;
                _loading = true; PolicyBox.SelectedIndex = state.Policy; _loading = false;
                var old = (ClientsBox.ItemsSource as ClientInfo[]) ?? [];
                if (!old.SequenceEqual(state.Clients))
                {
                    ulong selected = (ClientsBox.SelectedItem as ClientInfo)?.Id ?? 0;
                    ClientsBox.ItemsSource = state.Clients;
                    ClientsBox.SelectedItem = state.Clients.FirstOrDefault(x => x.Id == selected);
                }
                CountersText.Text = $"RX  {state.Rx:N0} B       TX  {state.Tx:N0} B";
            }
            foreach (var entry in _native.Drain())
            {
                if (PauseBox.IsChecked == true && entry.Bytes != null) continue;
                _history.Enqueue(entry); _historyBytes += entry.Bytes?.Length ?? entry.Text.Length * 2;
                _rows.Add(Render(entry));
                while (_history.Count > 2000 || _historyBytes > 1024 * 1024)
                {
                    var removed = _history.Dequeue(); _historyBytes -= removed.Bytes?.Length ?? removed.Text.Length * 2; _rows.RemoveAt(0);
                }
            }
            if (_rows.Count > 0) MonitorBox.ScrollIntoView(_rows[^1]);
        }
        catch (Exception ex) { StatusText.Text = "监视更新失败：" + ex.Message; }
    }
    private MonitorLine Render(MonitorEntry entry) => new(entry.Time.ToString("HH:mm:ss.fff"), entry.Direction, entry.Render(HexBox.IsChecked == true),
        (Brush)FindResource(entry.Direction == "TX" ? "AccentBrush" : entry.Direction == "RX" ? "InkBrush" : "MutedBrush"));
    internal void RefreshAppearance() { _rows.Clear(); foreach (var entry in _history) _rows.Add(Render(entry)); }
    private void RenderModeChanged(object sender, RoutedEventArgs e) { if (!_loading) RefreshAppearance(); }
    private void ClearClick(object sender, RoutedEventArgs e) { _history.Clear(); _rows.Clear(); _historyBytes = 0; }
    internal Task CloseSessionAsync() => _closeTask ??= CloseCoreAsync();
    private async Task CloseCoreAsync()
    {
        _closing = true; _timer.Stop(); UpdateControls();
        await _operation;
        await _native.StopAsync();
        _native.Dispose(); IsRunning = false;
    }
    internal void ReleaseOnProcessExit()
    {
        // 原生回调只排队不等待UI，因此系统退出时可同步取消I/O并等待线程结束。
        _closing = true; _timer.Stop(); _native.Dispose(); IsRunning = false;
    }
}
