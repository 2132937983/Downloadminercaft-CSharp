// GUI 使用的逻辑：合并后的版本目录、批量下载引擎。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace McDownloader
{
    /// <summary>一次清单加载的结果：两个源的原始数据 + 合并后的版本表。</summary>
    public sealed class VersionCatalog
    {
        public static readonly string[] TypeOrder = { "release", "snapshot", "april", "old_alpha", "old_beta" };

        public readonly List<ManifestResult> Results;
        public readonly VersionIndex All;

        public VersionCatalog(List<ManifestResult> results)
        {
            Results = results;
            All = VersionIndex.Merge(results);
        }

        public ManifestResult Result(string source)
        {
            return Results.FirstOrDefault(r => r.Source == source);
        }

        /// <summary>清单中 latest.release / latest.snapshot 指向的版本（优先取 Mojang 清单）。</summary>
        public string Latest(string kind)
        {
            foreach (var r in Results.OrderBy(r => r.Source == Mc.Mojang ? 0 : 1))
            {
                var latest = Mc.Get(r.Data, "latest") as Dictionary<string, object>;
                var vid = Mc.GetString(latest, kind, null);
                if (!string.IsNullOrEmpty(vid) && All.Contains(vid))
                    return vid;
            }
            return null;
        }

        /// <summary>按显示类型（愚人节单独计）统计合并后的版本数量。</summary>
        public Dictionary<string, int> TypeCounts()
        {
            var counts = TypeOrder.ToDictionary(t => t, t => 0);
            foreach (var vid in All.Order)
            {
                var t = Mc.DisplayType(vid, All[vid].Type);
                if (counts.ContainsKey(t))
                    counts[t]++;
            }
            return counts;
        }
    }
    // ---------- 批量下载 ----------

    /// <summary>下载事件回调。由引擎通过 Post 投递，GUI 在界面线程中执行。</summary>
    public interface IBatchListener
    {
        void TaskSource(string vid, string source);
        void Progress(string vid, long got, long total, double speed);
        void ProgressMessage(string vid, string text);
        void TaskDone(string vid, bool success, string text);
        void TotalSize(long total);
        void AllDone();
    }

    public sealed class BatchDownloader
    {
        sealed class Candidate
        {
            public string Source;
            public string Url;
            public long Size;
            public string Sha1;
        }

        sealed class Job
        {
            public int Index;
            public string Vid;
            public string Path;
            public List<Candidate> Candidates;
        }

        readonly VersionIndex versions;
        readonly IBatchListener listener;
        readonly Action<Action> post;

        public BatchDownloader(VersionIndex versions, IBatchListener listener, Action<Action> post)
        {
            this.versions = versions;
            this.listener = listener;
            this.post = post;
        }

        /// <summary>在调用线程上阻塞执行：先并发解析版本 JSON，再并发下载。</summary>
        public void Run(IList<string> vids, string outDir, int maxWorkers, string prioritySource)
        {
            try
            {
                string fallbackSource = Mc.OtherSource(prioritySource);
                maxWorkers = Math.Max(1, Math.Min(maxWorkers, vids.Count));

                var parsed = new List<Job>();
                var indexed = vids.Select((vid, i) => new KeyValuePair<int, string>(i, vid)).ToList();
                Mc.RunParallel(indexed, maxWorkers, kv =>
                {
                    string error;
                    var job = Prepare(kv.Value, kv.Key, outDir, prioritySource, fallbackSource, out error);
                    if (job == null)
                    {
                        var vid = kv.Value;
                        post(() => listener.TaskDone(vid, false, error));
                        return;
                    }
                    lock (parsed)
                        parsed.Add(job);
                    var src = job.Candidates[0].Source;
                    post(() => listener.TaskSource(job.Vid, src));
                });
                parsed.Sort((a, b) => a.Index.CompareTo(b.Index));

                long totalSize = parsed.Sum(p => p.Candidates[0].Size);
                post(() => listener.TotalSize(totalSize));

                if (parsed.Count > 0)
                    Mc.RunParallel(parsed, maxWorkers, DownloadOneTask);
            }
            finally
            {
                post(listener.AllDone);
            }
        }

        Job Prepare(string vid, int index, string outDir, string prioritySource, string fallbackSource, out string error)
        {
            var info = versions[vid];
            var sourcesToTry = new List<string>();
            if (info.Has(prioritySource))
                sourcesToTry.Add(prioritySource);
            if (info.Has(fallbackSource))
                sourcesToTry.Add(fallbackSource);

            var candidates = new List<Candidate>();
            string lastErr = null;
            foreach (var src in sourcesToTry)
            {
                try
                {
                    var dl = ClientDownload.From(versions.GetVersionJson(vid, src, CancellationToken.None));
                    if (dl == null)
                    {
                        lastErr = Mc.SourceName(src) + " 无 client.jar";
                        continue;
                    }
                    candidates.Add(new Candidate
                    {
                        Source = src,
                        Url = Mc.ResolveUrl(dl.Url, src),
                        Size = dl.Size,
                        Sha1 = dl.Sha1,
                    });
                }
                catch (Exception e)
                {
                    lastErr = Mc.SourceName(src) + ": " + e.Message;
                }
            }

            if (candidates.Count == 0)
            {
                error = lastErr ?? "无可用源";
                return null;
            }
            error = null;
            return new Job
            {
                Index = index,
                Vid = vid,
                Path = Path.Combine(outDir, vid + ".jar"),
                Candidates = candidates,
            };
        }

        void DownloadOneTask(Job job)
        {
            var vid = job.Vid;
            var first = job.Candidates[0];
            if (File.Exists(job.Path) && !string.IsNullOrEmpty(first.Sha1))
            {
                try
                {
                    if (string.Equals(Mc.Sha1OfFile(job.Path), first.Sha1, StringComparison.OrdinalIgnoreCase))
                    {
                        long fileSize = new FileInfo(job.Path).Length;
                        post(() => listener.Progress(vid, fileSize, fileSize, 0.0));
                        post(() => listener.TaskDone(vid, true, "已存在"));
                        return;
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            Exception lastErr = null;
            for (int i = 0; i < job.Candidates.Count; i++)
            {
                var cand = job.Candidates[i];
                try
                {
                    if (i > 0)
                    {
                        post(() => listener.TaskSource(vid, cand.Source));
                        post(() => listener.ProgressMessage(vid, "回退到 " + Mc.SourceName(cand.Source) + "..."));
                    }
                    else
                    {
                        post(() => listener.Progress(vid, 0, cand.Size, 0.0));
                    }
                    DownloadUrl(cand.Url, job.Path, cand.Size, cand.Sha1, vid);
                    post(() => listener.TaskDone(vid, true, "完成"));
                    return;
                }
                catch (Exception e)
                {
                    lastErr = e;
                }
            }
            var msg = lastErr == null ? "未知错误" : lastErr.Message;
            post(() => listener.TaskDone(vid, false, msg));
        }

        void DownloadUrl(string url, string path, long size, string sha1, string vid)
        {
            var sw = Stopwatch.StartNew();
            double lastReport = 0;
            long got = 0, total = size;
            // 最近约 1 秒内的 (时间, 字节数) 采样，用于计算实时速度
            var samples = new Queue<KeyValuePair<double, long>>();
            samples.Enqueue(new KeyValuePair<double, long>(0, 0));
            KeyValuePair<double, long> newest = samples.Peek();

            Mc.DownloadFile(url, path, size, sha1, (g, t) =>
            {
                got = g;
                total = t;
                double now = sw.Elapsed.TotalSeconds;
                newest = new KeyValuePair<double, long>(now, g);
                samples.Enqueue(newest);
                while (samples.Count > 32 || (samples.Count > 2 && now - samples.Peek().Key > 1.0))
                    samples.Dequeue();
                if (now - lastReport >= 0.15)
                {
                    lastReport = now;
                    double speed = 0;
                    var oldest = samples.Peek();
                    double dt = newest.Key - oldest.Key;
                    long db = newest.Value - oldest.Value;
                    if (samples.Count >= 2 && dt > 0.05 && db >= 0)
                        speed = db / dt;
                    if (speed <= 0)
                        speed = g / Math.Max(now, 0.001);
                    post(() => listener.Progress(vid, g, t, speed));
                }
            }, CancellationToken.None);

            long finalSize = got;
            long finalTotal = total > 0 ? total : finalSize;
            post(() => listener.Progress(vid, finalSize, finalTotal, 0.0));
        }
    }
}
