// 设置：保存在 %APPDATA%\MCDownloader\settings.json，修改后立即写入。

using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace McDownloader
{
    public sealed class AppSettings
    {
        public string OutDir { get; set; }
        public int Concurrency { get; set; }
        public string Priority { get; set; } // 优先下载源：mojang / bmclapi
        public string Theme { get; set; } // light / dark / system
        public string Accent { get; set; }

        static string FilePath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MCDownloader", "settings.json");
            }
        }

        public static AppSettings Load()
        {
            AppSettings s = null;
            try
            {
                if (File.Exists(FilePath))
                    s = new JavaScriptSerializer().Deserialize<AppSettings>(File.ReadAllText(FilePath, Encoding.UTF8));
            }
            catch (Exception)
            {
            }
            s = s ?? new AppSettings();
            if (string.IsNullOrWhiteSpace(s.OutDir))
                s.OutDir = Mc.DefaultDownloadDir();
            s.Concurrency = s.Concurrency == 0 ? 4 : Math.Max(1, Math.Min(8, s.Concurrency));
            if (s.Priority != Mc.Mojang && s.Priority != Mc.Bmclapi)
                s.Priority = Mc.Mojang;
            if (s.Theme != "light" && s.Theme != "dark" && s.Theme != "system")
                s.Theme = "system";
            s.Accent = ThemeManager.FindAccent(s.Accent).Key;
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(this), Encoding.UTF8);
            }
            catch (Exception)
            {
            }
        }
    }
}
