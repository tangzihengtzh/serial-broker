using System.ComponentModel;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using SerialBroker.Core;

namespace SerialBroker.Gui;

public partial class WorkspaceWindow : Window
{
    internal PreferenceStore Store { get; }
    internal Preferences Preferences { get; }
    internal List<SessionView> Sessions { get; } = [];
    internal Func<bool>? ConfirmHistoryClear { get; set; }
    private readonly TrayHost _tray;
    private readonly IStartupRegistration _startup;
    private bool _exiting, _ready;
    private string? _preferenceError;
    private string? _startupError;

    public WorkspaceWindow(string dataDirectory) : this(dataDirectory,
        new StartupRegistration(Environment.ProcessPath ?? throw new InvalidOperationException("无法读取程序路径"), dataDirectory)) { }

    internal WorkspaceWindow(string dataDirectory, IStartupRegistration startup)
    {
        _startup = startup;
        Store = new(dataDirectory);
        Preferences = Store.Load();
        _preferenceError = Store.LastError;
        InitializeComponent();
        AppTheme.Apply(Preferences.Theme);
        ThemeBox.SelectedValue = Preferences.Theme;
        var placement = WindowPlacementStore.Load(Store.DirectoryPath);
        if (placement != null)
        {
            var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var fit = WindowPlacementStore.Fit(placement, desktop, MinWidth, MinHeight);
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = fit.Width; Height = fit.Height; Left = fit.Left; Top = fit.Top;
            if (placement.Maximized) WindowState = WindowState.Maximized;
        }
        _tray = new(ShowFromTray, HelpDocument.Open, () => _ = ExitAsync());
        Closing += WindowClosing;
        Loaded += (_, _) => { _ready = true; UpdateSummary(); };
        RefreshStartupSetting();
        if (Store.LastError != null) WorkspaceStatus.Text = "历史记录未能读取：" + Store.LastError;
    }
    internal void Start(bool startInTray)
    {
        if (!startInTray) Show();
    }
    internal void RefreshStartupSetting()
    {
        try { AutoStartMenuItem.IsChecked = _startup.IsEnabled; AutoStartMenuItem.IsEnabled = true; _startupError = null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        { AutoStartMenuItem.IsEnabled = false; _startupError = "自启动设置未能读取：" + ex.Message; }
        UpdateSummary();
    }
    private void SettingsClick(object sender, RoutedEventArgs e)
    {
        RefreshStartupSetting();
        var button = (Button)sender;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }
    private void AutoStartClick(object sender, RoutedEventArgs e)
    {
        if (_exiting) return;
        try { _startup.SetEnabled(AutoStartMenuItem.IsChecked); _startupError = null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            RefreshStartupSetting();
            _startupError = "自启动设置未能保存：" + ex.Message;
        }
        UpdateSummary();
    }
    internal SessionView AddSession(ConnectionProfile? profile = null)
    {
        var view = new SessionView(this, profile);
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var close = new Button { Content = "×", MinHeight = 22, MinWidth = 22, Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(10, 0, 0, 0), Style = (Style)FindResource("QuietButton"), ToolTip = "关闭此设备连接" };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(label); header.Children.Add(close);
        var tab = new TabItem { Header = header, Content = view };
        void SetTitle() { label.Text = view.Title; UpdateSummary(); }
        view.TitleChanged += SetTitle;
        close.Click += async (_, _) =>
        {
            if (_exiting) return;
            close.IsEnabled = false;
            await view.CloseSessionAsync();
            Sessions.Remove(view); SessionTabs.Items.Remove(tab); UpdateSummary();
        };
        Sessions.Add(view); SessionTabs.Items.Add(tab); SessionTabs.SelectedItem = tab; SetTitle();
        return view;
    }
    internal bool SavePreferences()
    {
        try { Store.Save(Preferences); _preferenceError = null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _preferenceError = "偏好设置未能保存：" + ex.Message;
            UpdateSummary(); return false;
        }
        UpdateSummary();
        return true;
    }
    internal bool ClearConnectionHistory()
    {
        if (Preferences.History.Count == 0) return true;
        var previous = Preferences.History;
        Preferences.History = [];
        if (!SavePreferences())
        {
            Preferences.History = previous;
            UpdateSummary(); return false;
        }
        foreach (var session in Sessions) session.RefreshPorts();
        return true;
    }
    private void ClearHistoryClick(object sender, RoutedEventArgs e)
    {
        if (_exiting || Preferences.History.Count == 0) return;
        bool confirmed = ConfirmHistoryClear?.Invoke() ?? MessageBox.Show(this,
            $"清除全部 {Preferences.History.Count} 条历史连接及其保存的配置？\n\n当前连接和已填写的参数将保留，设备日志不会删除。下次连接时会重新记录。",
            "清除历史连接", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK;
        if (confirmed) ClearConnectionHistory();
    }
    internal void UpdateSummary()
    {
        HistoryHint.Text = Preferences.History.Count == 0 ? "从新连接开始。" : $"已记住 {Preferences.History.Count} 台设备的配置；启动时不会自动连接。";
        ClearHistoryMenuItem.IsEnabled = Preferences.History.Count > 0;
        WorkspaceStatus.Text = $"{Sessions.Count} 个连接标签 · {Sessions.Count(x => x.IsRunning)} 台在线"
            + (_preferenceError == null ? "" : " · " + _preferenceError) + (_startupError == null ? "" : " · " + _startupError);
    }
    private void NewClick(object sender, RoutedEventArgs e) { try { AddSession(); } catch (Exception ex) { WorkspaceStatus.Text = ex.Message; } }
    private void HelpClick(object sender, RoutedEventArgs e) => HelpDocument.Open();
    private async void ExitClick(object sender, RoutedEventArgs e) => await ExitAsync();
    private void ThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || ThemeBox.SelectedValue is not string theme) return;
        Preferences.Theme = theme; AppTheme.Apply(theme);
        foreach (var session in Sessions) session.RefreshAppearance();
        SavePreferences();
    }
    internal void ShowFromTray()
    {
        Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate();
    }
    private void SavePlacement()
    {
        // 托盘启动后可能直接退出，未显示的窗口没有有效几何，不能覆盖原布局。
        if (!IsLoaded) { SavePreferences(); return; }
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        try
        {
            WindowPlacementStore.Save(Store.DirectoryPath, new(bounds.Width, bounds.Height, bounds.Left, bounds.Top, WindowState == WindowState.Maximized));
            SavePreferences();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { WorkspaceStatus.Text = ex.Message; }
    }
    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        SavePlacement(); e.Cancel = true; Hide();
    }
    internal async Task ExitAsync(bool shutdownApplication = true)
    {
        if (_exiting) return;
        _exiting = true; IsEnabled = false; SavePlacement();
        // 显式退出必须等待全部原生I/O和回调结束，再销毁托盘与主窗口。
        await Task.WhenAll(Sessions.Select(x => x.CloseSessionAsync()));
        _tray.Dispose(); Close();
        if (shutdownApplication) Application.Current.Shutdown();
    }
    internal void ReleaseOnProcessExit()
    {
        _exiting = true;
        foreach (var session in Sessions) session.ReleaseOnProcessExit();
        _tray.Dispose();
    }
}
