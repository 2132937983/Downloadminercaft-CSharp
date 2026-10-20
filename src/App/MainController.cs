// 主窗口逻辑：界面由 MainWindow.xaml 描述，这里按 x:Name 绑定事件，并驱动清单加载与批量下载。

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using System.Windows.Shell;
using System.Windows.Threading;

namespace McDownloader
{
    sealed class MainController : IBatchListener
    {
        public readonly Window Window;
        readonly AppSettings settings;
        readonly MainViewModel vm = new MainViewModel();

        // 后台线程只往队列里放回调，由界面定时器统一执行
        readonly ConcurrentQueue<Action> uiQueue = new ConcurrentQueue<Action>();
        readonly DispatcherTimer pollTimer, searchTimer, toastTimer;

        readonly Dictionary<string, FrameworkElement> pages;
        readonly Dictionary<string, RadioButton> navs;
        string currentPage = "home";

        VersionCatalog catalog;
        readonly HashSet<string> checkedVids = new HashSet<string>();
        List<VersionItem> versionItems = new List<VersionItem>();
        ListCollectionView versionView;
        string typeFilter = "all", sourceFilter = "all", search = "";
        bool loading, bulkUpdate;

        ObservableCollection<TaskItem> tasks;
        Dictionary<string, TaskItem> taskMap = new Dictionary<string, TaskItem>();
        long totalSize;
        bool running, confirmedExit;

        int filesVersion, toastVersion;
        Action dialogOk;

        public MainController(AppSettings settings)
        {
            this.settings = settings;
            Window = AppMain.LoadXaml<Window>("MainWindow.xaml");
            Window.DataContext = vm;
            Window.TaskbarItemInfo = new TaskbarItemInfo();

            pages = new Dictionary<string, FrameworkElement>
            {
                { "home", Find<FrameworkElement>("PageHome") },
                { "versions", Find<FrameworkElement>("PageVersions") },
                { "downloads", Find<FrameworkElement>("PageDownloads") },
                { "files", Find<FrameworkElement>("PageFiles") },
                { "settings", Find<FrameworkElement>("PageSettings") },
            };
            navs = new Dictionary<string, RadioButton>
            {
                { "home", Find<RadioButton>("NavHome") },
                { "versions", Find<RadioButton>("NavVersions") },
                { "downloads", Find<RadioButton>("NavDownloads") },
                { "files", Find<RadioButton>("NavFiles") },
                { "settings", Find<RadioButton>("NavSettings") },
            };
            foreach (var kv in navs)
            {
                var key = kv.Key;
                kv.Value.Checked += delegate { Navigate(key); };
            }

            pollTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(60) };
            pollTimer.Tick += delegate { Drain(); };
            pollTimer.Start();
            searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            searchTimer.Tick += delegate { searchTimer.Stop(); ApplyFilter(); };
            toastTimer = new DispatcherTimer();
            toastTimer.Tick += delegate { toastTimer.Stop(); HideToast(); };

            WireChrome();
            WireHome();
            WireVersions();
            WireDownloads();
            WireFiles();
            WireSettings();
            WireDialog();
            Window.AddHandler(Hyperlink.RequestNavigateEvent, new RequestNavigateEventHandler((s, e) =>
            {
                OpenShell(e.Uri.AbsoluteUri);
                e.Handled = true;
            }));
            Window.Closing += OnClosing;
            ThemeManager.Changed += delegate
            {
                if (Handle != IntPtr.Zero)
                    Native.SetWindowFrame(Handle, ThemeManager.IsDark);
            };

            ApplySettingsToView();
            UpdateTotals();
            LoadManifests();
            RefreshFiles();
        }

        T Find<T>(string name) where T : class
        {
            var o = Window.FindName(name) as T;
            if (o == null)
                throw new InvalidOperationException("界面缺少元素：" + name);
            return o;
        }

        IntPtr Handle
        {
            get { return new WindowInteropHelper(Window).Handle; }
        }

        void Drain()
        {
            Action action;
            while (uiQueue.TryDequeue(out action))
                action();
        }

        // ---------- 窗口外框 ----------

        void WireChrome()
        {
            Find<Button>("BtnMin").Click += delegate { Window.WindowState = WindowState.Minimized; };
            Find<Button>("BtnMax").Click += delegate
            {
                Window.WindowState = Window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            };
            Find<Button>("BtnClose").Click += delegate { Window.Close(); };
            Window.StateChanged += delegate { UpdateFrame(); };
            Window.SourceInitialized += delegate
            {
                UpdateFrame();
                Native.SetWindowFrame(Handle, ThemeManager.IsDark);
            };
            var hero = Find<Border>("Hero");
            hero.SizeChanged += (s, e) => hero.Clip = new RectangleGeometry(new Rect(e.NewSize), 14, 14);
        }

        void UpdateFrame()
        {
            var root = Find<Border>("RootBorder");
            var max = Find<Button>("BtnMax");
            if (Window.WindowState == WindowState.Maximized)
            {
                var source = PresentationSource.FromVisual(Window);
                double scale = source != null && source.CompositionTarget != null ? source.CompositionTarget.TransformToDevice.M11 : 1.0;
                root.BorderThickness = new Thickness(0);
                root.Margin = Native.MaximizedFrame(scale);
                max.Content = "";
                max.ToolTip = "向下还原";
            }
            else
            {
                root.Margin = new Thickness(0);
                // Windows 11 由系统绘制圆角边框；Windows 10 自己画 1px 边
                root.BorderThickness = new Thickness(Native.IsWindows11 ? 0 : 1);
                max.Content = "";
                max.ToolTip = "最大化";
            }
        }

        // ---------- 导航 ----------

        void Navigate(string key)
        {
            if (navs[key].IsChecked != true)
                navs[key].IsChecked = true; // 会再次进入 Navigate
            if (currentPage == key && pages[key].Visibility == Visibility.Visible)
                return;
            currentPage = key;
            foreach (var kv in pages)
                kv.Value.Visibility = kv.Key == key ? Visibility.Visible : Visibility.Collapsed;
            AnimateIn(pages[key], 16);
            if (key == "files")
                RefreshFiles();
        }

        static void AnimateIn(UIElement el, double offset)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var shift = new TranslateTransform(0, offset);
            el.RenderTransform = shift;
            el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offset, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        }

        // ---------- 首页 ----------

        void WireHome()
        {
            Find<Button>("HeroDownload").Click += delegate { DownloadLatest("release"); };
            Find<Button>("BtnSnapshot").Click += delegate { DownloadLatest("snapshot"); };
            Find<Button>("HeroBrowse").Click += delegate
            {
                SetTypeFilter("all");
                Navigate("versions");
            };
            Find<Button>("BtnReload").Click += delegate { LoadManifests(); };
            Find<Button>("BtnHomeOpenDir").Click += delegate { OpenOutDir(); };
            Find<ItemsControl>("StatsList").AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((s, e) =>
            {
                var b = e.OriginalSource as Button;
                if (b == null || !(b.Tag is string))
                    return;
                SetTypeFilter((string)b.Tag);
                Navigate("versions");
            }));
        }

        void DownloadLatest(string kind)
        {
            var vid = catalog == null ? null : catalog.Latest(kind);
            if (vid != null)
                StartDownload(new[] { vid });
        }

        // ---------- 清单加载 ----------

        void LoadManifests()
        {
            if (loading)
                return;
            loading = true;
            vm.IsLoading = true;
            vm.VersionsEmpty = false;
            vm.Mojang.SetLoading();
            vm.Bmclapi.SetLoading();
            var t = new Thread(() =>
            {
                var results = ManifestResult.FetchBoth();
                uiQueue.Enqueue(() => OnManifestsLoaded(results));
            });
            t.IsBackground = true;
            t.Start();
        }

        void OnManifestsLoaded(List<ManifestResult> results)
        {
            loading = false;
            catalog = new VersionCatalog(results);
            vm.Mojang.SetResult(catalog.Result(Mc.Mojang));
            vm.Bmclapi.SetResult(catalog.Result(Mc.Bmclapi));

            var counts = catalog.TypeCounts();
            vm.Stats = VersionCatalog.TypeOrder.Select(t => new StatVm
            {
                Type = t,
                Label = Labels.Type(t),
                Glyph = Labels.TypeGlyph(t),
                Count = counts[t],
            }).ToList();

            var release = catalog.Latest("release");
            vm.LatestRelease = release ?? "—";
            vm.LatestReleaseDetail = release != null ? "发布于 " + catalog.All[release].Date + " · " + SourceSummary(release) : "版本清单不可用";
            vm.CanDownloadRelease = release != null;
            var snapshot = catalog.Latest("snapshot");
            vm.LatestSnapshot = snapshot ?? "暂无预览版";
            vm.LatestSnapshotDetail = snapshot != null ? "最新预览版 · 发布于 " + catalog.All[snapshot].Date : "版本清单不可用";
            vm.CanDownloadSnapshot = snapshot != null;

            BuildVersionItems();
            vm.IsLoading = false;
            ApplyFilter();
            UpdateSelection();

            var failed = results.Where(r => r.Data == null).ToList();
            if (failed.Count == results.Count)
                Toast("两个源都无法连接，请检查网络后在首页点击「重新获取清单」", "error");
            else if (failed.Count > 0)
                Toast("部分源加载失败：" + string.Join("；", failed.Select(r => Mc.SourceName(r.Source) + " " + r.Error.Message)), "warning");
        }

        string SourceSummary(string vid)
        {
            var v = catalog.All[vid];
            if (v.Has(Mc.Mojang) && v.Has(Mc.Bmclapi))
                return "两个源均可下载";
            return "仅 " + Mc.SourceName(v.Has(Mc.Mojang) ? Mc.Mojang : Mc.Bmclapi) + " 可下载";
        }

        // ---------- 版本列表 ----------

        void WireVersions()
        {
            var box = Find<TextBox>("SearchBox");
            var clear = Find<Button>("BtnClearSearch");
            box.TextChanged += delegate
            {
                clear.Visibility = box.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                search = box.Text.Trim().ToLowerInvariant();
                searchTimer.Stop();
                searchTimer.Start();
            };
            clear.Click += delegate
            {
                box.Text = "";
                box.Focus();
            };
            foreach (var name in new[] { "SrcAll", "SrcMojang", "SrcBmclapi" })
            {
                var rb = Find<RadioButton>(name);
                rb.Checked += delegate
                {
                    sourceFilter = (string)rb.Tag;
                    ApplyFilter();
                };
            }
            foreach (var rb in Find<Panel>("TypeChips").Children.OfType<RadioButton>())
            {
                var chip = rb;
                chip.Checked += delegate
                {
                    typeFilter = (string)chip.Tag;
                    ApplyFilter();
                };
            }
            Find<Button>("BtnSelectVisible").Click += delegate { SetChecked(versionItems.Where(Filter), true); };
            Find<Button>("BtnUnselectVisible").Click += delegate { SetChecked(versionItems.Where(Filter), false); };
            Find<Button>("BtnClearSelection").Click += delegate
            {
                SetChecked(versionItems, false);
                checkedVids.Clear();
                UpdateSelection();
            };
            Find<Button>("BtnDownloadSelected").Click += delegate { StartDownload(checkedVids.ToList()); };
            Find<ItemsControl>("VersionList").AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((s, e) =>
            {
                var b = e.OriginalSource as Button;
                if (b != null && b.Tag is string)
                    StartDownload(new[] { (string)b.Tag });
            }));
        }

        void SetTypeFilter(string type)
        {
            var chip = Find<Panel>("TypeChips").Children.OfType<RadioButton>().FirstOrDefault(r => (string)r.Tag == type);
            if (chip != null)
                chip.IsChecked = true;
        }

        void BuildVersionItems()
        {
            // 合并后的版本按发布时间倒序；同一时间保持清单顺序
            versionItems = catalog.All.Order
                .Select((vid, i) => new { Info = catalog.All[vid], Index = i })
                .OrderByDescending(x => x.Info.ReleaseTime, StringComparer.Ordinal)
                .ThenBy(x => x.Index)
                .Select(x => new VersionItem(x.Info, checkedVids.Contains(x.Info.Id), OnItemChecked))
                .ToList();
            versionView = new ListCollectionView(versionItems) { Filter = o => Filter((VersionItem)o) };
            vm.Versions = versionView;
        }

        bool Filter(VersionItem v)
        {
            if (typeFilter != "all" && v.Type != typeFilter)
                return false;
            if (sourceFilter == Mc.Mojang && !v.HasMojang)
                return false;
            if (sourceFilter == Mc.Bmclapi && !v.HasBmclapi)
                return false;
            return search.Length == 0 || v.IdLower.Contains(search);
        }

        void ApplyFilter()
        {
            if (versionView == null)
                return;
            versionView.Refresh();
            int visible = versionItems.Count(Filter);
            vm.VersionsEmpty = !vm.IsLoading && visible == 0;
            vm.VersionSummary = "共 " + versionItems.Count + " 个版本" + (visible != versionItems.Count ? " · 当前显示 " + visible + " 个" : "") + " · 点击整行即可勾选";
        }

        void OnItemChecked(VersionItem item)
        {
            if (item.IsChecked)
                checkedVids.Add(item.Id);
            else
                checkedVids.Remove(item.Id);
            if (!bulkUpdate)
                UpdateSelection();
        }

        void SetChecked(IEnumerable<VersionItem> items, bool value)
        {
            bulkUpdate = true;
            try
            {
                foreach (var item in items.ToList())
                    item.IsChecked = value;
            }
            finally
            {
                bulkUpdate = false;
            }
            UpdateSelection();
        }

        void UpdateSelection()
        {
            int n = catalog == null ? 0 : checkedVids.Count(catalog.All.Contains);
            vm.HasSelection = n > 0;
            vm.SelectedText = n > 0 ? "已选择 " + n + " 个版本" : "未选择版本";
            vm.DownloadSelectedText = n > 0 ? "下载所选（" + n + "）" : "下载所选";
        }

        // ---------- 下载 ----------

        void WireDownloads()
        {
            Find<Button>("BtnDownloadsOpenDir").Click += delegate { OpenOutDir(); };
            Find<Button>("BtnDownloadSettings").Click += delegate { Navigate("settings"); };
            Find<Button>("BtnGoVersions").Click += delegate { Navigate("versions"); };
        }

        void StartDownload(IList<string> vids)
        {
            if (catalog == null)
            {
                Toast("版本清单尚未加载完成，请稍候", "info");
                return;
            }
            if (running)
            {
                Toast("当前已有下载任务进行中，请等待完成后再开始新的下载", "warning");
                Navigate("downloads");
                return;
            }
            var outDir = (settings.OutDir ?? "").Trim();
            if (outDir.Length == 0)
            {
                Toast("请先在设置中选择下载目录", "error");
                Navigate("settings");
                return;
            }
            try
            {
                Directory.CreateDirectory(outDir);
            }
            catch (Exception e)
            {
                Toast("无法创建下载目录：" + e.Message, "error");
                return;
            }

            var list = vids.Where(catalog.All.Contains).Distinct()
                .OrderByDescending(v => catalog.All[v].ReleaseTime, StringComparer.Ordinal)
                .ToList();
            if (list.Count == 0)
            {
                Toast("请先勾选要下载的版本", "info");
                return;
            }

            tasks = new ObservableCollection<TaskItem>(list.Select(v => new TaskItem(v)));
            taskMap = tasks.ToDictionary(t => t.Vid);
            totalSize = 0;
            running = true;
            vm.Tasks = tasks;
            vm.HasTasks = true;
            vm.IsDownloading = true;
            Window.TaskbarItemInfo.ProgressState = TaskbarItemProgressState.Normal;
            UpdateTotals();

            var engine = new BatchDownloader(catalog.All, this, uiQueue.Enqueue);
            int workers = settings.Concurrency;
            string priority = settings.Priority;
            var thread = new Thread(() => engine.Run(list, outDir, workers, priority));
            thread.IsBackground = true;
            thread.Start();
            Navigate("downloads");
        }

        void UpdateTotals()
        {
            if (tasks == null || tasks.Count == 0)
            {
                vm.TotalPercent = 0;
                vm.TotalPercentText = "0%";
                vm.TotalDetail = "暂无任务";
                vm.TotalSpeedText = "";
                vm.ActiveBadgeVisible = false;
                return;
            }
            long done = tasks.Sum(t => t.Got);
            int finished = tasks.Count(t => t.State == "done" || t.State == "fail");
            double speed = tasks.Where(t => t.State == "run").Sum(t => t.Speed);
            double pct = totalSize > 0 ? Math.Min(100.0, done * 100.0 / totalSize) : (finished == tasks.Count ? 100.0 : 0.0);

            vm.TotalPercent = pct;
            vm.TotalPercentText = (int)Math.Floor(pct) + "%";
            vm.TotalDetail = (totalSize > 0 ? Mc.Human(done) + " / " + Mc.Human(totalSize) : Mc.Human(done)) + " · 已完成 " + finished + " / " + tasks.Count;
            vm.TotalSpeedText = running && speed > 0 ? Mc.Human(speed) + "/s" : "";
            vm.ActiveBadge = (tasks.Count - finished).ToString();
            vm.ActiveBadgeVisible = running && finished < tasks.Count;
            vm.DownloadSubtitle = running
                ? "正在下载 " + tasks.Count + " 个版本 · 完成后按 SHA1 与文件大小校验"
                : "下载完成后按 SHA1 与文件大小校验";
            Window.TaskbarItemInfo.ProgressValue = pct / 100.0;
        }

        TaskItem Task(string vid)
        {
            TaskItem t;
            return taskMap.TryGetValue(vid, out t) ? t : null;
        }

        void IBatchListener.TaskSource(string vid, string source)
        {
            var t = Task(vid);
            if (t != null)
                t.Source = Mc.SourceName(source);
        }

        void IBatchListener.Progress(string vid, long got, long total, double speed)
        {
            var t = Task(vid);
            if (t == null)
                return;
            t.Got = got;
            t.Total = total;
            t.Speed = speed;
            if (t.State == "wait")
                t.State = "run";
            if (total > 0)
            {
                t.Percent = Math.Min(got * 100.0 / total, 100.0);
                t.PercentText = (int)Math.Floor(t.Percent) + "%";
            }
            if (got == 0 && speed <= 0)
                t.Status = "正在连接…";
            else if (total > 0)
                t.Status = Mc.Human(got) + " / " + Mc.Human(total) + " · " + Mc.Human(speed) + "/s";
            else
                t.Status = Mc.Human(got) + " · " + Mc.Human(speed) + "/s";
            UpdateTotals();
        }

        void IBatchListener.ProgressMessage(string vid, string text)
        {
            var t = Task(vid);
            if (t != null)
                t.Status = text;
        }

        void IBatchListener.TaskDone(string vid, bool success, string text)
        {
            var t = Task(vid);
            if (t == null)
                return;
            t.Speed = 0;
            if (success)
            {
                t.State = "done";
                t.Percent = 100;
                t.PercentText = "100%";
                if (t.Total > 0)
                    t.Got = t.Total;
                t.Status = text == "已存在" ? "文件已存在且 SHA1 一致，已跳过" : "下载完成，校验通过";
            }
            else
            {
                t.State = "fail";
                t.PercentText = "";
                t.Status = "失败：" + text;
            }
            UpdateTotals();
        }

        void IBatchListener.TotalSize(long total)
        {
            totalSize = total;
            UpdateTotals();
        }

        void IBatchListener.AllDone()
        {
            running = false;
            vm.IsDownloading = false;
            UpdateTotals();
            Window.TaskbarItemInfo.ProgressState = TaskbarItemProgressState.None;
            RefreshFiles();
            int ok = tasks.Count(t => t.State == "done");
            int failed = tasks.Count - ok;
            if (failed == 0)
                Toast("全部下载完成，共 " + ok + " 个版本", "success");
            else
                Toast("下载结束：成功 " + ok + " 个，失败 " + failed + " 个", ok == 0 ? "error" : "warning");
        }

        // ---------- 下载目录 ----------

        void WireFiles()
        {
            Find<Button>("BtnRefreshFiles").Click += delegate { RefreshFiles(); };
            Find<Button>("BtnFilesOpenDir").Click += delegate { OpenOutDir(); };
            Find<Button>("BtnFilesChangeDir").Click += delegate { ChooseDir(); };
        }

        void RefreshFiles()
        {
            var path = (settings.OutDir ?? "").Trim();
            vm.OutDir = path;
            int version = ++filesVersion;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var items = path.Length > 0 && Directory.Exists(path) ? FileItem.Load(path, 0) : new List<FileItem>();
                uiQueue.Enqueue(() =>
                {
                    if (version != filesVersion)
                        return;
                    vm.Files = items;
                    vm.FilesEmpty = items.Count == 0;
                });
            });
        }

        void ChooseDir()
        {
            string dir;
            try
            {
                dir = FolderPicker.Pick(Handle, "选择下载目录", settings.OutDir);
            }
            catch (Exception e)
            {
                Toast("无法打开目录选择框：" + e.Message, "error");
                return;
            }
            if (string.IsNullOrEmpty(dir))
                return;
            settings.OutDir = dir;
            settings.Save();
            ApplySettingsToView();
            RefreshFiles();
            Toast("下载目录已更改为 " + dir, "success");
        }

        void OpenOutDir()
        {
            var dir = (settings.OutDir ?? "").Trim();
            if (!Directory.Exists(dir))
            {
                Toast("目录不存在：" + dir + "（开始下载时会自动创建）", "warning");
                return;
            }
            OpenShell(dir);
        }

        void OpenShell(string target)
        {
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception e)
            {
                Toast("无法打开：" + e.Message, "error");
            }
        }

        // ---------- 设置 ----------

        void WireSettings()
        {
            Find<Button>("BtnSettingsBrowse").Click += delegate { ChooseDir(); };
            Find<Button>("BtnSettingsOpenDir").Click += delegate { OpenOutDir(); };
            Find<Button>("BtnConcurrencyDown").Click += delegate { SetConcurrency(settings.Concurrency - 1); };
            Find<Button>("BtnConcurrencyUp").Click += delegate { SetConcurrency(settings.Concurrency + 1); };

            var prioMojang = Find<RadioButton>("PrioMojang");
            var prioBmclapi = Find<RadioButton>("PrioBmclapi");
            (settings.Priority == Mc.Bmclapi ? prioBmclapi : prioMojang).IsChecked = true;
            prioMojang.Checked += delegate { SetPriority(Mc.Mojang); };
            prioBmclapi.Checked += delegate { SetPriority(Mc.Bmclapi); };

            var themes = new[] { Find<RadioButton>("ThemeLight"), Find<RadioButton>("ThemeDark"), Find<RadioButton>("ThemeSystem") };
            foreach (var rb in themes)
            {
                if ((string)rb.Tag == settings.Theme)
                    rb.IsChecked = true;
            }
            foreach (var rb in themes)
            {
                var r = rb;
                r.Checked += delegate
                {
                    settings.Theme = (string)r.Tag;
                    settings.Save();
                    ThemeManager.Apply(settings.Theme, settings.Accent);
                };
            }

            vm.Accents = ThemeManager.Accents.Select(a =>
            {
                var brush = new SolidColorBrush(a.Base);
                brush.Freeze();
                return new AccentVm { Key = a.Key, Name = a.Name, Brush = brush, IsSelected = a.Key == settings.Accent };
            }).ToList();
            Find<ItemsControl>("AccentList").AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((s, e) =>
            {
                var rb = e.OriginalSource as RadioButton;
                if (rb == null || !(rb.Tag is string) || (string)rb.Tag == settings.Accent)
                    return;
                settings.Accent = (string)rb.Tag;
                settings.Save();
                ThemeManager.Apply(settings.Theme, settings.Accent);
            }));
        }

        void SetConcurrency(int n)
        {
            settings.Concurrency = Math.Max(1, Math.Min(8, n));
            settings.Save();
            ApplySettingsToView();
        }

        void SetPriority(string source)
        {
            settings.Priority = source;
            settings.Save();
            ApplySettingsToView();
        }

        void ApplySettingsToView()
        {
            vm.OutDir = settings.OutDir;
            vm.Concurrency = settings.Concurrency;
            vm.ConcurrencyText = "并发 " + settings.Concurrency;
            vm.PriorityText = "优先 " + Mc.SourceName(settings.Priority);
        }

        // ---------- 通知与对话框 ----------

        void Toast(string text, string kind)
        {
            string fg, bg, glyph;
            switch (kind)
            {
                case "success": fg = "Success"; bg = "SuccessSubtle"; glyph = ""; break;
                case "warning": fg = "Warning"; bg = "WarningSubtle"; glyph = ""; break;
                case "error": fg = "Danger"; bg = "DangerSubtle"; glyph = ""; break;
                default: fg = "AccentText"; bg = "AccentSubtle"; glyph = ""; break;
            }
            Find<Border>("ToastIconBox").SetResourceReference(Border.BackgroundProperty, bg);
            var icon = Find<TextBlock>("ToastIcon");
            icon.SetResourceReference(TextBlock.ForegroundProperty, fg);
            icon.Text = glyph;
            Find<TextBlock>("ToastText").Text = text;

            toastVersion++;
            var toast = Find<Border>("Toast");
            toast.Visibility = Visibility.Visible;
            toast.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            Find<TranslateTransform>("ToastShift").BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            toastTimer.Stop();
            toastTimer.Interval = TimeSpan.FromSeconds(kind == "error" || kind == "warning" ? 6 : 4);
            toastTimer.Start();
        }

        void HideToast()
        {
            int version = toastVersion;
            var toast = Find<Border>("Toast");
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
            fade.Completed += delegate
            {
                if (version == toastVersion)
                    toast.Visibility = Visibility.Collapsed;
            };
            toast.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        void WireDialog()
        {
            Find<Button>("DialogCancel").Click += delegate { HideDialog(); };
            Find<Button>("DialogOk").Click += delegate
            {
                HideDialog();
                if (dialogOk != null)
                    dialogOk();
            };
            Window.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && Find<Grid>("DialogOverlay").Visibility == Visibility.Visible)
                {
                    HideDialog();
                    e.Handled = true;
                }
            };
        }

        void ShowDialog(string title, string text, string okText, Action onOk)
        {
            Find<TextBlock>("DialogTitle").Text = title;
            Find<TextBlock>("DialogText").Text = text;
            Find<Button>("DialogOk").Content = okText;
            dialogOk = onOk;
            var overlay = Find<Grid>("DialogOverlay");
            overlay.Visibility = Visibility.Visible;
            overlay.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
            Find<Button>("DialogCancel").Focus();
        }

        void HideDialog()
        {
            Find<Grid>("DialogOverlay").Visibility = Visibility.Collapsed;
            dialogOk = null;
        }

        void OnClosing(object sender, CancelEventArgs e)
        {
            if (!running || confirmedExit)
                return;
            e.Cancel = true;
            int left = tasks.Count(t => t.State != "done" && t.State != "fail");
            ShowDialog("下载仍在进行", "还有 " + left + " 个版本没有下载完成，现在退出会中断这些下载。确定要退出吗？", "退出", () =>
            {
                confirmedExit = true;
                Window.Close();
            });
        }
    }
}
