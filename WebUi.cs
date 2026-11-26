using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web;
using System.Web.Script.Serialization;

namespace YundouScore
{
    internal sealed class WebUi
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false);
        private readonly TcpListener listener;
        private readonly object gate = new object();
        private readonly string token;
        private readonly bool fixture;
        private readonly Dictionary<string, Receipt> provinces = new Dictionary<string, Receipt>();
        private readonly string results;
        private string host, origin, phase = "idle", message = "", account = "", file, filename;
        private string mode = "all", resultDirectory;
        private long wakeAt;
        private CancellationTokenSource stop;
        private Thread worker;
        private bool closing;
        private const string Workbook = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

        public sealed class Receipt
        {
            public string name, status, source;
            public int count;
        }

        internal static int Run(bool fixture) { return new WebUi(fixture).Serve(); }
        private WebUi(bool fixture)
        {
            this.fixture = fixture;
            listener = new TcpListener(IPAddress.Loopback, fixture ? 32160 : 0);
            byte[] random = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(random);
            token = BitConverter.ToString(random).Replace("-", "");
            results = Path.Combine(Path.GetTempPath(), "YundouScore-" + Guid.NewGuid().ToString("N"));
        }

        private int Serve()
        {
            listener.Start(16);
            host = "127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);
            origin = "http://" + host;
            Console.WriteLine(origin + "/");
            Console.WriteLine("关闭工作台请点击网页右上角「退出工作台」，或按 Ctrl+C。");
            if (!fixture)
            {
                try { Process.Start(new ProcessStartInfo(origin + "/") { UseShellExecute = true }); }
                catch (Exception) { Console.WriteLine("请在 Edge 中打开以上本地地址。"); }
            }
            var slots = new SemaphoreSlim(12);
            ConsoleCancelEventHandler cancel = (s, e) => { e.Cancel = true; Exit(); };
            Console.CancelKeyPress += cancel;
            try
            {
                while (true)
                {
                    TcpClient client;
                    try { client = listener.AcceptTcpClient(); }
                    catch (SocketException) { lock (gate) { if (!closing) throw; } break; }
                    catch (InvalidOperationException) { lock (gate) { if (!closing) throw; } break; }
                    if (!slots.Wait(0)) { client.Close(); continue; }
                    ThreadPool.QueueUserWorkItem(_ => { try { Handle(client); } finally { slots.Release(); } });
                }
            }
            finally
            {
                Console.CancelKeyPress -= cancel;
                Exit();
                Thread job;
                lock (gate) job = worker;
                if (job != null) job.Join();
                // Allow a result response already in progress to release its open file.
                for (int i = 0; i < 12; ++i) slots.Wait();
                slots.Dispose();
                try { if (Directory.Exists(results)) Directory.Delete(results, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            return 0;
        }

        private void Exit()
        {
            lock (gate)
            {
                if (closing) return;
                closing = true;
                if (stop != null) stop.Cancel();
                listener.Stop();
            }
        }

        private void Handle(TcpClient client)
        {
            using (client)
            {
                client.ReceiveTimeout = 4000;
                client.SendTimeout = 6000;
                var stream = client.GetStream();
                try { Dispatch(stream); }
                catch (InvalidDataException) { try { Error(stream, 400, "无效请求。"); } catch (IOException) { } }
                catch (IOException) { }
                catch (Exception) { try { Error(stream, 500, "本机程序处理请求时出错。"); } catch (IOException) { } }
            }
        }

        private void Dispatch(NetworkStream stream)
        {
            var buffer = new byte[8192];
            int size = 0, end = -1;
            while (size < buffer.Length)
            {
                int n = stream.Read(buffer, size, buffer.Length - size);
                if (n == 0) return;
                int old = size;
                size += n;
                for (int i = Math.Max(0, old - 3); i + 3 < size; i++)
                    if (buffer[i] == 13 && buffer[i + 1] == 10 && buffer[i + 2] == 13 && buffer[i + 3] == 10) { end = i + 4; break; }
                if (end >= 0) break;
            }
            if (end < 0) throw new InvalidDataException();
            var lines = Encoding.ASCII.GetString(buffer, 0, end - 4).Split(new[] { "\r\n" }, StringSplitOptions.None);
            var first = lines[0].Split(' ');
            if (first.Length != 3 || first[2] != "HTTP/1.1") throw new InvalidDataException();
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0 || Char.IsWhiteSpace(line[0])) throw new InvalidDataException();
                var name = line.Substring(0, colon);
                if (headers.ContainsKey(name)) throw new InvalidDataException();
                headers.Add(name, line.Substring(colon + 1).Trim());
            }
            if (!String.Equals(Get(headers, "Host"), host, StringComparison.Ordinal) || headers.ContainsKey("Transfer-Encoding"))
            { Error(stream, 403, "仅允许访问本地工作台。"); return; }
            var method = first[0];
            var path = first[1];
            if (method == "GET" && (path == "/" || path == "/app.css" || path == "/app.js"))
            {
                string resource = path == "/" ? "index.html" : path.Substring(1);
                using (var embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream("web/" + resource))
                using (var reader = new StreamReader(embedded, Utf8))
                {
                    var content = reader.ReadToEnd();
                    if (path == "/") content = content.Replace("__SESSION_TOKEN__", token);
                    Send(stream, 200, path == "/" ? "text/html" : path == "/app.css" ? "text/css" : "text/javascript", Utf8.GetBytes(content));
                }
                return;
            }
            if (!path.StartsWith("/api/", StringComparison.Ordinal)) { Error(stream, 404, "找不到页面。"); return; }
            var site = Get(headers, "Sec-Fetch-Site");
            if (!String.Equals(Get(headers, "X-Yundou-Token"), token, StringComparison.Ordinal) ||
                (site != null && site != "same-origin") ||
                (method == "POST" && !String.Equals(Get(headers, "Origin"), origin, StringComparison.Ordinal)))
            { Error(stream, 403, "请从本地工作台发起操作。"); return; }
            if (method == "GET" && path == "/api/status")
            {
                object status;
                lock (gate) status = new { phase, message, account, wakeAt, mode, resultDirectory, provinces = provinces.Values.ToArray(),
                    canDownload = phase == "done" && file != null, canOpen = resultDirectory != null };
                Json(stream, 200, status); return;
            }
            if (method == "GET" && path.StartsWith("/api/cutoffs?", StringComparison.Ordinal))
            {
                try
                {
                    var queryText = Program.NormalizeQuery(HttpUtility.ParseQueryString(path.Substring("/api/cutoffs?".Length))["query"]);
                    Json(stream, 200, fixture ? new Dictionary<string, int> { { "天津", 160 } } : ProvinceExport.ReadCutoffs(queryText));
                }
                catch (Exception ex)
                {
                    if (!(ex is ArgumentException || ex is InvalidDataException || ex is IOException || ex is InvalidOperationException)) throw;
                    Error(stream, 400, "无法读取本地高中人数设置。");
                }
                return;
            }
            if (method == "GET" && path == "/api/result")
            {
                FileStream workbook = null;
                string downloadName;
                lock (gate)
                {
                    downloadName = filename;
                    if (phase == "done" && file != null) workbook = File.OpenRead(file);
                }
                if (workbook == null) { Error(stream, 404, "成绩表尚未生成。"); return; }
                using (workbook)
                {
                    Header(stream, 200, Workbook, workbook.Length, "Content-Disposition: attachment; filename=\"YundouScore.xlsx\"; filename*=UTF-8''" + Uri.EscapeDataString(downloadName) + "\r\n");
                    workbook.CopyTo(stream);
                }
                return;
            }
            if (method != "POST") { Error(stream, 404, "找不到操作。"); return; }
            if (path == "/api/exit") { Json(stream, 200, new { ok = true }); Exit(); return; }
            if (path == "/api/open")
            {
                string folder;
                lock (gate) folder = resultDirectory;
                if (folder == null || !Directory.Exists(folder)) { Error(stream, 404, "没有已保存的结果。"); return; }
                if (!fixture) Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true, Verb = "open" });
                Json(stream, 200, new { ok = true }); return;
            }
            if (path == "/api/stop")
            {
                lock (gate)
                {
                    if (stop != null && !stop.IsCancellationRequested)
                    { stop.Cancel(); phase = "working"; wakeAt = 0; message = "正在停止；已完成的成绩会保留。"; }
                }
                Json(stream, 200, new { ok = true }); return;
            }
            if (path != "/api/start") { Error(stream, 404, "找不到操作。"); return; }
            int len;
            if (!Int32.TryParse(Get(headers, "Content-Length"), NumberStyles.None, CultureInfo.InvariantCulture, out len) || len < 1 || len > 4096 ||
                !((Get(headers, "Content-Type") ?? "").Split(';')[0].Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException();
            var body = new byte[len];
            int used = Math.Min(size - end, len);
            Buffer.BlockCopy(buffer, end, body, 0, used);
            while (used < len)
            {
                int n = stream.Read(body, used, len - used);
                if (n == 0) throw new InvalidDataException();
                used += n;
            }
            var form = HttpUtility.ParseQueryString(Utf8.GetString(body));
            string query;
            try { query = Program.NormalizeQuery(form["query"]); }
            catch (ArgumentException ex) { Error(stream, 400, ex.Message); return; }
            var choice = form["kind"];
            var refresh = form["refresh"];
            if ((choice != "official" && choice != "unofficial") || (refresh != "0" && refresh != "1")) throw new InvalidDataException();
            ProvinceExport.Selection selection = null;
            var taskMode = form["mode"] ?? "all";
            try
            {
                if (taskMode != "all") selection = ProvinceExport.Validate(taskMode, query, form["group"], form["provinces"], form["compress"], form["cutoffs"]);
            }
            catch (ArgumentException ex) { Error(stream, 400, ex.Message); return; }
            if (!Start(query, choice == "official" ? "官方" : "民间", refresh == "1", selection)) { Error(stream, 409, "已有任务在运行，请先等待或停止。"); return; }
            Json(stream, 200, new { ok = true });
        }

        private bool Start(string query, string kind, bool refresh, ProvinceExport.Selection selection)
        {
            lock (gate)
            {
                if (closing || worker != null) return false;
                if (file != null)
                {
                    try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
                file = null;
                mode = selection == null ? "all" : selection.Mode;
                resultDirectory = null;
                provinces.Clear(); account = ""; wakeAt = 0; phase = "working";
                message = fixture ? "正在生成测试成绩…" : "正在打开专用 Edge 窗口…";
                var cancellation = new CancellationTokenSource();
                stop = cancellation;
                worker = new Thread(() => Work(query, kind, refresh, selection, cancellation)) { IsBackground = true };
                worker.Start();
                return true;
            }
        }

        private void Work(string query, string kind, bool refresh, ProvinceExport.Selection selection, CancellationTokenSource cancellation)
        {
            string path = selection == null ? Path.Combine(results, Guid.NewGuid().ToString("N") + ".xlsx") :
                fixture ? Path.Combine(results, Guid.NewGuid().ToString("N")) : ProvinceExport.NewOutput(query, kind);
            try
            {
                Directory.CreateDirectory(selection == null ? results : path);
                int code = fixture ? (selection == null ? Demo(path, cancellation.Token) : Program.DemoProvinces(path, selection, Apply, cancellation.Token)) :
                    Program.Export(query, kind, path, refresh, Apply, cancellation.Token, selection);
                lock (gate)
                {
                    if (code == 0)
                    {
                        if (selection == null) { file = path; filename = Program.OutputName(query, kind); }
                        phase = "done";
                    }
                    else phase = "failed";
                    wakeAt = 0;
                }
            }
            catch (OperationCanceledException)
            { lock (gate) { phase = cancellation.IsCancellationRequested ? "stopped" : "failed"; message = cancellation.IsCancellationRequested ? "任务已停止。已保存的省份下次可直接复用。" : "连接超时，已保存的省份下次可复用。"; wakeAt = 0; } }
            catch (Exception ex)
            { lock (gate) { phase = "failed"; message = ex.Message; wakeAt = 0; } }
            finally
            {
                lock (gate)
                {
                    if (selection != null && Directory.Exists(path))
                    {
                        try
                        {
                            if (Directory.EnumerateFileSystemEntries(path).Any()) resultDirectory = path;
                            else Directory.Delete(path);
                        }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                    worker = null; stop = null; cancellation.Dispose();
                }
            }
        }

        private void Apply(Program.Progress p)
        {
            lock (gate)
            {
                if (p.account != null) account = p.account;
                if (p.province != null) provinces[p.province] = new Receipt { name = p.province, status = p.status, source = p.source, count = p.count };
                if (stop != null && stop.IsCancellationRequested && p.phase != "done") return;
                if (p.phase != "done") phase = p.phase;
                message = p.message;
                wakeAt = p.wakeAt;
            }
        }

        private int Demo(string path, CancellationToken ct)
        {
            Apply(new Program.Progress { phase = "working", message = "本地测试数据，未访问云斗。", province = "浙江", status = "已复用", source = "本地缓存", count = 1 });
            Apply(new Program.Progress { phase = "waiting", message = "测试：等待下一下载窗口。", wakeAt = DateTimeOffset.UtcNow.AddSeconds(3).ToUnixTimeMilliseconds(), province = "广东", status = "待下载", source = "云斗" });
            if (ct.WaitHandle.WaitOne(3000)) ct.ThrowIfCancellationRequested();
            Apply(new Program.Progress { phase = "working", message = "测试成绩已写入。", province = "广东", status = "已保存", source = "云斗", count = 1 });
            Xlsx.Save(path, new List<IList<object>> { new object[] { "全国排名", "省份", "省内排名", "用户", "总分" }, new object[] { 1, "浙江", 1, "测试 A", 320 }, new object[] { 2, "广东", 1, "测试 B", 280 } },
                new List<IList<object>> { new object[] { "来源" }, new object[] { "本地测试数据，不是云斗成绩" } });
            Apply(new Program.Progress { phase = "done", message = "测试完成：2 个省份，2 条成绩。" });
            return 0;
        }

        private static string Get(IDictionary<string, string> headers, string name) { string value; return headers.TryGetValue(name, out value) ? value : null; }
        private static void Error(Stream s, int code, string text) { Json(s, code, new { error = text }); }
        private static void Json(Stream s, int code, object value) { Send(s, code, "application/json", Utf8.GetBytes(new JavaScriptSerializer().Serialize(value))); }
        private static void Send(Stream s, int code, string type, byte[] data) { Header(s, code, type + "; charset=utf-8", data.Length, ""); s.Write(data, 0, data.Length); }
        private static void Header(Stream s, int code, string type, long length, string extra)
        {
            var reason = code == 200 ? "OK" : code == 400 ? "Bad Request" : code == 403 ? "Forbidden" : code == 404 ? "Not Found" : code == 409 ? "Conflict" : "Internal Server Error";
            var text = "HTTP/1.1 " + code + " " + reason + "\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nX-Frame-Options: DENY\r\nContent-Security-Policy: default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; form-action 'self'; frame-ancestors 'none'; base-uri 'none'\r\nContent-Type: " + type + "\r\nContent-Length: " + length.ToString(CultureInfo.InvariantCulture) + "\r\n" + extra + "\r\n";
            var bytes = Encoding.ASCII.GetBytes(text); s.Write(bytes, 0, bytes.Length);
        }
    }
}
