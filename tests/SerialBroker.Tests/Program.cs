using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using SerialBroker.Core;
using SerialBroker.Gui;

namespace SerialBroker.Tests;

internal static class Program
{
    private static readonly List<string> Passed = [];
    private static string _root = "", _run = "";
    private static int _exit;
    [STAThread]
    private static int Main(string[] args)
    {
        _root = Path.GetFullPath(args[0]);
        bool production = args.Contains("--production");
        _run = Path.Combine(_root, "artifacts", "tests", (production ? "production-" : "functional-") + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(_run);
        string native = Path.Combine(_root, production ? "artifacts/app" : "build-test/Release", "SerialBroker.Native.dll");
        NativeLibrary.SetDllImportResolver(typeof(NativeSession).Assembly, (name, _, _) => name == "SerialBroker.Native" ? NativeLibrary.Load(native) : IntPtr.Zero);
        using var watchdog = new Timer(_ => { Console.Error.WriteLine("TEST TIMEOUT"); Environment.Exit(99); }, null, 180000, Timeout.Infinite);
        var app = new App { SuppressStartupWindow = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                if (production) await Production();
                else { Models(); await Native(); await Ui(); await HistoryClearUi(); await StartupUi(); }
                Console.WriteLine($"PASS {Passed.Count} checks; no physical serial devices used.");
            }
            catch (Exception ex) { _exit = 1; Console.Error.WriteLine(ex); }
            finally
            {
                File.WriteAllText(Path.Combine(_run, "results.json"), JsonSerializer.Serialize(new { ok = _exit == 0, checks = Passed.Count, passed = Passed, hardwareUsed = false, production, time = DateTimeOffset.Now }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(_exit);
            }
        }, DispatcherPriority.ApplicationIdle);
        return app.Run();
    }
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        Passed.Add(name); Console.WriteLine("PASS: " + name);
    }
    private static async Task Wait(Func<bool> condition, string failure)
    {
        var time = Stopwatch.StartNew();
        while (!condition()) { if (time.ElapsedMilliseconds > 5000) throw new TimeoutException(failure); await Task.Delay(20); }
    }
    private static ConnectionProfile Profile(int number, string name) => new() { Port = "COM" + number, Name = name, TelnetPort = NetworkFixture.FreePort(), LogRoot = Path.Combine(_run, "logs") };
    private static void Models()
    {
        Check(ConnectionProfile.NormalizePort(" com0007 ") == "COM7", "COM canonical identity");
        bool rejected = false; try { ConnectionProfile.NormalizePort("COM0"); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "Invalid COM rejected before native device access");
        ConnectionProfile a = Profile(62001, "🧭 开发板"), b = Profile(62002, "🧭 开发板");
        var preferences = new Preferences(); preferences.Remember(a); preferences.Remember(b); preferences.Remember(a with { Baud = 921600 });
        Check(preferences.History.Count == 2 && preferences.History[0].Baud == 921600, "History updates by COM, most recent first");
        Check(preferences.History.All(x => x.Name == "🧭 开发板"), "Duplicate Unicode and emoji remarks supported");
        var store = new PreferenceStore(Path.Combine(_run, "models")); preferences.Theme = "Warm"; store.Save(preferences);
        var restored = store.Load();
        Check(restored.History[0].Baud == 921600 && restored.Theme == "Warm", "History and theme survive restart");
        int suggestion = PortCatalog.Suggest([a with { TelnetPort = 7000 }], [7001], [7002]);
        Check(suggestion == 7003, "Telnet suggestion avoids history, drafts and listeners");
        Check(restored.History[0].TelnetPort == a.TelnetPort, "Historical Telnet port retained as editable default");
        Check(Preferences.NormalizeTheme("unknown") == "Light", "Unknown palette falls back to Light");
        WindowPlacementStore.Save(store.DirectoryPath, new(1240, 840, 100000, -50000, true));
        var placement = WindowPlacementStore.Load(store.DirectoryPath)!;
        var fit = WindowPlacementStore.Fit(placement, new(0, 0, 1920, 1080), 1080, 720);
        Check(placement.Maximized && fit.Right <= 1920 && fit.Top >= 0, "Window geometry restored within visible desktop");
        File.WriteAllText(Path.Combine(store.DirectoryPath, "preferences.json"), "invalid");
        Check(store.Load().History.Count == 0 && store.LastError != null, "Corrupt preferences do not prevent startup");
        Check(new MonitorEntry(DateTimeOffset.Now, "RX", "", [0, 13, 10, 255]).Render(false) == "\\x00\\r\\n\\xFF", "Monitor renders control and binary bytes without altering transport");
        var catalog = PortCatalog.Merge(
            [new("COM4", "USB-SERIAL CH340 (COM4)", true), new("COM12", "Silicon Labs CP210x USB to UART Bridge (COM12)", true)],
            [new() { Port = "COM4", DeviceName = "Old adapter (COM4)" }, new() { Port = "COM9", DeviceName = "USB Serial Port (COM9)" }],
            ["COM4", "COM7", "COM12"]);
        Check(catalog.Select(x => x.Port).SequenceEqual(new[] { "COM4", "COM7", "COM9", "COM12" }), "Device catalog merges system/history by COM and sorts numerically");
        Check(catalog[0].DisplayName == "USB-SERIAL CH340 (COM4)", "Present device uses Device Manager friendly name instead of stale history");
        Check(catalog[2].DisplayName.Contains("USB Serial Port (COM9)") && catalog[2].DisplayName.Contains("未连接"), "Offline history retains saved device identity with explicit offline label");
        Check(catalog[1].DisplayName == "COM7" && catalog[1].IsPresent, "Metadata-free registered port remains available as plain COM fallback");
        Check(PortCatalog.ResolvePort(catalog[3].DisplayName, catalog) == "COM12" && PortCatalog.ResolvePort(" com0007 ", catalog) == "COM7", "Friendly selection and manual COM both resolve to canonical transport identity");
        Check(new PortInfo("COM5", "USB-SERIAL CH340", true).DisplayName == "USB-SERIAL CH340 (COM5)", "Driver description without port receives COM suffix");
        rejected = false; try { PortCatalog.ResolvePort("Unlisted adapter (COM88)", catalog); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "Unlisted decorated text is not guessed into another COM target");
        var deviceStore = new PreferenceStore(Path.Combine(_run, "device-names"));
        var devicePreferences = new Preferences(); devicePreferences.Remember(a with { DeviceName = "USB-SERIAL CH340 (COM62001)" }); deviceStore.Save(devicePreferences);
        Check(deviceStore.Load().History[0].DeviceName == "USB-SERIAL CH340 (COM62001)", "Device friendly name survives history persistence");
        File.WriteAllText(Path.Combine(deviceStore.DirectoryPath, "preferences.json"), "{\"History\":[{\"Port\":\"COM4\"}]} ");
        Check(deviceStore.Load().History.Single().DeviceName == "", "Existing history without device-name field remains compatible");
        var actualDevices = PortCatalog.Enumerate([]);
        File.WriteAllText(Path.Combine(_run, "device-catalog.json"), JsonSerializer.Serialize(actualDevices));
        foreach (var device in actualDevices) Console.WriteLine($"DEVICE: {device.Port} => {device.DisplayName}");
    }
    private static async Task<(int Code, string Output)> Cli(params string[] args)
    {
        var info = new ProcessStartInfo(Path.Combine(_root, "artifacts", "app", "serialctl.exe")) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(10000);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { if (!process.HasExited) process.Kill(); throw; }
        return (process.ExitCode, (await stdout) + (await stderr));
    }
    private static async Task Native()
    {
        await using var da = new Device(); await using var db = new Device();
        using var a = new NativeSession(); using var b = new NativeSession();
        ConnectionProfile pa = Profile(62011, "A"), pb = Profile(62012, "B");
        await a.StartAsync(pa, da.Path); await b.StartAsync(pb, db.Path);
        Check(a.GetSnapshot().Running && b.GetSnapshot().Running, "Two independent native brokers run concurrently");
        using var ta = new TcpClient(); using var tb = new TcpClient();
        await ta.ConnectAsync("127.0.0.1", pa.TelnetPort); await tb.ConnectAsync("127.0.0.1", pb.TelnetPort);
        await Wait(() => a.GetSnapshot().Clients.Length > 0 && b.GetSnapshot().Clients.Length > 0, "Telnet peers not attached");
        await da.Send("device-A\r\n"); await db.Send("device-B\r\n");
        string ra = await NetworkFixture.ReadUntil(ta, "device-A"), rb = await NetworkFixture.ReadUntil(tb, "device-B");
        Check(!ra.Contains("device-B") && !rb.Contains("device-A"), "Each Telnet endpoint receives only its own device output");
        await ta.GetStream().WriteAsync(Encoding.UTF8.GetBytes("terminal-A\r"));
        await Wait(() => da.Received.Contains("terminal-A"), "Telnet TX absent");
        var result = await Cli("send", "--pipe", "SerialBroker." + pa.Port, "--text", "cli-A", "--cr");
        await Wait(() => da.Received.Contains("cli-A"), "CLI TX absent");
        Check(result.Code == 0 && !db.Received.Contains("cli-A"), "Existing serialctl shares target A with Telnet without cross-device TX");
        Check(a.GetSnapshot().Tx > 0 && b.GetSnapshot().Tx == 0, "RX/TX counters are independent");
        a.SetPolicy(0);
        result = await Cli("send", "--pipe", "SerialBroker." + pa.Port, "--text", "denied", "--timeout", "1000");
        Check(result.Code != 0 && !da.Received.Contains("denied"), "Read-only policy rejects CLI transmission");
        Check(b.GetSnapshot().Policy == 2, "Policy changes do not affect other devices");
        a.SetPolicy(1);
        await using var first = new Peer("SerialBroker." + pa.Port); await first.Connect();
        await using var second = new Peer("SerialBroker." + pa.Port); await second.Connect();
        await first.Send(4); await first.Until(7); await second.Send(4); await second.Until(9);
        Check(a.GetSnapshot().Writer != 0, "Single-writer policy grants one lease and rejects competing lease");
        await first.Transmit("leased-write"); await Wait(() => da.Received.Contains("leased-write"), "Lease TX absent");
        Check(da.Received.Contains("leased-write"), "Granted writer receives existing written acknowledgement");
        await first.Send(5); await first.Until(6); await second.Send(4); await second.Until(7);
        Check(a.GetSnapshot().Policy == 1, "Writer release allows another client to acquire");
        a.SetPolicy(2);
        using (var duplicate = new NativeSession())
        {
            bool failed = false; try { await duplicate.StartAsync(pa, da.Path); } catch (InvalidOperationException) { failed = true; }
            Check(failed && a.GetSnapshot().Running && b.GetSnapshot().Running, "Duplicate COM rejected without stopping existing instances");
        }
        using (var conflict = new NativeSession())
        {
            bool failed = false;
            try { await conflict.StartAsync(Profile(62013, "C") with { TelnetPort = pb.TelnetPort }, da.Path); } catch (InvalidOperationException) { failed = true; }
            Check(failed && b.GetSnapshot().Running, "Telnet bind conflict remains isolated to attempted connection");
        }
        var client = b.GetSnapshot().Clients[0]; b.Disconnect(client.Id);
        await Wait(() => b.GetSnapshot().Clients.Length == 0, "Client still attached");
        Check(b.GetSnapshot().Running, "Disconnect client leaves device broker running");
        await da.DisposeAsync();
        await Wait(() => !a.GetSnapshot().Running, "Device A fault not reported");
        Check(b.GetSnapshot().Running, "Device A failure does not stop device B");
        var stillAlive = await Cli("send", "--pipe", "SerialBroker." + pb.Port, "--text", "after-fault", "--cr");
        await Wait(() => db.Received.Contains("after-fault"), "Device B stopped after A fault");
        Check(stillAlive.Code == 0, "Unaffected device still accepts CLI after another device faults");
        await a.StopAsync(); await b.StopAsync();
        string[] logs = Directory.GetDirectories(pa.LogRoot);
        Check(logs.Length == 2 && logs.All(x => File.Exists(Path.Combine(x, "rx.bin")) && File.Exists(Path.Combine(x, "tx.bin")) && File.Exists(Path.Combine(x, "events.log"))), "Each device owns separate binary and event logs");
        string aFolder = logs.Single(x => x.Contains(pa.Port));
        Check(File.ReadAllText(Path.Combine(aFolder, "rx.bin")) == "device-A\r\n", "RX binary log preserves exact bytes");
        Check(File.ReadAllText(Path.Combine(aFolder, "tx.bin")).Contains("leased-write"), "TX log records acknowledged device writes");
    }
    private static async Task Ui()
    {
        string state = Path.Combine(_run, "workspace");
        var window = new WorkspaceWindow(state) { ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        window.Show(); await Task.Delay(100);
        try
        {
            Check(window.Sessions.Count == 0 && window.SessionTabs.SelectedItem == window.WelcomeTab, "Startup shows welcome page without opening devices");
            Capture(window, "welcome-Light");
            await using var aDevice = new Device(); await using var bDevice = new Device();
            ConnectionProfile aProfile = Profile(62021, "🧭 RK3588") with { DeviceName = "USB-SERIAL CH340 (COM62021)" },
                bProfile = Profile(62022, "🧭 RK3588") with { DeviceName = "Silicon Labs CP210x USB to UART Bridge (COM62022)" };
            var a = window.AddSession(aProfile); var b = window.AddSession(bProfile);
            await a.ConnectAsync(aProfile, aDevice.Path); await b.ConnectAsync(bProfile, bDevice.Path);
            await aDevice.Send("[RK3588] serial console ready\r\nroot@board:~# ");
            await bDevice.Send("[FPGA] initialization complete\r\nstatus: ready\r\n");
            await Task.Delay(350);
            Check(a.IsRunning && b.IsRunning && window.Sessions.Count == 2, "Actual WPF tabs connect two independent mock devices");
            Check(a.PortBox.Text == aProfile.DeviceName && b.PortBox.Text == bProfile.DeviceName, "Selected physical-port text shows full CH340 and CP210x device names");
            Check(a.ReadProfile().Port == aProfile.Port && b.ReadProfile().Port == bProfile.Port, "Actual GUI builds connection profiles using COM only, not display names");
            Check(b.PortDetails.Text == bProfile.DeviceName && b.PortDetails.TextWrapping == TextWrapping.Wrap, "Long selected device name has a complete wrapping display");
            window.SessionTabs.SelectedIndex = 1;
            Check(a.IsRunning && b.IsRunning && a.Title.Contains("🧭"), "Switching tabs preserves both connections and duplicate emoji names");
            a.PolicyBox.SelectedIndex = 0;
            Check(a.Snapshot.Policy == 0 && b.Snapshot.Policy == 2, "Actual policy dropdown targets only selected device");
            a.PolicyBox.SelectedIndex = 2;
            Check(!a.ConfigFields.IsEnabled && a.StopButton.IsEnabled, "Connected configuration protected; disconnect remains available");
            foreach (string theme in new[] { "Light", "Dark", "Warm", "Blue" })
            {
                window.ThemeBox.SelectedValue = theme;
                await Task.Delay(80); window.UpdateLayout();
                Check(window.Preferences.Theme == theme && a.MonitorBox.Background == (Brush)window.FindResource("FieldBrush"), $"Global {theme} palette also updates monitor");
                Capture(window, "session-" + theme);
            }
            a.HexBox.IsChecked = true;
            Check(a.MonitorBox.Items.Cast<MonitorLine>().Any(x => x.Direction == "RX" && x.Text.Contains("5B ")), "HEX switch renders received bytes through actual control");
            a.HexBox.IsChecked = false;
            b.PauseBox.IsChecked = true;
            ulong beforePause = b.Snapshot.Rx;
            await bDevice.Send("paused-output"); await Task.Delay(250);
            Check(b.Snapshot.Rx > beforePause && !b.MonitorBox.Items.Cast<MonitorLine>().Any(x => x.Text.Contains("paused-output")), "Pause affects only display while native RX continues");
            b.PauseBox.IsChecked = false; await bDevice.Send("resumed-output");
            await Wait(() => b.MonitorBox.Items.Cast<MonitorLine>().Any(x => x.Text.Contains("resumed-output")), "Monitor did not resume");
            Check(b.Snapshot.Running, "Monitor resumes on same live device");
            a.ClearButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(a.MonitorBox.Items.Count == 0 && a.Snapshot.Rx > 0, "Clear monitor retains native byte counts and forwarding state");
            var draft = window.AddSession(new ConnectionProfile { Port = "COM62023", TelnetPort = NetworkFixture.FreePort() });
            draft.PortBox.SelectedValue = aProfile.Port;
            await Task.Delay(60);
            Check(draft.NameBox.Text == aProfile.Name && draft.TelnetBox.Text == aProfile.TelnetPort.ToString(), "Actual editable history selector fills target settings");
            Check(draft.PortBox.Text.Contains(aProfile.DeviceName) && draft.CurrentPort == aProfile.Port, "History dropdown preserves adapter name while canonical COM identity is separate");
            Check(draft.PortBox.Items.Cast<PortInfo>().Any(x => x.Port == bProfile.Port && x.DisplayName.Contains("CP210x")), "Dropdown includes saved full names for other adapters");
            int manualPort = NetworkFixture.FreePort(); draft.TelnetBox.Text = manualPort.ToString();
            draft.PortBox.Text = "COM62024"; await Task.Delay(60);
            Check(draft.TelnetBox.Text == manualPort.ToString(), "Manually edited Telnet port is not replaced for new typed COM");
            var suggested = window.AddSession();
            Check(suggested.DraftTelnetPort != aProfile.TelnetPort && suggested.DraftTelnetPort != bProfile.TelnetPort && suggested.DraftTelnetPort != manualPort, "New tab avoids historical and current draft endpoints");
            await draft.CloseSessionAsync(); await suggested.CloseSessionAsync();
            window.SessionTabs.SelectedIndex = 1;
            using var tcp = new TcpClient(); await tcp.ConnectAsync("127.0.0.1", aProfile.TelnetPort);
            window.Width = 1260; window.Height = 850;
            window.Close(); await Task.Delay(100);
            Check(!window.IsVisible && a.Snapshot.Running && b.Snapshot.Running, "Window close hides to tray while both connections remain live");
            await tcp.GetStream().WriteAsync(Encoding.UTF8.GetBytes("while-hidden\r"));
            await Wait(() => aDevice.Received.Contains("while-hidden"), "Hidden app stopped forwarding");
            Check(aDevice.Received.Contains("while-hidden"), "Telnet forwarding continues while window is hidden");
            window.ShowFromTray();
            Check(window.IsVisible, "Tray restore reveals same workspace");
            await a.CloseSessionAsync();
            Check(b.Snapshot.Running, "Closing one tab leaves other device connected");
            await window.ExitAsync(false);
            Check(!a.Snapshot.Running && !b.Snapshot.Running && !window.IsVisible, "Explicit exit releases all native instances");
            var restored = new PreferenceStore(state).Load();
            Check(restored.History.Count == 2 && restored.Theme == "Blue", "UI-applied history and appearance persist without active connections");
            var geometry = WindowPlacementStore.Load(state);
            Check(geometry != null && Math.Abs(geometry.Width - 1260) < 2 && Math.Abs(geometry.Height - 850) < 2, "Last window size is saved on close");
            var reopened = new WorkspaceWindow(state) { ShowActivated = false, ShowInTaskbar = false };
            Check(reopened.Sessions.Count == 0 && reopened.Preferences.History.Count == 2, "Restart restores history but does not auto-connect");
            reopened.Show(); reopened.Left = -20000; reopened.Top = -20000;
            await using var lastDevice = new Device();
            var lastProfile = Profile(62025, "shutdown");
            var lastSession = reopened.AddSession(lastProfile); await lastSession.ConnectAsync(lastProfile, lastDevice.Path);
            reopened.ReleaseOnProcessExit();
            Check(!lastSession.Snapshot.Running, "Application-exit fallback synchronously releases active native devices");
            reopened.Close();
        }
        finally { await window.ExitAsync(false); }
    }
    private static async Task HistoryClearUi()
    {
        string state = Path.Combine(_run, "clear-history");
        var window = new WorkspaceWindow(state) { ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        window.Show(); await Task.Delay(100);
        try
        {
            Check(!window.ClearHistoryMenuItem.IsEnabled, "Clear-history menu item is disabled for empty history");
            window.SettingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(window.SettingsButton.ContextMenu.IsOpen && window.SettingsButton.ContextMenu.Items.Contains(window.ClearHistoryMenuItem)
                && window.SettingsButton.ContextMenu.Items.Contains(window.AutoStartMenuItem), "Settings dropdown contains both connection history and startup options");
            window.SettingsButton.ContextMenu.IsOpen = false;
            await using var device = new Device();
            var profile = Profile(62041, "保留在线连接 🧭") with { DeviceName = "USB-SERIAL CH340 (COM62041)" };
            var live = window.AddSession(profile); await live.ConnectAsync(profile, device.Path);
            var offline = Profile(62042, "旧设备") with { DeviceName = "Old adapter (COM62042)" };
            window.Preferences.Remember(offline); window.SavePreferences();
            var draft = window.AddSession(offline);
            draft.NameBox.Text = "尚未保存的备注"; draft.BaudBox.Text = "921600";
            draft.DtrBox.IsChecked = true; draft.RtsBox.IsChecked = true;
            draft.TelnetBox.Text = NetworkFixture.FreePort().ToString();
            var beforeDraft = draft.ReadProfile();
            window.ThemeBox.SelectedValue = "Warm";
            int confirmations = 0;
            window.ConfirmHistoryClear = () => { confirmations++; return false; };
            window.ClearHistoryMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(confirmations == 1 && window.Preferences.History.Count == 2 && new PreferenceStore(state).Load().History.Count == 2,
                "Cancel via actual clear-history menu item leaves memory and disk unchanged");
            Check(draft.PortBox.Items.Cast<PortInfo>().Any(x => x.Port == profile.Port), "Cancel keeps historical dropdown candidates");
            window.Width = window.MinWidth; window.Height = window.MinHeight;
            Capture(window, "clear-history-minimum-Warm");
            using var tcp = new TcpClient(); await tcp.ConnectAsync("127.0.0.1", profile.TelnetPort);
            await device.Send("before-history-clear\r\n");
            await Wait(() => live.MonitorBox.Items.Cast<MonitorLine>().Any(x => x.Text.Contains("before-history-clear")), "Initial monitor output absent");
            window.ConfirmHistoryClear = () => { confirmations++; return true; };
            window.ClearHistoryMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(confirmations == 2 && window.Preferences.History.Count == 0 && new PreferenceStore(state).Load().History.Count == 0,
                "Confirm via actual menu item clears history immediately in memory and on disk");
            Check(!window.ClearHistoryMenuItem.IsEnabled && window.HistoryHint.Text == "从新连接开始。", "Clear updates menu item and welcome history summary");
            Check(window.Sessions.All(x => !x.PortBox.Items.Cast<PortInfo>().Any(p => p.Port == offline.Port)), "Clear refreshes offline history candidates in every open tab");
            var presentPorts = PortCatalog.Enumerate([]).Select(x => x.Port);
            Check(presentPorts.All(p => draft.PortBox.Items.Cast<PortInfo>().Any(x => x.Port == p)), "Clearing history retains currently enumerated system ports");
            Check(draft.ReadProfile() == beforeDraft && draft.PortBox.Text.Contains(offline.DeviceName), "Clear preserves draft device name, COM and every editable setting");
            Check(live.IsRunning && live.CurrentPort == profile.Port && live.NameBox.Text == profile.Name, "Clear preserves active session identity and connection");
            Check(live.PortBox.Text == profile.DeviceName, "Clear preserves the active device friendly name outside historical dropdown candidates");
            Check(live.MonitorBox.Items.Cast<MonitorLine>().Any(x => x.Text.Contains("before-history-clear")), "Clear connection history retains monitor content");
            await tcp.GetStream().WriteAsync(Encoding.UTF8.GetBytes("tx-after-clear\r"));
            await Wait(() => device.Received.Contains("tx-after-clear"), "TX stopped after history clear");
            await device.Send("rx-after-clear\r\n");
            await Wait(() => live.MonitorBox.Items.Cast<MonitorLine>().Any(x => x.Text.Contains("rx-after-clear")), "RX stopped after history clear");
            Check(live.Snapshot.Running, "Telnet TX and device RX continue after clearing history");
            var fresh = window.AddSession(new ConnectionProfile { Port = "COM62043", TelnetPort = NetworkFixture.FreePort() });
            fresh.PortBox.Text = offline.Port;
            Check(fresh.NameBox.Text == "" && fresh.ReadProfile().DeviceName == "", "A new tab cannot refill a cleared device profile");
            Check(window.ClearConnectionHistory(), "Clearing empty history is harmless");
            window.ThemeBox.SelectedValue = "Blue";
            await window.ExitAsync(false);
            var restored = new PreferenceStore(state).Load();
            Check(restored.History.Count == 0 && restored.Theme == "Blue", "Theme save and exit do not resurrect cleared history");
            var reopened = new WorkspaceWindow(state) { ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
            reopened.Show();
            try
            {
                Check(reopened.Preferences.History.Count == 0 && reopened.Sessions.Count == 0 && !reopened.ClearHistoryMenuItem.IsEnabled,
                    "Restart keeps cleared history empty without auto-connecting");
                await using var nextDevice = new Device();
                var next = reopened.AddSession(profile); await next.ConnectAsync(profile, nextDevice.Path);
                Check(next.IsRunning, "New mock device connects after restart");
                Check(reopened.Preferences.History.Count == 1 && reopened.ClearHistoryMenuItem.IsEnabled, "A new connection can be remembered after clearing history");
                // A directory at the temporary-file path deterministically makes Save fail, even with administrator privileges.
                Directory.CreateDirectory(Path.Combine(state, "preferences.json.tmp"));
                reopened.ConfirmHistoryClear = () => true;
                reopened.ClearHistoryMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Check(reopened.Preferences.History.Count == 1 && new PreferenceStore(state).Load().History.Count == 1,
                    "Failed clear restores original in-memory history and preserves saved history");
                Check(reopened.WorkspaceStatus.Text.Contains("偏好设置未能保存") && reopened.ClearHistoryMenuItem.IsEnabled,
                    "Failed clear reports persistence error and leaves retry available");
                Check(next.IsRunning, "Failed clear does not interrupt active session");
                Directory.Delete(Path.Combine(state, "preferences.json.tmp"));
                reopened.ClearHistoryMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Check(reopened.Preferences.History.Count == 0 && !reopened.WorkspaceStatus.Text.Contains("偏好设置未能保存"), "Clear succeeds on retry and removes the stale save error");
                Capture(reopened, "clear-history-Blue");
            }
            finally { await reopened.ExitAsync(false); }
        }
        finally { await window.ExitAsync(false); }
    }
    private static async Task StartupUi()
    {
        string state = Path.Combine(_run, "启动设置 with spaces");
        string registryPath = @"Software\SerialBroker.Tests\Startup-" + Guid.NewGuid().ToString("N");
        string exe = @"C:\Serial Broker 测试\SerialBroker.exe";
        var startup = new StartupRegistration(exe, state, registryPath);
        try
        {
            Check(!startup.IsEnabled, "Startup is opt-in when no registration exists");
            Check(startup.CommandLine == $"\"{exe}\" --start-in-tray --data-directory \"{state}\"", "Startup command quotes executable and data directory with spaces and Unicode");
            var rootStartup = new StartupRegistration(exe, @"C:\", registryPath);
            Check(rootStartup.CommandLine.EndsWith("\"C:\\\\\""), "Startup command correctly escapes root-directory trailing backslash");
            var options = App.ParseArguments(["--start-in-tray", "--data-directory", state]);
            Check(options.StartInTray && options.DataDirectory == state, "Startup parser retains tray mode and the selected workspace");
            Check(App.ParseArguments(["--data-directory", state, "--start-in-tray"]) == options, "Startup options work in either order");
            Check(!App.ParseArguments([]).StartInTray && !App.ParseArguments(["--data-directory", state]).StartInTray,
                "Normal launches keep showing the main window by default");
            foreach (string[] invalid in new[] { new[] { "--unknown" }, new[] { "--data-directory" }, new[] { "--data-directory", "--start-in-tray" },
                new[] { "--start-in-tray", "--start-in-tray" }, new[] { "--data-directory", state, "--data-directory", state } })
            {
                bool rejected = false; try { App.ParseArguments(invalid); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "Invalid startup arguments rejected: " + string.Join(" ", invalid));
            }
            WindowPlacementStore.Save(state, new(1260, 850, 100, 120, true));
            var originalPlacement = WindowPlacementStore.Load(state);
            var window = new WorkspaceWindow(state, startup) { ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Start(true);
                Check(!window.IsVisible && !window.IsLoaded && PresentationSource.FromVisual(window) == null && window.Sessions.Count == 0,
                    "Tray startup creates no visible main window or active connections");
                Check(!window.AutoStartMenuItem.IsChecked, "Settings menu initially reflects disabled startup");
                window.AutoStartMenuItem.IsChecked = true;
                window.AutoStartMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Check(new StartupRegistration(exe, state, registryPath).IsEnabled, "Checking the actual settings item persists startup registration");
                using (var key = Registry.CurrentUser.OpenSubKey(registryPath))
                    Check((string?)key?.GetValue("SerialBroker") == startup.CommandLine, "Persisted Run command starts the correct executable and workspace in tray mode");
                await window.ExitAsync(false);
                Check(WindowPlacementStore.Load(state) == originalPlacement, "Exiting before showing a tray-started window preserves valid saved geometry");
            }
            finally { await window.ExitAsync(false); }

            var failure = new StartupFailureFixture(startup);
            var reopened = new WorkspaceWindow(state, failure) { ShowActivated = false, ShowInTaskbar = false, WindowState = WindowState.Normal, Left = -20000, Top = -20000 };
            try
            {
                Check(reopened.AutoStartMenuItem.IsChecked, "Reopened workspace reads enabled startup from the registry");
                reopened.Start(false);
                Check(reopened.IsVisible, "A normal manual launch still opens the window when autostart is enabled");
                reopened.Hide(); reopened.ShowFromTray();
                Check(reopened.IsVisible && reopened.Sessions.Count == 0, "Tray restore opens the same workspace without auto-connecting");
                reopened.Width = reopened.MinWidth; reopened.Height = reopened.MinHeight;
                reopened.SettingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(reopened.SettingsButton.ContextMenu.IsOpen && reopened.AutoStartMenuItem.IsCheckable, "Settings button opens a checkable startup option");
                reopened.SettingsButton.ContextMenu.IsOpen = false;
                Capture(reopened, "startup-settings-minimum");
                failure.FailWrite = true;
                reopened.AutoStartMenuItem.IsChecked = false;
                reopened.AutoStartMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Check(reopened.AutoStartMenuItem.IsChecked && startup.IsEnabled && reopened.WorkspaceStatus.Text.Contains("自启动设置未能保存"),
                    "Failed startup write restores actual check state and reports the error");
                failure.FailWrite = false;
                reopened.AutoStartMenuItem.IsChecked = false;
                reopened.AutoStartMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Check(!startup.IsEnabled && !reopened.WorkspaceStatus.Text.Contains("自启动设置未能保存"), "Unchecking removes startup registration and clears previous errors");
                using (var key = Registry.CurrentUser.OpenSubKey(registryPath, true)) key!.SetValue("OtherApp", "untouched");
                startup.SetEnabled(false);
                using (var key = Registry.CurrentUser.OpenSubKey(registryPath))
                    Check((string?)key?.GetValue("OtherApp") == "untouched", "Disabling startup is idempotent and retains unrelated registry values");
                startup.SetEnabled(true); reopened.RefreshStartupSetting();
                Check(reopened.AutoStartMenuItem.IsChecked, "Settings refresh reflects externally changed startup registration");
                reopened.Preferences.Remember(Profile(62051, "startup-history")); reopened.SavePreferences();
                Check(reopened.ClearConnectionHistory() && startup.IsEnabled, "Clearing connection history retains startup registration");
                failure.FailRead = true; reopened.RefreshStartupSetting();
                Check(!reopened.AutoStartMenuItem.IsEnabled && reopened.WorkspaceStatus.Text.Contains("自启动设置未能读取"), "Unreadable startup registration disables the option and reports the error");
                failure.FailRead = false; reopened.RefreshStartupSetting();
                Check(reopened.AutoStartMenuItem.IsEnabled && !reopened.WorkspaceStatus.Text.Contains("自启动设置未能读取"), "Startup setting recovers after read access is restored");
            }
            finally { await reopened.ExitAsync(false); }
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(registryPath, false); }
    }
    private static void Capture(Window window, string name)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(content);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(_run, name + ".png")); encoder.Save(stream);
    }
    private static async Task Production()
    {
        using var session = new NativeSession();
        bool rejected = false;
        try { await session.StartAsync(Profile(62031, "production-deny"), @"\\.\pipe\SerialBroker.Test.not-a-real-device"); }
        catch (InvalidOperationException ex) { rejected = ex.Message.Contains("disabled in production"); }
        Check(rejected && !session.GetSnapshot().Running, "Shipped native library rejects simulated transport before any COM access");
        var result = await Cli("--help"); Check(result.Code == 0, "Shipped serialctl executable starts and documents compatible commands");
        result = await Cli("--version"); Check(result.Code == 0 && result.Output.Contains("2.0.1"), "Shipped CLI reports V2.0.1");
        Check(typeof(WorkspaceWindow).Assembly.GetName().Version == new Version(2, 0, 1, 0), "Shipped GUI assembly reports V2.0.1");
        foreach (string file in new[] { "SerialBroker.Native.dll", "serialctl.exe" })
            Check(FileVersionInfo.GetVersionInfo(Path.Combine(_root, "artifacts", "app", file)).ProductVersion == "2.0.1", $"Shipped {file} metadata reports V2.0.1");
    }
}
