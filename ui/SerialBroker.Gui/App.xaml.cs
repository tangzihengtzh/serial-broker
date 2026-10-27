using System.IO;
using System.Windows;

namespace SerialBroker.Gui;

public partial class App : Application
{
    internal bool SuppressStartupWindow { get; set; }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (SuppressStartupWindow) return;
        try
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SerialBroker", "workspace");
            if (e.Args.Length != 0)
            {
                if (e.Args.Length != 2 || e.Args[0] != "--data-directory") throw new ArgumentException("仅支持 --data-directory PATH");
                directory = Path.GetFullPath(e.Args[1]);
            }
            var window = new WorkspaceWindow(directory);
            MainWindow = window;
            window.Show();
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
