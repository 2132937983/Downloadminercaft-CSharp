// 界面绑定用的数据对象。

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace McDownloader
{
    public abstract class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return;
            field = value;
            Raise(name);
        }

        protected void Raise(string name)
        {
            var h = PropertyChanged;
            if (h != null)
                h(this, new PropertyChangedEventArgs(name));
        }
    }

    public static class Labels
    {
        public static string Type(string type)
        {
            switch (type)
            {
                case "release": return "正式版";
                case "snapshot": return "预览版";
                case "april": return "愚人节";
                case "old_alpha": return "早期 Alpha";
                case "old_beta": return "早期 Beta";
                default: return type;
            }
        }

        public static string TypeGlyph(string type)
        {
            switch (type)
            {
                case "release": return "";
                case "snapshot": return "";
                case "april": return "";
                case "old_alpha": return "";
                default: return "";
            }
        }
    }

    /// <summary>首页的版本源状态卡片。</summary>
    public sealed class SourceVm : Observable
    {
        public string Name { get; set; }
        public string Host { get; set; }
        public string Glyph { get; set; }

        string kind = "loading", stateText = "连接中", countText = "—", detail = "正在获取版本清单…";

        public string Kind { get { return kind; } set { Set(ref kind, value); } } // loading / ok / fail
        public string StateText { get { return stateText; } set { Set(ref stateText, value); } }
        public string CountText { get { return countText; } set { Set(ref countText, value); } }
        public string Detail { get { return detail; } set { Set(ref detail, value); } }

        public void SetLoading()
        {
            Kind = "loading";
            StateText = "连接中";
            Detail = "正在获取版本清单…";
        }

        public void SetResult(ManifestResult r)
        {
            if (r == null || r.Data == null)
            {
                Kind = "fail";
                StateText = "不可用";
                CountText = "—";
                Detail = r == null || r.Error == null ? "加载失败" : r.Error.Message;
                return;
            }
            Kind = "ok";
            StateText = "在线";
            CountText = r.Versions.Count().ToString();
            Detail = "响应耗时 " + r.Elapsed.ToString("0.00") + " 秒";
        }
    }

    /// <summary>首页的版本类型统计磁贴。</summary>
    public sealed class StatVm
    {
        public string Type { get; set; }
        public string Label { get; set; }
        public string Glyph { get; set; }
        public int Count { get; set; }
    }

    public sealed class AccentVm
    {
        public string Key { get; set; }
        public string Name { get; set; }
        public Brush Brush { get; set; }
        public bool IsSelected { get; set; }
    }

    /// <summary>版本列表中的一行。</summary>
    public sealed class VersionItem : Observable
    {
        readonly Action<VersionItem> onCheckedChanged;
        bool isChecked;

        public VersionItem(VersionInfo info, bool isChecked, Action<VersionItem> onCheckedChanged)
        {
            Info = info;
            Id = info.Id;
            IdLower = info.Id.ToLowerInvariant();
            Type = Mc.DisplayType(info.Id, info.Type);
            TypeLabel = Labels.Type(Type);
            Date = info.Date;
            HasMojang = info.Has(Mc.Mojang);
            HasBmclapi = info.Has(Mc.Bmclapi);
            this.isChecked = isChecked;
            this.onCheckedChanged = onCheckedChanged;
        }

        public VersionInfo Info { get; private set; }
        public string Id { get; private set; }
        public string IdLower { get; private set; }
        public string Type { get; private set; }
        public string TypeLabel { get; private set; }
        public string Date { get; private set; }
        public bool HasMojang { get; private set; }
        public bool HasBmclapi { get; private set; }

        public bool IsChecked
        {
            get { return isChecked; }
            set
            {
                if (isChecked == value)
                    return;
                isChecked = value;
                Raise("IsChecked");
                onCheckedChanged(this);
            }
        }
    }

    /// <summary>下载任务列表中的一行。</summary>
    public sealed class TaskItem : Observable
    {
        public TaskItem(string vid)
        {
            Vid = vid;
        }

        public string Vid { get; private set; }
        public long Got, Total;
        public double Speed;

        string state = "wait", source = "", status = "等待解析", percentText = "";
        double percent;

        public string State { get { return state; } set { Set(ref state, value); } } // wait / run / done / fail
        public string Source { get { return source; } set { Set(ref source, value); Raise("HasSource"); } }
        public bool HasSource { get { return source.Length > 0; } }
        public string Status { get { return status; } set { Set(ref status, value); } }
        public double Percent { get { return percent; } set { Set(ref percent, value); } }
        public string PercentText { get { return percentText; } set { Set(ref percentText, value); } }
    }

    /// <summary>下载目录文件树中的节点。</summary>
    public sealed class FileItem
    {
        public string Name { get; set; }
        public bool IsDir { get; set; }
        public string Size { get; set; }
        public string Glyph { get; set; }
        public List<FileItem> Children { get; set; }

        /// <summary>读取 path 下的文件与目录：目录在前、名称不区分大小写排序，最多 4 层。</summary>
        public static List<FileItem> Load(string path, int depth)
        {
            var list = new List<FileItem>();
            if (depth > 3)
                return list;
            List<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(path).GetFileSystemInfos()
                    .OrderBy(e => e is FileInfo)
                    .ThenBy(e => e.Name.ToLowerInvariant(), StringComparer.Ordinal)
                    .ToList();
            }
            catch (Exception)
            {
                return list;
            }
            foreach (var e in entries)
            {
                var dir = e as DirectoryInfo;
                var item = new FileItem { Name = e.Name, IsDir = dir != null, Size = "" };
                if (dir != null)
                {
                    item.Glyph = "";
                    item.Children = Load(dir.FullName, depth + 1);
                }
                else
                {
                    var ext = e.Extension.ToLowerInvariant();
                    item.Glyph = ext == ".jar" ? "" : ext == ".part" ? "" : "";
                    try
                    {
                        item.Size = Mc.Human(((FileInfo)e).Length);
                    }
                    catch (Exception)
                    {
                    }
                }
                list.Add(item);
            }
            return list;
        }
    }

    /// <summary>主窗口的数据上下文。</summary>
    public sealed class MainViewModel : Observable
    {
        public MainViewModel()
        {
            Mojang = new SourceVm { Name = "Mojang 官方源", Host = "piston-meta.mojang.com", Glyph = "" };
            Bmclapi = new SourceVm { Name = "BMCLAPI 国内镜像", Host = "bmclapi2.bangbang93.com", Glyph = "" };
        }

        public SourceVm Mojang { get; private set; }
        public SourceVm Bmclapi { get; private set; }

        bool isLoading = true, isDownloading, hasSelection, hasTasks, filesEmpty, versionsEmpty;
        bool canDownloadRelease, canDownloadSnapshot, activeBadgeVisible;
        string latestRelease = "—", latestReleaseDetail = "正在获取版本清单…", latestSnapshot = "—", latestSnapshotDetail = "";
        string versionSummary = "正在获取版本清单…", selectedText = "未选择版本", downloadSelectedText = "下载所选";
        string totalPercentText = "0%", totalDetail = "暂无任务", totalSpeedText = "", downloadSubtitle = "下载完成后按 SHA1 与文件大小校验";
        string activeBadge = "", outDir = "", concurrencyText = "", priorityText = "";
        int concurrency;
        double totalPercent;
        object versions, tasks;
        List<StatVm> stats = new List<StatVm>();
        List<FileItem> files = new List<FileItem>();
        List<AccentVm> accents = new List<AccentVm>();

        public bool IsLoading { get { return isLoading; } set { Set(ref isLoading, value); Raise("CanReload"); } }
        public bool CanReload { get { return !isLoading; } }
        public bool IsDownloading { get { return isDownloading; } set { Set(ref isDownloading, value); } }
        public List<StatVm> Stats { get { return stats; } set { Set(ref stats, value); } }

        public string LatestRelease { get { return latestRelease; } set { Set(ref latestRelease, value); } }
        public string LatestReleaseDetail { get { return latestReleaseDetail; } set { Set(ref latestReleaseDetail, value); } }
        public string LatestSnapshot { get { return latestSnapshot; } set { Set(ref latestSnapshot, value); } }
        public string LatestSnapshotDetail { get { return latestSnapshotDetail; } set { Set(ref latestSnapshotDetail, value); } }
        public bool CanDownloadRelease { get { return canDownloadRelease; } set { Set(ref canDownloadRelease, value); } }
        public bool CanDownloadSnapshot { get { return canDownloadSnapshot; } set { Set(ref canDownloadSnapshot, value); } }

        public object Versions { get { return versions; } set { Set(ref versions, value); } }
        public bool VersionsEmpty { get { return versionsEmpty; } set { Set(ref versionsEmpty, value); } }
        public string VersionSummary { get { return versionSummary; } set { Set(ref versionSummary, value); } }
        public string SelectedText { get { return selectedText; } set { Set(ref selectedText, value); } }
        public string DownloadSelectedText { get { return downloadSelectedText; } set { Set(ref downloadSelectedText, value); } }
        public bool HasSelection { get { return hasSelection; } set { Set(ref hasSelection, value); } }

        public object Tasks { get { return tasks; } set { Set(ref tasks, value); } }
        public bool HasTasks { get { return hasTasks; } set { Set(ref hasTasks, value); Raise("NoTasks"); } }
        public bool NoTasks { get { return !hasTasks; } }
        public double TotalPercent { get { return totalPercent; } set { Set(ref totalPercent, value); } }
        public string TotalPercentText { get { return totalPercentText; } set { Set(ref totalPercentText, value); } }
        public string TotalDetail { get { return totalDetail; } set { Set(ref totalDetail, value); } }
        public string TotalSpeedText { get { return totalSpeedText; } set { Set(ref totalSpeedText, value); } }
        public string DownloadSubtitle { get { return downloadSubtitle; } set { Set(ref downloadSubtitle, value); } }
        public string ActiveBadge { get { return activeBadge; } set { Set(ref activeBadge, value); } }
        public bool ActiveBadgeVisible { get { return activeBadgeVisible; } set { Set(ref activeBadgeVisible, value); } }

        public List<FileItem> Files { get { return files; } set { Set(ref files, value); } }
        public bool FilesEmpty { get { return filesEmpty; } set { Set(ref filesEmpty, value); } }

        public string OutDir { get { return outDir; } set { Set(ref outDir, value); } }
        public int Concurrency { get { return concurrency; } set { Set(ref concurrency, value); } }
        public string ConcurrencyText { get { return concurrencyText; } set { Set(ref concurrencyText, value); } }
        public string PriorityText { get { return priorityText; } set { Set(ref priorityText, value); } }
        public List<AccentVm> Accents { get { return accents; } set { Set(ref accents, value); } }
    }
}
