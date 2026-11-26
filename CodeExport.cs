using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace YundouScore
{
    internal static class CodeExport
    {
        public sealed class Candidate
        {
            public string title { get; set; }
            public string path { get; set; }
            public string province { get; set; }
            public string state { get; set; }
            public string note { get; set; }
            public string download { get; set; }
            public long started { get; set; }
            public Dictionary<string, string> problems { get; set; }
        }

        internal static int Run(string query, string kind, string directory, ProvinceExport.Selection selection, Action<Program.Progress> report, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            int done = 0;
            var script = Program.SiteScript();
            using (var edge = new Edge())
            {
                var account = edge.WaitForLogin(() => report(new Program.Progress { phase = "login", message = "请在专用 Edge 窗口登录云斗。登录后自动继续。" }), ct);
                report(new Program.Progress { phase = "working", account = account, message = "正在寻找所选省份的代码导出入口…" });
                Func<string, object, string> expr = (cmd, arg) => "(" + script + ")(" + edge.Argument(cmd) + "," + edge.Argument(arg) + "," + edge.Argument(account) + ")";
                var list = edge.Evaluate<Program.Listing>(expr("list", new { query, kind }), 90000, ct);
                var selected = list.candidates.Where(c => selection.Provinces.Contains(c.province)).ToList();
                var candidates = new List<Candidate>();
                for (int i = 0; i < selected.Count; i += 8)
                    candidates.AddRange(edge.Evaluate<List<Candidate>>(expr("codeDetails", selected.Skip(i).Take(8).ToArray()), 100000, ct));
                var recent = new Queue<TimeSpan>();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var window = TimeSpan.FromSeconds(62);
                foreach (var province in selection.Provinces)
                {
                    ct.ThrowIfCancellationRequested();
                    var possible = candidates.Where(c => c.province == province).ToList();
                    var newest = possible.Where(c => c.state == "可下载").OrderByDescending(c => c.started).ThenByDescending(c => c.path, StringComparer.Ordinal).FirstOrDefault();
                    if (newest == null)
                    {
                        Failed(report, province, possible.Select(c => c.note).FirstOrDefault() ?? "未找到匹配的省级场次。");
                        continue;
                    }
                    try
                    {
                        report(new Program.Progress { phase = "working", province = province, source = "云斗", status = "正在下载", message = province + "：核对选手和题目，下载代码…" });
                        var users = edge.Evaluate<Dictionary<string, string>>(expr("codeUsers", new { newest.path }), 50000, ct);
                        var staging = Path.Combine(Path.GetTempPath(), "YundouCode-" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(staging);
                        try
                        {
                            var input = Path.Combine(staging, "download.zip");
                            while (recent.Count != 0 && clock.Elapsed - recent.Peek() >= window) recent.Dequeue();
                            if (recent.Count >= 10)
                            {
                                var wait = window - (clock.Elapsed - recent.Peek());
                                if (wait > TimeSpan.Zero)
                                {
                                    report(new Program.Progress { phase = "waiting", message = "等待云斗代码下载额度…", wakeAt = DateTimeOffset.UtcNow.Add(wait).ToUnixTimeMilliseconds() });
                                    if (ct.WaitHandle.WaitOne(wait)) ct.ThrowIfCancellationRequested();
                                }
                                while (recent.Count != 0 && clock.Elapsed - recent.Peek() >= window) recent.Dequeue();
                                report(new Program.Progress { phase = "working", message = province + "：正在下载云斗代码…" });
                            }
                            recent.Enqueue(clock.Elapsed);
                            Fetch(edge, expr, newest, input, ct);
                            var dest = Path.Combine(directory, ProvinceExport.Code(province) + "-" + province + "-answers" + (selection.Compress ? ".zip" : ""));
                            int count = AnswerArchive.Convert(input, dest, users, newest.problems, selection.Compress, ct);
                            done++;
                            report(new Program.Progress { phase = "working", province = province, source = "云斗", status = "已保存", count = count, message = province + "：已保存 " + count + " 份源码。" });
                        }
                        finally { try { Directory.Delete(staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
                    }
                    catch (Exception ex)
                    {
                        if (!(ex is IOException || ex is InvalidDataException || ex is InvalidOperationException || ex is UnauthorizedAccessException)) throw;
                        Failed(report, province, ex.Message);
                        if (ex.Message.Contains("限制代码下载频率") || ex.Message.Contains("下载过于频繁"))
                        {
                            foreach (var remaining in selection.Provinces.SkipWhile(p => p != province).Skip(1)) Failed(report, remaining, "云斗停止接收代码下载，请稍后重试。");
                            break;
                        }
                    }
                }
            }
            report(new Program.Progress { phase = done == 0 ? "failed" : "done", message = "已导出 " + done + "/" + selection.Provinces.Length + " 省的选手代码。" });
            return done == 0 ? 2 : 0;
        }

        private static void Fetch(Edge edge, Func<string, object, string> expr, Candidate c, string input, CancellationToken ct)
        {
            int length = edge.Evaluate<int>(expr("codeFetch", new { c.path, c.download }), 125000, ct);
            if (length < 22 || length > 128 * 1024 * 1024) throw new InvalidDataException("云斗没有返回有效的代码 ZIP 大小。");
            using (var f = new FileStream(input, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                for (int pos = 0; pos < length;)
                {
                    var chunk = Convert.FromBase64String(edge.Evaluate<string>(expr("codeChunk", pos), 15000, ct));
                    if (chunk.Length != Math.Min(524288, length - pos)) throw new InvalidDataException("云斗 ZIP 下载不完整。");
                    f.Write(chunk, 0, chunk.Length);
                    pos += chunk.Length;
                }
            }
            edge.Evaluate<bool>(expr("codeClear", null), 10000, ct);
        }

        private static void Failed(Action<Program.Progress> report, string province, string message)
        {
            report(new Program.Progress { phase = "working", province = province, source = "云斗", status = "下载失败", message = province + "：" + message });
        }
    }
}
