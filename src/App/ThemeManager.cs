// 主题：浅色 / 深色（可跟随系统）+ 主题色。所有颜色以画刷资源写入 Application.Resources，
// 界面通过 DynamicResource 引用，切换时即时生效。

using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace McDownloader
{
    public sealed class AccentInfo
    {
        public string Key;
        public string Name;
        public Color Base;
        public Color Gradient; // 首页横幅渐变的另一端

        public AccentInfo(string key, string name, string baseHex, string gradientHex)
        {
            Key = key;
            Name = name;
            Base = ThemeManager.Hex(baseHex);
            Gradient = ThemeManager.Hex(gradientHex);
        }
    }

    public static class ThemeManager
    {
        public static readonly AccentInfo[] Accents =
        {
            new AccentInfo("blue", "天蓝", "#1E6FE8", "#5B5BF0"),
            new AccentInfo("purple", "星紫", "#7C4DFF", "#C04DE8"),
            new AccentInfo("green", "草绿", "#16A34A", "#0EA5A4"),
            new AccentInfo("teal", "湖青", "#0891B2", "#2563EB"),
            new AccentInfo("orange", "落日", "#EA580C", "#E11D74"),
            new AccentInfo("pink", "樱粉", "#DB2777", "#9333EA"),
        };

        // 版本类型标签的基础色
        static readonly string[][] TypeColors =
        {
            new[] { "release", "#16A34A" },
            new[] { "snapshot", "#D97706" },
            new[] { "april", "#DB2777" },
            new[] { "old_alpha", "#7C3AED" },
            new[] { "old_beta", "#0284C7" },
        };

        public static bool IsDark { get; private set; }

        public static event Action Changed;

        static ResourceDictionary target;
        static string mode = "system", accentKey = "blue";

        public static void Init(ResourceDictionary resources, string themeMode, string accent)
        {
            target = resources;
            SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                if (mode == "system" && (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Color))
                    Application.Current.Dispatcher.BeginInvoke(new Action(Reapply));
            };
            Apply(themeMode, accent);
        }

        public static void Apply(string themeMode, string accent)
        {
            mode = themeMode;
            accentKey = accent;
            Reapply();
        }

        static void Reapply()
        {
            bool dark = mode == "dark" || (mode == "system" && SystemPrefersDark());
            IsDark = dark;
            var accent = FindAccent(accentKey);

            if (dark)
            {
                Set("WindowBg", "#1C1C1C");
                Set("LayerBg", "#262626");
                Set("LayerBorder", "#303030");
                Set("CardBg", "#2D2D2D");
                Set("CardBorder", "#383838");
                Set("TextPrimary", "#FFFFFF");
                Set("TextSecondary", "#C5C5C5");
                Set("TextTertiary", "#9A9A9A");
                Set("Divider", "#383838");
                Set("ControlBg", "#353535");
                Set("ControlBorder", "#454545");
                Set("ControlBorderStrong", "#8A8A8A");
                Set("SubtleHover", "#14FFFFFF");
                Set("SubtlePressed", "#0AFFFFFF");
                Set("SubtleSelected", "#14FFFFFF");
                Set("ProgressTrack", "#404040");
                Set("ScrollThumb", "#55FFFFFF");
                Set("ScrollThumbHover", "#88FFFFFF");
                Set("OverlayBg", "#99000000");
                Set("Success", "#6CCB5F");
                Set("SuccessSubtle", "#2E6CCB5F");
                Set("Warning", "#FCE100");
                Set("WarningSubtle", "#2EFCE100");
                Set("Danger", "#FF99A4");
                Set("DangerSubtle", "#2EFF99A4");
            }
            else
            {
                Set("WindowBg", "#EEF0F3");
                Set("LayerBg", "#F7F8FA");
                Set("LayerBorder", "#E1E3E8");
                Set("CardBg", "#FFFFFF");
                Set("CardBorder", "#E6E8EC");
                Set("TextPrimary", "#1A1A1A");
                Set("TextSecondary", "#5C5F66");
                Set("TextTertiary", "#8C9099");
                Set("Divider", "#ECEEF1");
                Set("ControlBg", "#FFFFFF");
                Set("ControlBorder", "#D9DCE1");
                Set("ControlBorderStrong", "#9A9EA6");
                Set("SubtleHover", "#0A000000");
                Set("SubtlePressed", "#14000000");
                Set("SubtleSelected", "#0D000000");
                Set("ProgressTrack", "#E3E5E9");
                Set("ScrollThumb", "#40000000");
                Set("ScrollThumbHover", "#70000000");
                Set("OverlayBg", "#66000000");
                Set("Success", "#0F7B0F");
                Set("SuccessSubtle", "#1F0F7B0F");
                Set("Warning", "#9D5D00");
                Set("WarningSubtle", "#269D5D00");
                Set("Danger", "#C42B1C");
                Set("DangerSubtle", "#1FC42B1C");
            }

            // 主题色：深色模式下提亮，保证在深色背景上的可读性
            var a = dark ? Mix(accent.Base, Colors.White, 0.35) : accent.Base;
            Set("Accent", a);
            Set("AccentText", dark ? Mix(accent.Base, Colors.White, 0.45) : Mix(accent.Base, Colors.Black, 0.08));
            Set("AccentSubtle", Color.FromArgb((byte)(dark ? 0x30 : 0x1C), accent.Base.R, accent.Base.G, accent.Base.B));
            Set("TextOnAccent", Luminance(a) > 0.45 ? Colors.Black : Colors.White);
            Set("HeroButtonFg", Mix(accent.Base, Colors.Black, 0.12));
            var hero = new LinearGradientBrush(
                dark ? Mix(accent.Base, Colors.Black, 0.18) : accent.Base,
                dark ? Mix(accent.Gradient, Colors.Black, 0.18) : accent.Gradient,
                new Point(0, 0), new Point(1, 1));
            hero.Freeze();
            target["HeroBg"] = hero;

            foreach (var tc in TypeColors)
            {
                var c = Hex(tc[1]);
                var name = Pascal(tc[0]);
                Set("Tag" + name + "Fg", dark ? Mix(c, Colors.White, 0.42) : Mix(c, Colors.Black, 0.12));
                Set("Tag" + name + "Bg", Color.FromArgb((byte)(dark ? 0x38 : 0x1E), c.R, c.G, c.B));
            }

            if (Changed != null)
                Changed();
        }

        public static AccentInfo FindAccent(string key)
        {
            foreach (var a in Accents)
                if (a.Key == key)
                    return a;
            return Accents[0];
        }

        static bool SystemPrefersDark()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var v = key == null ? null : key.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        static string Pascal(string type)
        {
            switch (type)
            {
                case "old_alpha": return "OldAlpha";
                case "old_beta": return "OldBeta";
                default: return char.ToUpperInvariant(type[0]) + type.Substring(1);
            }
        }

        static void Set(string key, string hex)
        {
            Set(key, Hex(hex));
        }

        static void Set(string key, Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            target[key] = b;
        }

        public static Color Hex(string s)
        {
            return (Color)ColorConverter.ConvertFromString(s);
        }

        public static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb(
                (byte)Math.Round(a.A + (b.A - a.A) * t),
                (byte)Math.Round(a.R + (b.R - a.R) * t),
                (byte)Math.Round(a.G + (b.G - a.G) * t),
                (byte)Math.Round(a.B + (b.B - a.B) * t));
        }

        static double Luminance(Color c)
        {
            Func<byte, double> ch = v =>
            {
                double s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            };
            return 0.2126 * ch(c.R) + 0.7152 * ch(c.G) + 0.0722 * ch(c.B);
        }
    }
}
