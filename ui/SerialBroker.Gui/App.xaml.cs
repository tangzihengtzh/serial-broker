using System.IO;
using System.Windows;

namespace SerialBroker.Gui;

public partial class App : Application
{
    internal bool SuppressStartupWindow { get; set; }
    internal static (string DataDirectory, bool StartInTray) ParseArguments(string[] args)
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SerialBroker", "workspace");
        bool startInTray = false, hasDirectory = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--start-in-tray" && !startInTray) startInTray = true;
            else if (args[i] == "--data-directory" && !hasDirectory && i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            { directory = Path.GetFullPath(args[++i]); hasDirectory = true; }
            else throw new ArgumentException("支持 --data-directory PATH 和 --start-in-tray，参数不能重复");
        }
        return (directory, startInTray);
    }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (SuppressStartupWindow) return;
        try
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var options = ParseArguments(e.Args);
            var window = new WorkspaceWindow(options.DataDirectory);
            MainWindow = window;
            window.Start(options.StartInTray);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Serial Broker 启动失败", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        // 系统结束会话/应用Shutdown也释放资源；不能只依赖托盘菜单的异步退出路径。
        if (MainWindow is WorkspaceWindow workspace) workspace.ReleaseOnProcessExit();
        base.OnExit(e);
    }
}
