using System.Diagnostics;
using System.IO;
using System.Windows;

namespace SerialBroker.Gui;

public static class HelpDocument
{
    public static void Open()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "docs", "V2-USER-GUIDE.html");
            if (!File.Exists(path)) throw new FileNotFoundException("帮助文档缺失，请保留完整发布目录。", path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "无法打开帮助", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
}
