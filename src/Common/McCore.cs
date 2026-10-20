// Minecraft 原版 client.jar 下载器 —— 公共核心（三个前端共用）
// - 版本清单拉取与合并、版本 JSON 解析、BMCLAPI 域名替换
// - 带 SHA1 / 大小校验的下载（.part 临时文件 + 原子替换）
// 仅使用 .NET Framework 4.x（Windows 10 自带）。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace McDownloader
{
    public static class Mc
    {
        public const string MojangManifest = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
        public const string BmclapiManifest = "https://bmclapi2.bangbang93.com/mc/game/version_manifest.json";
        public const string BmclapiHost = "bmclapi2.bangbang93.com";

        public static readonly string[] MirrorHosts =
        {
            "launchermeta.mojang.com",
            "launcher.mojang.com",
            "piston-meta.mojang.com",
            "piston-data.mojang.com",
        };

        public const string Mojang = "mojang";
        public const string Bmclapi = "bmclapi";

        // 由各前端在启动时设置（对应 Python 版的 UA 常量）
        public static string UserAgent = "mc-jar-downloader/1.0";

        // 已知愚人节版本（仅用于列表标记与统计，可自行补充）
        public static readonly HashSet<string> AprilFools = new HashSet<string>
        {
            "15w14a",
            "1.RV-Pre1",
            "3D Shareware v1.34",
            "20w14infinite",
            "22w13oneBlockAtATime",
            "23w13a_or_b",
            "24w14potato",
            "25w14craftmine",
        };

        public static string SourceName(string source)
        {
            return source == Mojang ? "Mojang" : source == Bmclapi ? "BMCLAPI" : source;
        }

        public static string OtherSource(string source)
        {
            return source == Mojang ? Bmclapi : Mojang;
        }

        // ---------- 网络初始化 ----------

        static bool networkReady;

        /// <summary>
        /// 编译时未带 TargetFramework 特性的程序会以 4.0 兼容模式运行，默认只启用 TLS 1.0，
        /// 这里补上 TLS 1.2；同时放开每主机 2 个连接的默认上限，否则并发下载会排队。
        /// </summary>
        public static void InitNetwork()
        {
            if (networkReady)
                return;
            networkReady = true;
            if (ServicePointManager.SecurityProtocol != 0) // 0 = SystemDefault（交给系统决定）
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11;
            ServicePointManager.DefaultConnectionLimit = 32;
            ServicePointManager.Expect100Continue = false;
        }

        // ---------- 基础工具 ----------

        const string SafeChars = ":/?&=%#[]@!$'()*+,;~";

        /// <summary>等价于 urllib.parse.quote(url, safe=":/?&amp;=%#[]@!$'()*+,;~")。</summary>
        public static string SafeUrl(string url)
        {
            var sb = new StringBuilder(url.Length + 16);
            foreach (byte b in Encoding.UTF8.GetBytes(url))
            {
                char c = (char)b;
                bool keep = b < 0x80 && ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                                         || c == '_' || c == '.' || c == '-' || SafeChars.IndexOf(c) >= 0);
                if (keep)
                    sb.Append(c);
                else
                    sb.Append('%').Append(b.ToString("X2"));
            }
            return sb.ToString();
        }

        public static HttpWebRequest CreateRequest(string url, int timeoutSeconds)
        {
            InitNetwork();
            var req = (HttpWebRequest)WebRequest.Create(new Uri(SafeUrl(url)));
            req.UserAgent = UserAgent;
            req.Timeout = timeoutSeconds * 1000;
            req.ReadWriteTimeout = timeoutSeconds * 1000;
            return req;
        }

        public static byte[] HttpGet(string url, int timeoutSeconds, CancellationToken token)
        {
            var req = CreateRequest(url, timeoutSeconds);
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            using (token.Register(req.Abort))
            {
                try
                {
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var stream = resp.GetResponseStream())
                    using (var ms = new MemoryStream())
                    {
                        stream.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
                catch (Exception)
                {
                    token.ThrowIfCancellationRequested();
                    throw;
                }
            }
        }

        public static byte[] HttpGet(string url)
        {
            return HttpGet(url, 20, CancellationToken.None);
        }

        public static string Human(double n)
        {
            if (double.IsNaN(n))
                return "未知";
            if (n < 0)
                n = 0;
            string[] units = { "B", "KB", "MB", "GB" };
            foreach (var unit in units)
            {
                if (n < 1024 || unit == "GB")
                    return n.ToString("0.0") + unit;
                n /= 1024;
            }
            return n.ToString("0.0") + "GB";
        }

        public static string ResolveUrl(string url, string source)
        {
            if (source != Bmclapi || string.IsNullOrEmpty(url))
                return url;
            foreach (var host in MirrorHosts)
                url = url.Replace(host, BmclapiHost);
            return url;
        }

        public static string Left(string s, int n)
        {
            if (s == null)
                return "";
            return s.Length <= n ? s : s.Substring(0, n);
        }

        public static string DefaultDownloadDir()
        {
            // 优先使用系统下载文件夹
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders"))
                {
                    if (key != null)
                    {
                        var val = key.GetValue("{374DE290-123F-4565-9164-39C4925E467B}",
                            null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                        if (!string.IsNullOrEmpty(val))
                        {
                            val = Environment.ExpandEnvironmentVariables(val);
                            if (val.Length > 0)
                                return val;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }

            var xdg = Environment.GetEnvironmentVariable("XDG_DOWNLOAD_DIR");
            if (!string.IsNullOrEmpty(xdg))
                return Environment.ExpandEnvironmentVariables(xdg);

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var name in new[] { "Downloads", "下载" })
            {
                var p = Path.Combine(home, name);
                if (Directory.Exists(p))
                    return p;
            }
            return Path.Combine(home, "Downloads");
        }

        // ---------- JSON ----------

        public static Dictionary<string, object> ParseJson(byte[] data)
        {
            var text = Encoding.UTF8.GetString(data);
            if (text.Length > 0 && text[0] == '﻿')
                text = text.Substring(1);
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };
            var obj = js.DeserializeObject(text) as Dictionary<string, object>;
            if (obj == null)
                throw new FormatException("JSON 顶层不是对象");
            return obj;
        }

        public static object Get(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v : null;
        }

        public static string GetString(Dictionary<string, object> d, string key, string fallback)
        {
            var v = Get(d, key) as string;
            return v ?? fallback;
        }

        public static long GetLong(Dictionary<string, object> d, string key)
        {
            var v = Get(d, key);
            if (v == null)
                return 0;
            try
            {
                return Convert.ToInt64(v);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        public static IEnumerable<Dictionary<string, object>> GetObjects(Dictionary<string, object> d, string key)
        {
            var arr = Get(d, key) as object[];
            if (arr == null)
                return Enumerable.Empty<Dictionary<string, object>>();
            return arr.OfType<Dictionary<string, object>>();
        }

        /// <summary>按类型分组用：愚人节版本单独归为 "april"。</summary>
        public static string DisplayType(string vid, string type)
        {
            return AprilFools.Contains(vid) ? "april" : type;
        }

        // ---------- 文件 ----------

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);

        const int MOVEFILE_REPLACE_EXISTING = 0x1;
        const int MOVEFILE_COPY_ALLOWED = 0x2;

        /// <summary>等价于 os.replace：覆盖式重命名。</summary>
        public static void ReplaceFile(string src, string dst)
        {
            if (!MoveFileEx(src, dst, MOVEFILE_REPLACE_EXISTING | MOVEFILE_COPY_ALLOWED))
                throw new IOException("无法替换文件 " + dst + "（错误码 " + Marshal.GetLastWin32Error() + "）");
        }

        public static string Sha1OfFile(string path)
        {
            using (var sha = SHA1.Create())
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                return Hex(sha.ComputeHash(f));
        }

        public static string Hex(byte[] hash)
        {
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>
        /// 下载到 path + ".part"，校验通过后替换为正式文件；任何异常（含取消）都会删除临时文件。
        /// progress 在每个数据块后回调 (已下载, 总大小)，由调用方自行节流。
        /// </summary>
        public static void DownloadFile(string url, string path, long expectedSize, string expectedSha1,
                                        Action<long, long> progress, CancellationToken token)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            var tmp = path + ".part";
            var req = CreateRequest(url, 30);
            long got = 0;
            long total = expectedSize;
            try
            {
                using (token.Register(req.Abort))
                using (var sha = SHA1.Create())
                {
                    try
                    {
                        using (var resp = (HttpWebResponse)req.GetResponse())
                        using (var stream = resp.GetResponseStream())
                        using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            if (total <= 0)
                                total = Math.Max(0, resp.ContentLength);
                            var buf = new byte[64 * 1024];
                            int n;
                            while ((n = stream.Read(buf, 0, buf.Length)) > 0)
                            {
                                token.ThrowIfCancellationRequested();
                                f.Write(buf, 0, n);
                                sha.TransformBlock(buf, 0, n, null, 0);
                                got += n;
                                if (progress != null)
                                    progress(got, total);
                            }
                        }
                    }
                    catch (Exception)
                    {
                        token.ThrowIfCancellationRequested();
                        throw;
                    }
                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    var actual = Hex(sha.Hash);
                    if (!string.IsNullOrEmpty(expectedSha1) && !string.Equals(actual, expectedSha1, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("SHA1 校验失败：期望 " + expectedSha1 + "，实际 " + actual);
                    if (expectedSize > 0 && got != expectedSize)
                        throw new InvalidDataException("文件大小不符：期望 " + expectedSize + "，实际 " + got);
                }
                ReplaceFile(tmp, path);
            }
            catch (Exception)
            {
                try
                {
                    if (File.Exists(tmp))
                        File.Delete(tmp);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                throw;
            }
        }

        /// <summary>用固定数量的专用线程并行处理（下载属阻塞 I/O，不适合线程池慢速扩容）。</summary>
        public static void RunParallel<T>(IList<T> items, int maxWorkers, Action<T> action)
        {
            int next = -1;
            int workers = Math.Max(1, Math.Min(maxWorkers, items.Count));
            var threads = new List<Thread>();
            for (int i = 0; i < workers; i++)
            {
                var t = new Thread(() =>
                {
                    int idx;
                    while ((idx = Interlocked.Increment(ref next)) < items.Count)
                        action(items[idx]);
                });
                t.IsBackground = true;
                t.Start();
                threads.Add(t);
            }
            foreach (var t in threads)
                t.Join();
        }
    }

    // ---------- 清单拉取与合并 ----------

    public sealed class ManifestResult
    {
        public string Source;
        public Dictionary<string, object> Data;
        public double Elapsed;
        public Exception Error;

        public static ManifestResult Fetch(string source, string url)
        {
            var sw = Stopwatch.StartNew();
            var r = new ManifestResult { Source = source };
            try
            {
                r.Data = Mc.ParseJson(Mc.HttpGet(url));
            }
            catch (Exception e)
            {
                r.Error = e;
            }
            r.Elapsed = sw.Elapsed.TotalSeconds;
            return r;
        }

        /// <summary>并发拉取 Mojang 与 BMCLAPI 两份清单（结果顺序固定为 Mojang、BMCLAPI）。</summary>
        public static List<ManifestResult> FetchBoth()
        {
            var a = Task.Factory.StartNew(() => Fetch(Mc.Mojang, Mc.MojangManifest), TaskCreationOptions.LongRunning);
            var b = Task.Factory.StartNew(() => Fetch(Mc.Bmclapi, Mc.BmclapiManifest), TaskCreationOptions.LongRunning);
            return new List<ManifestResult> { a.Result, b.Result };
        }

        public IEnumerable<Dictionary<string, object>> Versions
        {
            get { return Mc.GetObjects(Data, "versions"); }
        }
    }

    public sealed class VersionInfo
    {
        public string Id;
        public string Type;
        public string ReleaseTime;
        // 源 -> 版本 JSON 地址；按加入顺序保存
        public readonly Dictionary<string, string> Sources = new Dictionary<string, string>();

        public bool Has(string source)
        {
            return Sources.ContainsKey(source);
        }

        public string Date
        {
            get { return Mc.Left(ReleaseTime, 10); }
        }
    }

    /// <summary>合并后的版本表，保持清单中的出现顺序。</summary>
    public sealed class VersionIndex
    {
        public readonly Dictionary<string, VersionInfo> Map = new Dictionary<string, VersionInfo>();
        public readonly List<string> Order = new List<string>();

        public int Count
        {
            get { return Order.Count; }
        }

        public bool Contains(string vid)
        {
            return Map.ContainsKey(vid);
        }

        public VersionInfo this[string vid]
        {
            get { return Map[vid]; }
        }

        public void Add(string source, Dictionary<string, object> item)
        {
            var vid = Mc.GetString(item, "id", null);
            if (string.IsNullOrEmpty(vid))
                return;
            VersionInfo v;
            if (!Map.TryGetValue(vid, out v))
            {
                v = new VersionInfo
                {
                    Id = vid,
                    Type = Mc.GetString(item, "type", "unknown"),
                    ReleaseTime = Mc.GetString(item, "releaseTime", ""),
                };
                Map[vid] = v;
                Order.Add(vid);
            }
            v.Sources[source] = Mc.GetString(item, "url", null);
        }

        public static VersionIndex Merge(IEnumerable<ManifestResult> results)
        {
            var index = new VersionIndex();
            foreach (var r in results)
            {
                if (r.Data == null)
                    continue;
                foreach (var item in r.Versions)
                    index.Add(r.Source, item);
            }
            return index;
        }

        /// <summary>从指定源获取版本 JSON（BMCLAPI 会替换为镜像域名）。</summary>
        public Dictionary<string, object> GetVersionJson(string vid, string source, CancellationToken token)
        {
            string url;
            if (!Map[vid].Sources.TryGetValue(source, out url) || string.IsNullOrEmpty(url))
                throw new InvalidOperationException(Mc.SourceName(source) + " 源没有该版本");
            return Mc.ParseJson(Mc.HttpGet(Mc.ResolveUrl(url, source), 20, token));
        }
    }

    /// <summary>版本 JSON 中 downloads.client 的信息。</summary>
    public sealed class ClientDownload
    {
        public string Url;
        public long Size;
        public string Sha1;

        public static ClientDownload From(Dictionary<string, object> versionJson)
        {
            var downloads = Mc.Get(versionJson, "downloads") as Dictionary<string, object>;
            var client = Mc.Get(downloads, "client") as Dictionary<string, object>;
            var url = Mc.GetString(client, "url", null);
            if (string.IsNullOrEmpty(url))
                return null;
            return new ClientDownload
            {
                Url = url,
                Size = Mc.GetLong(client, "size"),
                Sha1 = Mc.GetString(client, "sha1", null),
            };
        }
    }
}
