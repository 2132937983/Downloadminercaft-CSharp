// Minecraft 原版 client.jar 下载器（图形界面）
// WPF 界面：XAML 以内嵌资源形式编进程序，运行时由 XamlReader 加载，
// 因此只需要 Windows 自带的 csc.exe 即可编译。

using System;
using System.IO;
using System.Windows;
using System.Windows.Markup;

namespace McDownloader
{
    static class AppMain
    {
        [STAThread]
        static void Main()
        {
            Mc.UserAgent = "mc-jar-downloader/2.0 (+gui)";
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += (s, e) =>
            {
                ShowError(e.Exception);
                e.Handled = true;
            };

            MainController controller;
            try
            {
                var settings = AppSettings.Load();
                ThemeManager.Init(app.Resources, settings.Theme, settings.Accent);
                app.Resources.MergedDictionaries.Add(LoadXaml<ResourceDictionary>("Theme.xaml"));
                controller = new MainController(settings);
            }
            catch (Exception e)
            {
                ShowError(e);
                return;
            }
            app.Run(controller.Window);
        }

        public static T LoadXaml<T>(string name)
        {
            using (var stream = typeof(AppMain).Assembly.GetManifestResourceStream("McDownloader." + name))
            {
                if (stream == null)
                    throw new FileNotFoundException("程序缺少内嵌资源：" + name);
                return (T)XamlReader.Load(stream);
            }
        }

        static void ShowError(Exception e)
        {
            MessageBox.Show(e.ToString(), "MC 原版下载器 - 发生错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
