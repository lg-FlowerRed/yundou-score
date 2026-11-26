using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Microsoft.VisualBasic.FileIO;

namespace YundouScore
{
    internal static class Program
    {
        public sealed class Contest
        {
            public string title { get; set; }
            public string path { get; set; }
            public string province { get; set; }
            public string state { get; set; }
            public string note { get; set; }
            public string csv { get; set; }
            public string download { get; set; }
            public int retrySeconds { get; set; }
            public string downloadError { get; set; }
            // These fields are written only after parsing or reading the local cache.
            [System.Web.Script.Serialization.ScriptIgnore]
            public string origin { get; set; }
            [System.Web.Script.Serialization.ScriptIgnore]
            public DateTime captured { get; set; }
            public long started { get; set; }
            public int count { get; set; }
        }
        public sealed class Listing
        {
            public List<Contest> candidates { get; set; }
            public List<Contest> ignored { get; set; }
        }
        private sealed class Score
        {
            public string Province, User;
            public int OriginalRank;
            public decimal Total;
            public object[] Problems;
        }
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private static readonly RegexBody NoControls = new RegexBody();

        private static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.InputEncoding = new UTF8Encoding(false);
            if (args.Length == 1 && args[0] == "--self-test")
            {
                try { SelfTest(); return 0; }
                catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
            }
            try
            {
                if (args.Contains("--help") || args.Contains("-h")) { Help(); return 0; }
                if (args.Length == 0 || (args.Length == 1 && args[0] == "--web")) return WebUi.Run(false);
                if (args.Length == 1 && args[0] == "--web-test") return WebUi.Run(true);
                string query = null, kind = "官方", output = null;
                bool refresh = false;
                for (int i = 0; i < args.Length; i++)
                {
                    var a = args[i];
                    if (a == "--data" && i + 1 < args.Length)
                    {
                        var input = args[++i];
                        if (input == "official" || input == "官方") kind = "官方";
                        else if (input == "unofficial" || input == "民间") kind = "民间";
                        else throw new ArgumentException("--data 只接受 official 或 unofficial。");
                    }
                    else if (a == "--refresh") refresh = true;
                    else if (a == "--out" && i + 1 < args.Length) output = args[++i];
                    else if (a.StartsWith("-", StringComparison.Ordinal) || query != null) throw new ArgumentException("参数错误。赛事名含空格时请加双引号；用 --help 查看用法。");
                    else query = a;
                }
                query = NormalizeQuery(query);
                if (output == null) output = Path.Combine(Environment.CurrentDirectory, OutputName(query, kind));
                output = Path.GetFullPath(output);
                if (!String.Equals(Path.GetExtension(output), ".xlsx", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("输出文件名必须以 .xlsx 结尾。");
                if (File.Exists(output) || Directory.Exists(output)) throw new IOException("输出路径已存在，不会覆盖：" + output);
                if (!Directory.Exists(Path.GetDirectoryName(output))) throw new IOException("输出目录不存在：" + Path.GetDirectoryName(output));
                using (var cancellation = new CancellationTokenSource())
                {
                    ConsoleCancelEventHandler stop = (s, e) => { e.Cancel = true; cancellation.Cancel(); };
                    Console.CancelKeyPress += stop;
                    try {
                        int result = Export(query, kind, output, refresh, p => Console.WriteLine(p.message), cancellation.Token);
                        if (result == 0) Console.WriteLine(output);
                        return result;
                    }
                    finally { Console.CancelKeyPress -= stop; }
                }
            }
            catch (OperationCanceledException) { Console.Error.WriteLine("已取消，或浏览器连接超时。未写入成绩表。"); return 130; }
            catch (Exception ex) { Console.Error.WriteLine("失败：" + ex.Message); return 1; }
        }

        internal sealed class Progress
        {
            public string phase, message, account, province, source, status;
            public int count;
            public long wakeAt;
        }

        internal static string NormalizeQuery(string query)
        {
            query = System.Text.RegularExpressions.Regex.Replace((query ?? "").Normalize(NormalizationForm.FormKC).Trim(), @"\s+", " ");
            if (query.Length == 0 || query.Length > 100 || query.Any(Char.IsControl)) throw new ArgumentException("请输入 1–100 字的赛事名称，例如 NOIP 2026。");
            return query;
        }

        internal static string OutputName(string query, string kind)
        {
            var safeName = new string(query.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
            return String.Format(Invariant, "{0}-{1}-{2:yyyyMMdd-HHmmss}.xlsx", safeName.TrimEnd('.', ' '), kind, DateTime.Now);
        }

        internal static int Export(string query, string kind, string path, bool refresh, Action<Progress> report, CancellationToken ct, ProvinceExport.Selection selection = null)
        {
            using (var single = new Mutex(false, @"Local\YundouScore-" + Environment.UserName))
            {
                bool acquired;
                try { acquired = single.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new InvalidOperationException("已有一个云斗汇总任务在运行。");
                try
                {
                    if (selection != null && selection.Mode == "code") return CodeExport.Run(query, kind, path, selection, report, ct);
                    return Run(query, kind, path, refresh, report, ct, selection);
                }
                finally { single.ReleaseMutex(); }
            }
        }

        internal static string SiteScript()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Site.js"))
            using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
        }

        private static int Run(string query, string kind, string path, bool refresh, Action<Progress> report, CancellationToken ct, ProvinceExport.Selection selection)
        {
            Action<string> say = message => report(new Progress { phase = "working", message = message });
            ct.ThrowIfCancellationRequested();
            var scores = new List<Score>();
            var sources = new List<Contest>();
            var timer = new Stopwatch();
            var script = SiteScript();
            using (var edge = new Edge())
            {
                var account = edge.WaitForLogin(() => report(new Progress { phase = "login", message = "请在专用 Edge 窗口登录云斗并勾选「记住我」。完成后自动继续。" }), ct);
                report(new Progress { phase = "working", account = account, message = "云斗登录有效，正在查找「" + query + "」…" });
                var cache = new ScoreCache(account);
                ProvinceExport.Remember(query, selection);
                timer.Start();
                Func<string, object, string> expr = (cmd, arg) => "(" + script + ")(" + edge.Argument(cmd) + "," + edge.Argument(arg) + "," + edge.Argument(account) + ")";
                var list = edge.Evaluate<Listing>(expr("list", new { query, kind }), 90000, ct);
                if (selection != null) list.candidates = list.candidates.Where(c => selection.Provinces.Contains(c.province)).ToList();
                sources.AddRange(list.ignored);
                foreach (var c in list.ignored.Where(x => x.state == "未识别")) say("未识别：" + Clean(c.title) + "（" + Clean(c.note) + "）");
                if (list.candidates.Count == 0) { say("云斗没有找到匹配的省级场次；未生成文件。"); return 2; }
                say("找到 " + list.candidates.Count + " 个候选场次，正在核对比赛状态…");
                var finished = new List<Contest>();
                foreach (var group in Batches(list.candidates, 12))
                {
                    var detail = edge.Evaluate<List<Contest>>(expr("details", group), 150000, ct);
                    foreach (var c in detail)
                    {
                        if (c.state == "待取成绩") finished.Add(c);
                        else sources.Add(c);
                    }
                }
                var queues = finished.GroupBy(c => c.province).OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => new Queue<Contest>(g.OrderByDescending(c => c.started).ThenByDescending(c => c.path, StringComparer.Ordinal))).ToList();
                var ready = new List<Contest>();
                while (queues.Count != 0)
                {
                    var next = queues.Select(q => q.Dequeue()).ToList();
                    var got = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var group in Batches(next, 8))
                    {
                        foreach (var c in edge.Evaluate<List<Contest>>(expr("boards", group), 100000, ct))
                        {
                            if (c.state == "可下载") { ready.Add(c); got.Add(c.province); }
                            else sources.Add(c);
                        }
                    }
                    foreach (var q in queues.Where(q => q.Count != 0 && got.Contains(q.Peek().province)))
                    {
                        foreach (var old in q) { old.state = "跳过"; old.note = "同省已有更新的已结束场次和成绩"; sources.Add(old); }
                        q.Clear();
                    }
                    queues.RemoveAll(q => q.Count == 0);
                }
                var missing = new List<Contest>();
                Action<Contest, List<Score>, string, DateTime> add = (c, rows, origin, captured) => {
                    c.count = rows.Count; c.origin = origin; c.captured = captured; c.state = "已纳入"; c.csv = null;
                    scores.AddRange(rows); sources.Add(c);
                    var status = origin == "本地缓存" ? "已复用" : "已保存";
                    report(new Progress { phase = "working", message = c.province + "：" + status + " " + rows.Count + " 人的成绩。", province = c.province, status = status, source = origin, count = rows.Count });
                };
                foreach (var c in ready)
                {
                    ct.ThrowIfCancellationRequested();
                    DateTime captured = default(DateTime);
                    var cached = refresh ? null : ReadCached(cache, c, out captured);
                    if (cached != null) { add(c, cached, "本地缓存", captured); continue; }
                    c.csv = null;
                    missing.Add(c);
                    report(new Progress { phase = "working", message = "正在核对本地缓存…", province = c.province, status = "待下载", source = "云斗" });
                }
                say("已复用 " + (ready.Count - missing.Count) + " 省，" + missing.Count + " 省待下载。");
                var errors = new List<string>();
                var batches = Batches(missing, 3).ToList();
                var nextWindow = TimeSpan.Zero;
                for (int batch = 0; batch < batches.Count; batch++)
                {
                    var pending = batches[batch];
                    for (int attempt = 0; pending.Count != 0; attempt++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var remaining = nextWindow - timer.Elapsed;
                        if (remaining > TimeSpan.Zero)
                        {
                            report(new Progress { phase = "waiting", message = "等待云斗下载额度…", wakeAt = DateTimeOffset.UtcNow.Add(remaining).ToUnixTimeMilliseconds() });
                            if (ct.WaitHandle.WaitOne(remaining)) ct.ThrowIfCancellationRequested();
                        }
                        var retry = new List<Contest>();
                        var requested = timer.Elapsed;
                        int retrySeconds = 0;
                        say("正在下载 " + String.Join("、", pending.Select(x => x.province)) + "…");
                        // Finish the bounded in-flight batch, then honor Stop so completed responses survive.
                        foreach (var c in edge.Evaluate<List<Contest>>(expr("downloads", pending), 30000))
                        {
                            if (c.retrySeconds > 0) { retry.Add(c); retrySeconds = Math.Max(retrySeconds, c.retrySeconds); continue; }
                            try
                            {
                                if (!String.IsNullOrEmpty(c.downloadError)) throw new IOException(c.downloadError);
                                if (c.state != "已纳入") throw new InvalidDataException("云斗返回未知的下载状态。");
                                var rows = Parse(c);
                                if (rows.Count == 0) throw new InvalidDataException("成绩 CSV 为空。");
                                add(c, rows, "云斗", cache.Save(c.path, c.csv));
                            }
                            catch (Exception ex)
                            {
                                if (!(ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is MalformedLineException)) throw;
                                var error = c.province + "：" + Clean(ex.Message);
                                errors.Add(error);
                                report(new Progress { phase = "working", message = error, province = c.province, status = "下载失败", source = "云斗" });
                            }
                        }
                        ct.ThrowIfCancellationRequested();
                        nextWindow = requested + TimeSpan.FromSeconds(62);
                        if (retry.Count > 0)
                        {
                            if (attempt >= 2 || retrySeconds > 300) throw new IOException("云斗仍在限制 CSV 下载，请稍后重试；未生成不完整文件。");
                            var serveWindow = timer.Elapsed + TimeSpan.FromSeconds(retrySeconds);
                            if (serveWindow > nextWindow) nextWindow = serveWindow;
                        }
                        pending = retry;
                    }
                }
                if (errors.Count != 0 && selection == null) throw new IOException("未生成不完整文件，重试时会复用已保存的成绩。" + String.Join("；", errors));
            }
            ct.ThrowIfCancellationRequested();
            if (selection != null) return SaveProvinceSheets(query, kind, path, selection, scores, sources, report, ct);
            if (scores.Count == 0) { say("没有已结束且已有成绩的省份；未生成文件。"); return 2; }
            Xlsx.Save(path, ScoreSheet(scores), SourceSheet(sources, query, kind));
            report(new Progress { phase = "done", message = "完成：" + sources.Count(c => c.state == "已纳入") + " 个省份，" + scores.Count + " 条成绩。" });
            return 0;
        }

        private static int SaveProvinceSheets(string query, string kind, string directory, ProvinceExport.Selection choice, List<Score> scores, List<Contest> sources, Action<Progress> report, CancellationToken ct)
        {
            int saved = 0;
            foreach (var province in choice.Provinces)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var input = scores.Where(s => s.Province == province).ToList();
                    if (input.Count == 0) throw new InvalidDataException("未找到该省可用的成绩。");
                    int cutoff = choice.Group == "full" ? 0 : choice.Cutoffs[province];
                    var filtered = input.Where(s => ProvinceExport.MatchesGroup(s.User, province, choice.Group, cutoff)).ToList();
                    var notes = SourceSheet(sources.Where(s => s.province == province).ToList(), query, kind);
                    notes.Add(new object[] { province, "成绩范围", ProvinceExport.GroupName(choice.Group), null, null,
                        choice.Group == "full" ? "未按学段筛选" : "高中实际参赛人数：" + cutoff.ToString(Invariant) + "。按准考证号数字部分筛选：≤ 该人数为高中，> 为初中。" });
                    Xlsx.Save(Path.Combine(directory, ProvinceExport.Code(province) + "-" + province + "-" + ProvinceExport.GroupName(choice.Group) + ".xlsx"),
                        ScoreSheet(filtered, false, input.Max(s => s.Problems.Length)), notes);
                    saved++;
                    report(new Progress { phase = "working", province = province, source = "成绩表", status = "已保存", count = filtered.Count, message = province + "：已保存 " + filtered.Count + " 条成绩。" });
                }
                catch (Exception ex)
                {
                    if (!(ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException)) throw;
                    report(new Progress { phase = "working", province = province, source = "成绩表", status = "下载失败", message = province + "：" + Clean(ex.Message) });
                }
            }
            report(new Progress { phase = saved == 0 ? "failed" : "done", message = "已导出 " + saved + "/" + choice.Provinces.Length + " 省的独立成绩表。" });
            return saved == 0 ? 2 : 0;
        }

        internal static int DemoProvinces(string path, ProvinceExport.Selection choice, Action<Progress> report, CancellationToken ct)
        {
            if (choice.Mode == "code")
            {
                foreach (var p in choice.Provinces)
                {
                    ct.ThrowIfCancellationRequested();
                    var dest = Path.Combine(path, ProvinceExport.Code(p) + "-answers" + (choice.Compress ? ".zip" : ""));
                    if (choice.Compress)
                    {
                        using (var f = File.Create(dest))
                        using (var zip = new System.IO.Compression.ZipArchive(f, System.IO.Compression.ZipArchiveMode.Create))
                        using (var w = new StreamWriter(zip.CreateEntry("sample/task/task.cpp").Open())) w.Write("// synthetic test\n");
                    }
                    else
                    {
                        var task = Path.Combine(dest, "sample", "task");
                        Directory.CreateDirectory(task);
                        File.WriteAllText(Path.Combine(task, "task.cpp"), "// synthetic test\n");
                    }
                    report(new Progress { phase = "working", province = p, source = "测试", status = "已保存", count = 1, message = "已生成测试文件。" });
                }
                report(new Progress { phase = "done", message = "测试代码已保存，未访问云斗。" });
                return 0;
            }
            var scores = choice.Provinces.SelectMany(p => new[] { 12, 160, 178, 190 }.Select((n, i) => new Score {
                Province = p, User = ProvinceExport.Code(p) + "-" + n.ToString("D4", Invariant), Total = 400 - i * 20, OriginalRank = i + 1, Problems = new object[] { 100 } })).ToList();
            return SaveProvinceSheets("NOIP 2025", "测试数据", path, choice, scores, new List<Contest>(), report, ct);
        }

        private static List<Score> ReadCached(ScoreCache cache, Contest contest, out DateTime captured)
        {
            contest.csv = cache.Read(contest.path, out captured);
            if (contest.csv == null) return null;
            try { var rows = Parse(contest); return rows.Count == 0 ? null : rows; }
            catch (InvalidDataException) { return null; }
            catch (MalformedLineException) { return null; }
        }

        private static IEnumerable<List<T>> Batches<T>(IList<T> items, int size)
        {
            for (int i = 0; i < items.Count; i += size) yield return items.Skip(i).Take(size).ToList();
        }

        private static List<Score> Parse(Contest c)
        {
            var result = new List<Score>();
            using (var csv = new TextFieldParser(new StringReader((c.csv ?? "").TrimStart('﻿'))))
            {
                csv.Delimiters = new[] { "," };
                csv.HasFieldsEnclosedInQuotes = true;
                csv.TrimWhiteSpace = false;
                string[] h = csv.EndOfData ? new string[0] : csv.ReadFields();
                if (h.Length < 4 || h[0].Trim() != "#" || !new[] { "用户", "User" }.Contains(h[1].Trim()) || !new[] { "总分数", "总分", "Total Score" }.Contains(h[2].Trim()))
                    throw new InvalidDataException(Clean(c.title) + "：CSV 表头与云斗成绩表格式不符。");
                for (int j = 3; j < h.Length; ++j)
                    if (!h[j].StartsWith("#" + (j - 2).ToString(Invariant) + " ", StringComparison.Ordinal))
                        throw new InvalidDataException(Clean(c.title) + "：CSV 分题顺序未能确认。");
                c.note = String.Join(" / ", h.Skip(3));
                int index = 1;
                while (!csv.EndOfData)
                {
                    string[] fields;
                    ++index;
                    try { fields = csv.ReadFields(); }
                    catch (MalformedLineException) { throw BadRow(c, index); }
                    int rank;
                    decimal score;
                    if (fields.Length != h.Length || !Int32.TryParse(fields[0], NumberStyles.Integer, Invariant, out rank) || rank < 0 ||
                        String.IsNullOrWhiteSpace(fields[1]) || !TryScore(fields[2], out score)) throw BadRow(c, index);
                    var problems = new object[fields.Length - 3];
                    for (int j = 3; j < fields.Length; ++j)
                    {
                        var raw = fields[j].Trim();
                        decimal part;
                        if (TryScore(raw, out part)) problems[j - 3] = part;
                        else if (raw != "" && raw != "-" && raw != "—" && raw != "–") throw BadRow(c, index);
                    }
                    result.Add(new Score { Province = c.province, User = fields[1], OriginalRank = rank, Total = score, Problems = problems });
                }
            }
            return result;
        }

        private static bool TryScore(string text, out decimal score)
        {
            return Decimal.TryParse(text, NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite | NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, Invariant, out score);
        }
        private static Exception BadRow(Contest c, int row) { return new InvalidDataException(String.Format(Invariant, "{0}：CSV 第 {1} 条记录字段异常；未写入不完整的成绩表。", Clean(c.title), row)); }
        private static string Clean(string s) { return NoControls.Filter(s ?? ""); }
        private sealed class RegexBody
        {
            internal string Filter(string s) { return new string(s.Select(ch => Char.IsControl(ch) ? ' ' : ch).Take(300).ToArray()); }
        }
        private static IList<IList<object>> ScoreSheet(List<Score> scores, bool national = true, int problemCount = 0)
        {
            var head = new List<object> { national ? "全国排名" : "本表排名", "省份", national ? "省内排名" : "云斗原排名", "用户", "总分" };
            int max = scores.Count == 0 ? problemCount : Math.Max(problemCount, scores.Max(s => s.Problems.Length));
            for (int i = 1; i <= max; ++i) head.Add("第" + i.ToString(Invariant) + "题");
            var result = new List<IList<object>>(scores.Count + 1) { head };
            var sorted = scores.OrderByDescending(s => s.Total).ThenBy(s => s.Province, StringComparer.Ordinal).ThenBy(s => s.OriginalRank).ThenBy(s => s.User, StringComparer.Ordinal);
            decimal? before = null;
            int position = 0, rank = 0;
            foreach (var s in sorted)
            {
                position++;
                if (before != s.Total) rank = position;
                var row = new List<object>(head.Count) { rank, s.Province, s.OriginalRank, s.User, s.Total };
                row.AddRange(s.Problems);
                result.Add(row);
                before = s.Total;
            }
            return result;
        }
        private static IList<IList<object>> SourceSheet(List<Contest> sources, string query, string kind)
        {
            var notes = new List<IList<object>> {
                new object[] { "省份", "云斗比赛", "状态", "人数", "成绩表 / 比赛地址", "原 CSV 分题标题或备注", "数据来源", "下载时间（本地）" },
                new object[] { null, query + " / " + kind + "数据", "导出时间", null, DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", Invariant) },
                new object[] { null, "纳入条件", null, null, null, "站内比赛已结束、成绩表有可见评测记录；同省取最新符合条件场次。并非 CCF 官方发布的全国榜。" },
                new object[] { null, "排序规则", null, null, null, "使用云斗原始总分降序；同分同名次（例如 1、1、3）；分题按原 CSV 题序对齐。" },
                new object[] { null, "零分或缺考", null, null, null, "保留网站导出值；0 可能是未评到，省级场次完结不保证每人每题均已评测。" }
            };
            notes.AddRange(sources.OrderByDescending(c => c.state == "已纳入").ThenBy(c => c.province, StringComparer.Ordinal).ThenByDescending(c => c.started)
                .Select(c => (IList<object>)new object[] {c.province, c.title, c.state, c.state == "已纳入" ? (object)c.count : null,
                    Edge.Site + c.path + (c.state == "已纳入" ? "/scoreboard" : ""), c.note, c.origin,
                    c.captured == default(DateTime) ? null : c.captured.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", Invariant)}));
            return notes;
        }
        private static void Help()
        {
            Console.WriteLine("YundouScore — 云斗 NOI 批量评测成绩汇总\n\n用法：YundouScore.exe \"NOIP 2026\" [--data official|unofficial] [--out 成绩.xlsx] [--refresh]\n双击或 --web 打开本地工作台；带赛事名时走命令行，默认输出到当前目录。\n默认复用已下载成绩；--refresh 重新下载。\n仅下载当前账号在官网可见的成绩。官方与民间数据分开，默认官方。\n退出码：0 成功；1 错误；2 无可导出的成绩；130 取消或连接超时。");
        }
        private static void TestCache(Contest a, Contest b)
        {
            string root = Path.Combine(Path.GetTempPath(), "yundou-cache-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var cache = new ScoreCache("101", root);
                DateTime captured;
                var first = cache.Save(a.path, a.csv);
                var reopened = new ScoreCache("101", root);
                if (reopened.Read(a.path, out captured) != a.csv || captured != first) throw new Exception("Saved province did not survive reopening the cache");
                if (reopened.Read(b.path, out captured) != null || new ScoreCache("102", root).Read(a.path, out captured) != null)
                    throw new Exception("Cache entries crossed a contest or account boundary");
                reopened.Save(a.path, b.csv);
                if (cache.Read(a.path, out captured) != b.csv) throw new Exception("Refresh did not replace saved scores");
                var file = Directory.GetFiles(root, "*.cache", System.IO.SearchOption.AllDirectories).Single();
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite))
                { stream.Position = stream.Length - 1; int old = stream.ReadByte(); stream.Position--; stream.WriteByte((byte)(old ^ 1)); }
                if (cache.Read(a.path, out captured) != null) throw new Exception("Corrupt scores were reused");
                cache.Save(a.path, "not a scoreboard");
                var invalid = new Contest { path = a.path, province = a.province, title = a.title };
                if (ReadCached(cache, invalid, out captured) != null) throw new Exception("Invalid scoreboard was reused");
                cache.Save(a.path, a.csv);
                if (ReadCached(cache, invalid, out captured).Count != 2) throw new Exception("Resume did not parse saved scores");
                Console.WriteLine("Cache: resume, isolation, refresh and corruption checks passed.");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        private static void TestContestWireFormat(Contest candidate)
        {
            // Match the Edge path: serialize the request, then convert a JS result
            // represented as untyped dictionaries back to the C# model.
            var json = new System.Web.Script.Serialization.JavaScriptSerializer();
            var wire = json.Serialize(new[] { candidate });
            var response = json.ConvertToType<List<Contest>>(json.DeserializeObject(wire.Replace("\\/", "/"))).Single();
            if (response.path != candidate.path || response.csv != candidate.csv || response.captured != default(DateTime))
                throw new Exception("Contest browser round trip changed payload fields");

            candidate.origin = "本地缓存";
            candidate.captured = new DateTime(2026, 11, 26, 10, 0, 0, DateTimeKind.Utc);
            try
            {
                wire = json.Serialize(new[] { candidate });
                response = json.ConvertToType<List<Contest>>(json.DeserializeObject(wire)).Single();
                if (wire.Contains("\"captured\"") || wire.Contains("\"origin\"") || response.captured != default(DateTime) || response.origin != null)
                    throw new Exception("Local cache metadata leaked into the browser payload");
                var source = SourceSheet(new List<Contest> { candidate }, "测试赛事", "官方").Last();
                if (!Equals(source[6], "本地缓存") || !Equals(source[7], candidate.captured.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", Invariant)))
                    throw new Exception("Workbook lost cache provenance metadata");
            }
            finally { candidate.origin = null; candidate.captured = default(DateTime); }
            Console.WriteLine("Browser payload: contest round trip and local metadata checks passed.");
        }

        private static void SelfTest()
        {
            var a = new Contest { province = "甘肃", title = "测试一", state = "已纳入", path = "/d/NOIPC/contest/000000000000000000000001", csv = "﻿#,用户,总分数,#1 T,#2 U\r\n1,\"=SUM(1,2)\",120.5,100,20.5\r\n2,\"0003\",9.5,9.5,-\r\n" };
            var b = new Contest { province = "海南", title = "测试二", state = "已纳入", path = "/d/NOIPC/contest/000000000000000000000002", csv = "#,用户,总分数,#1 T,#2 U\n1,\"多\n行\"\"名\",120.5,90.5,30\n2,样例,100,100,\n" };
            TestContestWireFormat(a);
            TestCache(a, b);
            ProvinceExport.SelfTest();
            AnswerArchive.SelfTest();
            var ar = Parse(a);
            var br = Parse(b);
            if (ar[1].User != "0003" || ar[1].Problems[1] != null || br[0].User != "多\n行\"名") throw new Exception("CSV parser failed");
            var s = ScoreSheet(ar.Concat(br).ToList());
            if ((int)s[1][0] != 1 || (int)s[2][0] != 1 || (int)s[3][0] != 3 || (decimal)s[3][4] != 100 || (decimal)s[4][4] != 9.5m) throw new Exception("Sort or tied ranks failed");
            var test = new Contest { title = "invalid", csv = "#,用户,总分数,#1 T\n1,example,bad,90" };
            bool caught = false;
            try { Parse(test); } catch (InvalidDataException) { caught = true; }
            if (!caught) throw new Exception("Malformed CSV was accepted");
            string path = Path.Combine(Path.GetTempPath(), "yundou-score-test-" + Guid.NewGuid().ToString("N") + ".xlsx");
            Xlsx.Save(path, s, SourceSheet(new List<Contest> { a, b }, "测试赛事", "官方"));
            caught = false;
            try { Xlsx.Save(path, s, SourceSheet(new List<Contest> { a, b }, "测试赛事", "官方")); } catch (IOException) { caught = true; }
            if (!caught) throw new Exception("Existing file was overwritten");
            Console.WriteLine(path);
        }
    }
}
