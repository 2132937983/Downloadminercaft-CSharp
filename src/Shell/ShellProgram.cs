// Minecraft 原版 client.jar 下载器（命令行 shell 版）
// - 启动时并发拉取 Mojang 与 BMCLAPI 的版本清单
// - 支持 release / snapshot / old_beta / old_alpha（含愚人节版本）
// - list 结果带 0001 起始序号，get/info 可直接引用序号
// - 多版本下载时显示每个文件的进度条与一个总进度
// 仅使用 .NET Framework 4.x（Windows 10 自带）。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace McDownloader
{
    sealed class UserError : Exception
    {
        public UserError(string message) : base(message)
        {
        }
    }

    sealed class DownloadTask
    {
        public string Target;
        public string Actual;
        public string Url;
        public string OfficialUrl;
        public long Size;
        public string Sha1;
        public string Path;
    }

    static class ShellProgram
    {
        static VersionIndex versions;
        static Dictionary<string, string> lastList = new Dictionary<string, string>();
        static string outDir;

        // 当前正在执行的命令；Ctrl+C 时取消它，空闲时 Ctrl+C 直接退出
        static volatile CancellationTokenSource currentCts;

        // 是否有一行以回车符刷新的进度尚未换行
        static bool progressLineOpen;

        // ---------- 进度显示 ----------

        static string Pct(double pct)
        {
            return pct.ToString("0.0").PadLeft(5);
        }

        static void ShowProgress(long got, long total, Stopwatch sw)
        {
            double speed = got / Math.Max(sw.Elapsed.TotalSeconds, 1e-6);
            string msg;
            if (total > 0)
            {
                double pct = Math.Min(got * 100.0 / total, 100.0);
                const int barLen = 20;
                int filled = (int)Math.Min(barLen, barLen * got / total);
                string bar = new string('#', filled) + new string('-', barLen - filled);
                msg = "\r  [" + bar + "] " + Pct(pct) + "% " + Mc.Human(got) + "/" + Mc.Human(total) + " " + Mc.Human(speed) + "/s";
            }
            else
            {
                msg = "\r  " + Mc.Human(got) + " " + Mc.Human(speed) + "/s";
            }
            Console.Write(msg);
            progressLineOpen = true;
        }

        static void EndProgressLine()
        {
            if (progressLineOpen)
                Console.WriteLine();
            progressLineOpen = false;
        }

        static string RenderBar(long got, long total)
        {
            const int width = 20;
            if (total > 0)
            {
                double pct = Math.Min(got * 100.0 / total, 100.0);
                int filled = (int)Math.Min(width, width * got / total);
                string bar = new string('#', filled) + new string('-', width - filled);
                return "[" + bar + "] " + Pct(pct) + "% " + Mc.Human(got) + "/" + Mc.Human(total);
            }
            return Mc.Human(got);
        }

        // ---------- 清单统计 ----------

        static void SourceTypeStats(ManifestResult r, Dictionary<string, int> stats, out int other)
        {
            other = 0;
            foreach (var item in r.Versions)
            {
                var vid = Mc.GetString(item, "id", "");
                var t = Mc.GetString(item, "type", "");
                if (Mc.AprilFools.Contains(vid))
                    stats["april"]++;
                else if (stats.ContainsKey(t))
                    stats[t]++;
                else
                    other++;
            }
        }

        static Dictionary<string, object> GetVersionJson(string vid, string source, out string actual, CancellationToken token)
        {
            var info = versions[vid];
            if (!info.Has(source))
                throw new InvalidOperationException(Mc.SourceName(source) + " 源没有该版本");
            try
            {
                actual = source;
                return versions.GetVersionJson(vid, source, token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                var fallback = Mc.OtherSource(source);
                if (!info.Has(fallback))
                    throw;
                Console.WriteLine("  ! " + Mc.SourceName(source) + " 解析失败（" + e.Message + "），回退 " + Mc.SourceName(fallback));
                actual = fallback;
                return versions.GetVersionJson(vid, fallback, token);
            }
        }

        // ---------- 下载 ----------

        static void DownloadFile(string url, string path, long size, string sha1, CancellationToken token)
        {
            var sw = Stopwatch.StartNew();
            double last = -1;
            long lastGot = 0, lastTotal = 0;
            Mc.DownloadFile(url, path, size, sha1, (got, total) =>
            {
                lastGot = got;
                lastTotal = total;
                double now = sw.Elapsed.TotalSeconds;
                if (now - last >= 0.1)
                {
                    last = now;
                    ShowProgress(got, total, sw);
                }
            }, token);
            ShowProgress(lastGot, lastTotal > 0 ? lastTotal : size, sw);
            EndProgressLine();
        }

        /// <summary>下载单个任务，返回实际字节数。</summary>
        static long DownloadOne(DownloadTask task, CancellationToken token)
        {
            if (File.Exists(task.Path) && !string.IsNullOrEmpty(task.Sha1))
            {
                if (string.Equals(Mc.Sha1OfFile(task.Path), task.Sha1, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("  文件已存在且 SHA1 一致，跳过下载");
                    return task.Size > 0 ? task.Size : new FileInfo(task.Path).Length;
                }
            }

            try
            {
                DownloadFile(task.Url, task.Path, task.Size, task.Sha1, token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                var official = task.OfficialUrl;
                if (string.IsNullOrEmpty(official) || official == task.Url)
                    throw;
                EndProgressLine();
                Console.WriteLine("  下载失败: " + e.Message);
                Console.WriteLine("  回退官方链接: " + official);
                DownloadFile(official, task.Path, task.Size, task.Sha1, token);
            }
            return task.Size > 0 ? task.Size : new FileInfo(task.Path).Length;
        }

        // ---------- 版本解析 ----------

        static bool IsDigits(string s)
        {
            return s.Length > 0 && s.All(c => c >= '0' && c <= '9');
        }

        static string NormalizeType(string t)
        {
            var low = t.Trim().ToLowerInvariant();
            switch (low)
            {
                case "r":
                case "release":
                case "正式版":
                    return "release";
                case "s":
                case "snapshot":
                case "预览版":
                    return "snapshot";
                case "a":
                case "april":
                case "愚人节":
                    return "april";
                case "oa":
                case "old_alpha":
                    return "old_alpha";
                case "ob":
                case "old_beta":
                    return "old_beta";
                default:
                    return low;
            }
        }

        static string ResolveVersionToken(string token)
        {
            token = token.Trim();
            if (token.Length == 0)
                throw new UserError("版本为空");

            if (IsDigits(token))
            {
                if (lastList.Count == 0)
                    throw new UserError("尚未使用过 list 命令，无法使用序号。请先运行 list。");
                string vid;
                if (lastList.TryGetValue(token.PadLeft(4, '0'), out vid))
                    return vid;
                throw new UserError("序号 " + token + " 不在上次 list 结果中");
            }

            if (versions.Contains(token))
                return token;

            var low = token.ToLowerInvariant();
            var matches = versions.Order.Where(k => k.ToLowerInvariant().Contains(low)).ToList();
            if (matches.Count == 1)
                return matches[0];
            if (matches.Count > 1)
                throw new UserError("“" + token + "” 匹配到多个版本，请写完整：" + string.Join(", ", matches.Take(10)));
            throw new UserError("没有找到版本 “" + token + "”");
        }

        // ---------- 输入解析 ----------

        /// <summary>等价于 Python shlex.split(s, posix=True)。</summary>
        static List<string> ShlexSplit(string s)
        {
            var parts = new List<string>();
            var cur = new StringBuilder();
            bool inToken = false;
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c))
                {
                    if (inToken)
                    {
                        parts.Add(cur.ToString());
                        cur.Clear();
                        inToken = false;
                    }
                    i++;
                }
                else if (c == '\\')
                {
                    if (i + 1 >= s.Length)
                        throw new UserError("No escaped character");
                    cur.Append(s[i + 1]);
                    inToken = true;
                    i += 2;
                }
                else if (c == '\'')
                {
                    int end = s.IndexOf('\'', i + 1);
                    if (end < 0)
                        throw new UserError("No closing quotation");
                    cur.Append(s, i + 1, end - i - 1);
                    inToken = true;
                    i = end + 1;
                }
                else if (c == '"')
                {
                    i++;
                    bool closed = false;
                    while (i < s.Length)
                    {
                        char d = s[i];
                        if (d == '"')
                        {
                            closed = true;
                            i++;
                            break;
                        }
                        if (d == '\\' && i + 1 < s.Length && (s[i + 1] == '"' || s[i + 1] == '\\'))
                        {
                            cur.Append(s[i + 1]);
                            i += 2;
                            continue;
                        }
                        cur.Append(d);
                        i++;
                    }
                    if (!closed)
                        throw new UserError("No closing quotation");
                    inToken = true;
                }
                else
                {
                    cur.Append(c);
                    inToken = true;
                    i++;
                }
            }
            if (inToken)
                parts.Add(cur.ToString());
            return parts;
        }

        static readonly Regex OutPathPattern = new Regex("(out-)([\"'])(.*?)\\2");

        /// <summary>提取 out-"..." 路径，避免 shlex 吃掉 Windows 反斜杠与末尾引号。</summary>
        static List<string> ParseInputLine(string line)
        {
            var paths = new List<string>();
            var newLine = OutPathPattern.Replace(line, m =>
            {
                paths.Add(m.Groups[3].Value);
                return "out-__PATH_" + (paths.Count - 1) + "__";
            });

            List<string> parts;
            try
            {
                parts = ShlexSplit(newLine);
            }
            catch (UserError e)
            {
                throw new UserError("命令解析失败: " + e.Message);
            }

            const string prefix = "out-__PATH_";
            var restored = new List<string>();
            foreach (var p in parts)
            {
                int idx;
                if (p.StartsWith(prefix, StringComparison.Ordinal) && p.EndsWith("__", StringComparison.Ordinal)
                    && int.TryParse(p.Substring(prefix.Length, p.Length - prefix.Length - 2), out idx)
                    && idx >= 0 && idx < paths.Count)
                    restored.Add("out-" + paths[idx]);
                else
                    restored.Add(p);
            }
            return restored;
        }

        // ---------- shell 命令 ----------

        static void PrintHelp()
        {
            Console.WriteLine("命令:");
            Console.WriteLine("  list [数量] [-D=M/B] [-S=搜索] [-T=类型] [-a]");
            Console.WriteLine("        列出合并后的版本，带 0001 起始序号");
            Console.WriteLine("        -D=M/B       按下载源过滤（M=Mojang, B=BMCLAPI）");
            Console.WriteLine("        -S=关键字    按版本 ID 搜索");
            Console.WriteLine("        -T=类型      类型过滤：R=release, S=snapshot, a=april,");
            Console.WriteLine("                     oa=old_alpha, ob=old_beta（可逗号组合）");
            Console.WriteLine("        数量省略默认 30，all 或 -a 显示全部");
            Console.WriteLine("  april                       列出已知愚人节版本");
            Console.WriteLine("  info <版本ID或序号>         查看版本在 Mojang / BMCLAPI 的可用情况");
            Console.WriteLine("  get <版本[,版本...]> [M/B] [out-\"路径\"] [Y/N]");
            Console.WriteLine("        下载 client.jar；版本可用 list 序号，逗号支持中英文");
            Console.WriteLine("        未指定源时默认 BMCLAPI；未指定路径时用系统下载文件夹");
            Console.WriteLine("        Y 表示下载后退出，N 或不写表示不退出");
            Console.WriteLine("  outdir [目录]               查看或设置默认下载目录");
            Console.WriteLine("  quit / Exit                 退出");
            Console.WriteLine("提示: 版本 ID 含空格时用引号，如 get \"3D Shareware v1.34\" B");
        }

        static readonly Regex CommaSplit = new Regex("[,，]");

        static void CmdList(List<string> args)
        {
            int limit = 30;
            bool showAll = false;
            string sourceFilter = null;
            string search = null;
            var typeFilter = new HashSet<string>();

            foreach (var a in args)
            {
                if (a == "-a" || a == "--all" || a == "all")
                    showAll = true;
                else if (IsDigits(a))
                    limit = int.Parse(a);
                else if (a.StartsWith("-D="))
                {
                    var val = a.Substring(3).Trim().ToUpperInvariant();
                    if (val == "M" || val == "MOJANG")
                        sourceFilter = Mc.Mojang;
                    else if (val == "B" || val == "BMCLAPI")
                        sourceFilter = Mc.Bmclapi;
                    else
                        Console.WriteLine("忽略无效 -D 值: " + a.Substring(3).Trim());
                }
                else if (a.StartsWith("-S="))
                    search = a.Substring(3).Trim();
                else if (a.StartsWith("-T="))
                {
                    foreach (var t in CommaSplit.Split(a.Substring(3)))
                        if (t.Trim().Length > 0)
                            typeFilter.Add(NormalizeType(t));
                }
                else
                    Console.WriteLine("忽略未知参数: " + a);
            }

            var items = new List<VersionInfo>();
            foreach (var vid in versions.Order)
            {
                var v = versions[vid];
                if (!string.IsNullOrEmpty(search) && !vid.ToLowerInvariant().Contains(search.ToLowerInvariant()))
                    continue;
                if (sourceFilter != null && !v.Has(sourceFilter))
                    continue;
                if (typeFilter.Count > 0 && !typeFilter.Contains(Mc.DisplayType(vid, v.Type)))
                    continue;
                items.Add(v);
            }

            int total = items.Count;
            var shown = showAll || limit == 0 ? items : items.Take(limit).ToList();

            if (!showAll && limit != 0 && total > shown.Count)
                Console.WriteLine("共 " + total + " 个版本，显示最近 " + shown.Count + " 个（list all 查看全部）");
            else
                Console.WriteLine("共 " + total + " 个版本");

            lastList = new Dictionary<string, string>();
            for (int i = 0; i < shown.Count; i++)
            {
                var v = shown[i];
                var idx = (i + 1).ToString("D4");
                var flags = (v.Has(Mc.Mojang) ? "M" : "-") + (v.Has(Mc.Bmclapi) ? "B" : "-");
                var april = Mc.AprilFools.Contains(v.Id) ? " [愚人节]" : "";
                Console.WriteLine("  " + idx + "  " + v.Id.PadRight(30) + " " + v.Type.PadRight(10) + " " + v.Date + "  " + flags + april);
                lastList[idx] = v.Id;
            }
            Console.WriteLine("源标记: M=Mojang  B=BMCLAPI");
        }

        static string AvailableSources(VersionInfo v, string sep)
        {
            return string.Join(sep, new[] { Mc.Mojang, Mc.Bmclapi }.Where(v.Has).Select(Mc.SourceName));
        }

        static void CmdApril()
        {
            var found = Mc.AprilFools.Where(versions.Contains).ToList();
            if (found.Count == 0)
            {
                Console.WriteLine("未在版本列表中匹配到已知愚人节版本。");
                return;
            }
            Console.WriteLine("已知愚人节版本 " + found.Count + " 个：");
            foreach (var vid in found.OrderBy(x => versions[x].ReleaseTime, StringComparer.Ordinal))
            {
                var v = versions[vid];
                Console.WriteLine("  " + vid.PadRight(24) + " " + v.Type.PadRight(10) + " " + v.Date + "  可用源: " + AvailableSources(v, " + "));
            }
        }

        static void CmdInfo(List<string> args)
        {
            if (args.Count == 0)
            {
                Console.WriteLine("用法: info <版本ID或序号>");
                return;
            }
            string vid;
            try
            {
                vid = ResolveVersionToken(args[0]);
            }
            catch (UserError e)
            {
                Console.WriteLine("错误: " + e.Message);
                return;
            }

            var v = versions[vid];
            Console.WriteLine("版本: " + v.Id);
            Console.WriteLine("  类型: " + v.Type);
            Console.WriteLine("  发布时间: " + v.ReleaseTime);
            foreach (var src in new[] { Mc.Mojang, Mc.Bmclapi })
            {
                if (v.Has(src))
                    Console.WriteLine("  " + Mc.SourceName(src) + " 版本 JSON: " + v.Sources[src]);
                else
                    Console.WriteLine("  " + Mc.SourceName(src) + ": 不可用");
            }
            if (Mc.AprilFools.Contains(v.Id))
                Console.WriteLine("  标记: 愚人节版本");
            Console.WriteLine();
        }

        /// <summary>返回 true 表示下载结束后退出。</summary>
        static bool CmdGet(List<string> args, CancellationToken token)
        {
            string vidExpr = null;
            string source = null;
            string @out = outDir;
            bool exitAfter = false;

            foreach (var t in args)
            {
                if (t == "M" || t == "m" || t == "mojang" || t == "Mojang" || t == "MOJANG")
                    source = Mc.Mojang;
                else if (t == "B" || t == "b" || t == "bmclapi" || t == "BMCLAPI" || t == "Bmclapi")
                    source = Mc.Bmclapi;
                else if (t.ToLowerInvariant() == "y" || t.ToLowerInvariant() == "yes")
                    exitAfter = true;
                else if (t.ToLowerInvariant() == "n" || t.ToLowerInvariant() == "no")
                    exitAfter = false;
                else if (t.StartsWith("out-"))
                    @out = t.Substring(4);
                else if (vidExpr == null)
                    vidExpr = t;
                else
                    Console.WriteLine("忽略未知参数: " + t);
            }

            if (string.IsNullOrEmpty(vidExpr))
            {
                Console.WriteLine("用法: get <版本[,版本...]> [M/B] [out-\"路径\"] [Y/N]");
                return false;
            }
            if (string.IsNullOrEmpty(@out))
            {
                Console.WriteLine("输出路径为空");
                return false;
            }

            var tokens = CommaSplit.Split(vidExpr).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            if (tokens.Count == 0)
            {
                Console.WriteLine("版本列表为空");
                return false;
            }

            var targets = new List<string>();
            foreach (var tok in tokens)
            {
                try
                {
                    var vid = ResolveVersionToken(tok);
                    if (!targets.Contains(vid))
                        targets.Add(vid);
                }
                catch (UserError e)
                {
                    Console.WriteLine("错误: " + e.Message);
                    return false;
                }
            }

            var tasks = new List<DownloadTask>();
            foreach (var target in targets)
            {
                var info = versions[target];
                var src = source ?? (info.Has(Mc.Bmclapi) ? Mc.Bmclapi : Mc.Mojang);
                if (!info.Has(src))
                {
                    Console.WriteLine("跳过 " + target + ": " + Mc.SourceName(src) + " 源不可用");
                    continue;
                }
                Dictionary<string, object> vj;
                string actual;
                try
                {
                    vj = GetVersionJson(target, src, out actual, token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    Console.WriteLine("跳过 " + target + ": 解析版本 JSON 失败: " + e.Message);
                    continue;
                }

                var dl = ClientDownload.From(vj);
                if (dl == null)
                {
                    Console.WriteLine("跳过 " + target + ": 版本 JSON 中没有 client.jar");
                    continue;
                }

                tasks.Add(new DownloadTask
                {
                    Target = target,
                    Actual = actual,
                    Url = Mc.ResolveUrl(dl.Url, actual),
                    OfficialUrl = dl.Url,
                    Size = dl.Size,
                    Sha1 = dl.Sha1,
                    Path = Path.Combine(@out, target + ".jar"),
                });
            }

            if (tasks.Count == 0)
            {
                Console.WriteLine("没有可下载的版本。");
                return exitAfter;
            }

            Console.WriteLine("共 " + tasks.Count + " 个版本待下载，输出目录: " + Path.GetFullPath(@out));
            long overallTotal = tasks.Sum(t => t.Size);
            long overallGot = 0;
            var overallSw = Stopwatch.StartNew();
            var failed = new List<string>();

            for (int i = 0; i < tasks.Count; i++)
            {
                var task = tasks[i];
                Console.WriteLine();
                Console.WriteLine("[" + (i + 1) + "/" + tasks.Count + "] " + task.Target + "  源: " + Mc.SourceName(task.Actual));
                Console.WriteLine("  实际链接: " + task.Url);
                try
                {
                    overallGot += DownloadOne(task, token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    EndProgressLine();
                    Console.WriteLine("  下载失败: " + e.Message);
                    failed.Add(task.Target);
                    continue;
                }

                if (tasks.Count > 1)
                    Console.WriteLine("  总进度: " + RenderBar(overallGot, overallTotal));
            }

            if (tasks.Count > 1)
            {
                Console.WriteLine();
                Console.WriteLine("全部完成，总用时 " + overallSw.Elapsed.TotalSeconds.ToString("0.0") + "s");
                Console.WriteLine("总进度: " + RenderBar(overallGot, overallTotal));
            }
            if (failed.Count > 0)
                Console.WriteLine("失败 " + failed.Count + " 个: " + string.Join(", ", failed));

            return exitAfter;
        }

        /// <summary>执行一条命令；返回 true 表示退出 shell。</summary>
        static bool Dispatch(string cmd, List<string> args, CancellationToken token)
        {
            switch (cmd)
            {
                case "quit":
                case "exit":
                    Console.WriteLine("已退出");
                    return true;
                case "help":
                case "?":
                    PrintHelp();
                    break;
                case "list":
                    CmdList(args);
                    break;
                case "april":
                    CmdApril();
                    break;
                case "info":
                    CmdInfo(args);
                    break;
                case "get":
                    if (CmdGet(args, token))
                    {
                        Console.WriteLine("已退出");
                        return true;
                    }
                    break;
                case "outdir":
                    if (args.Count > 0)
                        outDir = args[0];
                    Console.WriteLine("下载目录: " + Path.GetFullPath(outDir));
                    break;
                default:
                    Console.WriteLine("未知命令: " + cmd + "（help 查看帮助）");
                    break;
            }
            return false;
        }

        static void Repl()
        {
            while (true)
            {
                Console.Write("mc> ");
                var line = Console.ReadLine();
                if (line == null)
                {
                    Console.WriteLine();
                    Console.WriteLine("已退出");
                    return;
                }
                line = line.Trim();
                if (line.Length == 0)
                    continue;

                List<string> parts;
                try
                {
                    parts = ParseInputLine(line);
                }
                catch (UserError e)
                {
                    Console.WriteLine("命令解析失败: " + e.Message);
                    continue;
                }
                if (parts.Count == 0)
                    continue;

                var cmd = parts[0].ToLowerInvariant();
                var args = parts.Skip(1).ToList();

                var cts = new CancellationTokenSource();
                currentCts = cts;
                try
                {
                    if (Dispatch(cmd, args, cts.Token))
                        return;
                }
                catch (OperationCanceledException)
                {
                    progressLineOpen = false;
                    Console.WriteLine();
                    Console.WriteLine("已取消当前操作");
                }
                catch (Exception e)
                {
                    EndProgressLine();
                    Console.WriteLine("出错: " + e.Message);
                }
                finally
                {
                    currentCts = null;
                }
            }
        }

        static void OnCancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            var cts = currentCts;
            if (cts != null)
            {
                // 正在执行命令：只取消当前操作，回到提示符
                e.Cancel = true;
                try
                {
                    cts.Cancel();
                }
                catch (Exception)
                {
                }
                return;
            }
            Console.WriteLine();
            Console.WriteLine("已退出");
        }

        static void PrintStats(List<ManifestResult> results)
        {
            Console.WriteLine("扫描结果统计：");
            foreach (var pair in new[] { new[] { Mc.Mojang, "M" }, new[] { Mc.Bmclapi, "B" } })
            {
                Console.WriteLine(pair[1] + "：");
                var r = results.FirstOrDefault(x => x.Source == pair[0]);
                if (r == null || r.Data == null)
                {
                    Console.WriteLine("  （拉取失败，无数据）");
                    continue;
                }
                var st = new Dictionary<string, int>
                {
                    { "release", 0 }, { "snapshot", 0 }, { "april", 0 }, { "old_alpha", 0 }, { "old_beta", 0 },
                };
                int other;
                SourceTypeStats(r, st, out other);
                Console.WriteLine("  release（正式版）：" + st["release"]);
                Console.WriteLine("  snapshot（预览版）：" + st["snapshot"]);
                Console.WriteLine("  april（愚人节）：" + st["april"]);
                Console.WriteLine("  old_alpha：" + st["old_alpha"]);
                Console.WriteLine("  old_beta：" + st["old_beta"]);
                if (other > 0)
                    Console.WriteLine("  其他类型：" + other);
            }
        }

        static void Main()
        {
            Mc.UserAgent = "mc-jar-downloader/1.0 (+cli)";
            Console.CancelKeyPress += OnCancelKeyPress;

            Console.WriteLine("Minecraft client.jar 下载器（Mojang + BMCLAPI 双源）");
            Console.WriteLine("正在并发拉取版本清单...");
            var sw = Stopwatch.StartNew();
            var results = ManifestResult.FetchBoth();
            double totalElapsed = sw.Elapsed.TotalSeconds;

            foreach (var r in results)
            {
                if (r.Data != null)
                    Console.WriteLine("  [" + Mc.SourceName(r.Source) + "] 成功：" + r.Versions.Count() + " 个版本，耗时 " + r.Elapsed.ToString("0.00") + "s");
                else
                    Console.WriteLine("  [" + Mc.SourceName(r.Source) + "] 失败：" + r.Error.Message + "，耗时 " + r.Elapsed.ToString("0.00") + "s");
            }

            versions = VersionIndex.Merge(results);
            if (versions.Count == 0)
            {
                Console.WriteLine("两个源都拉取失败，退出。");
                return;
            }

            Console.WriteLine("扫描完成，总用时 " + totalElapsed.ToString("0.00") + "s");
            Console.WriteLine();
            PrintStats(results);
            Console.WriteLine();
            PrintHelp();
            Console.WriteLine();

            outDir = Mc.DefaultDownloadDir();
            Repl();
        }
    }
}
