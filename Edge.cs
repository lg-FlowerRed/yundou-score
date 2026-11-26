using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace YundouScore
{
    // Use a visible, dedicated Edge session. No password, cookie, or profile from another browser is read.
    internal sealed class Edge : IDisposable
    {
        internal const string Site = "https://yundouxueyuan.com";
        internal const string Area = "/d/NOIPC/";
        private readonly ClientWebSocket socket = new ClientWebSocket();
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        private Process process;
        private int messageId;
        private const int MaxMessage = 16 * 1024 * 1024;

        internal Edge()
        {
            try
            {
                var edge = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) }
                    .Where(s => !String.IsNullOrEmpty(s)).Select(s => Path.Combine(s, "Microsoft", "Edge", "Application", "msedge.exe"))
                    .FirstOrDefault(File.Exists);
                if (edge == null) throw new InvalidOperationException("找不到 Windows 自带的 Microsoft Edge。");
                var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YundouScore", "EdgeProfile");
                Directory.CreateDirectory(profile);
                // Port 0 chooses a private, ephemeral loopback port. Never use this against a user's regular profile.
                var marker = Path.Combine(profile, "DevToolsActivePort");
                var started = DateTime.UtcNow;
                process = Process.Start(new ProcessStartInfo(edge, "--user-data-dir=\"" + profile + "\" --remote-debugging-address=127.0.0.1 --remote-debugging-port=0 --no-first-run --new-window " + Site + Area) { UseShellExecute = false });
                string endpoint = null;
                for (int i = 0; i < 100 && endpoint == null; i++)
                {
                    try
                    {
                        if (File.Exists(marker) && File.GetLastWriteTimeUtc(marker) >= started.AddSeconds(-1))
                        {
                            var lines = File.ReadAllLines(marker);
                            int port;
                            if (lines.Length >= 2 && Int32.TryParse(lines[0], out port) && port > 0 && port <= 65535)
                            {
                                var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/json/list");
                                req.Proxy = null;
                                req.Timeout = 750;
                                req.ReadWriteTimeout = 750;
                                using (var res = req.GetResponse())
                                using (var reader = new StreamReader(res.GetResponseStream()))
                                {
                                    var list = json.Deserialize<List<Dictionary<string, object>>>(reader.ReadToEnd());
                                    var target = list.FirstOrDefault(x => Value(x, "type") == "page" && Value(x, "url").StartsWith(Site + Area, StringComparison.Ordinal));
                                    Uri ws;
                                    if (target != null && Uri.TryCreate(Value(target, "webSocketDebuggerUrl"), UriKind.Absolute, out ws) &&
                                        ws.Scheme == "ws" && ws.IsLoopback && ws.Port == port && ws.AbsolutePath.StartsWith("/devtools/page/", StringComparison.Ordinal)) endpoint = ws.AbsoluteUri;
                                }
                            }
                        }
                    }
                    catch (IOException) { }
                    catch (WebException) { }
                    if (endpoint == null) Thread.Sleep(150);
                }
                if (endpoint == null) throw new InvalidOperationException("连接不到本工具打开的 Edge。请先关闭本工具上一次打开的 Edge 窗口，再重试。");
                socket.Options.Proxy = null;
                using (var timeout = new CancellationTokenSource(7000)) socket.ConnectAsync(new Uri(endpoint), timeout.Token).GetAwaiter().GetResult();
            }
            catch { Dispose(); throw; }
        }

        internal string WaitForLogin(Action prompt, CancellationToken cancellation)
        {
            const string expression = @"(() => {
                if (location.origin !== 'https://yundouxueyuan.com' || !location.pathname.startsWith('/d/NOIPC/') || location.pathname.includes('/login')) return '';
                const link = document.querySelector('[data-dropdown-target=""#menu-nav-user""] > a[href]');
                const match = (link?.getAttribute('href') || '').match(/^\/d\/NOIPC\/user\/([1-9][0-9]{0,15})$/);
                return match ? match[1] : '';
            })()";
            var loginTimeout = Stopwatch.StartNew();
            bool prompted = false;
            while (loginTimeout.Elapsed < TimeSpan.FromMinutes(15))
            {
                cancellation.ThrowIfCancellationRequested();
                var id = Evaluate<string>(expression, 8000, cancellation);
                if (!String.IsNullOrEmpty(id)) return id;
                if (!prompted && loginTimeout.ElapsedMilliseconds >= 2500) { prompt(); prompted = true; }
                if (cancellation.WaitHandle.WaitOne(500)) cancellation.ThrowIfCancellationRequested();
            }
            throw new InvalidOperationException("等待云斗登录超时，请重新开始。");
        }

        internal T Evaluate<T>(string expression, int timeoutMs, CancellationToken cancellation = default(CancellationToken))
        {
            cancellation.ThrowIfCancellationRequested();
            int id = ++messageId;
            var request = json.Serialize(new { id = id, method = "Runtime.evaluate", @params = new { expression = expression, awaitPromise = true, returnByValue = true } });
            var bytes = Encoding.UTF8.GetBytes(request);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(timeoutMs);
                var token = timeout.Token;
                socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token).GetAwaiter().GetResult();
                while (true)
                {
                    var message = json.Deserialize<Dictionary<string, object>>(Receive(token));
                    if (!message.ContainsKey("id") || Convert.ToInt32(message["id"]) != id) continue;
                    if (message.ContainsKey("error")) throw new InvalidOperationException("Edge 页面已关闭或无法执行读取操作。");
                    var result = (Dictionary<string, object>)message["result"];
                    if (result.ContainsKey("exceptionDetails"))
                    {
                        var details = (Dictionary<string, object>)result["exceptionDetails"];
                        object error;
                        if (details.TryGetValue("exception", out error))
                        {
                            string text = Value((Dictionary<string, object>)error, "description").Split('\n')[0];
                            throw new InvalidOperationException(text.Length > 280 ? text.Substring(0, 280) : text);
                        }
                        throw new InvalidOperationException("读取云斗页面时出错。");
                    }
                    var remote = (Dictionary<string, object>)result["result"];
                    if (!remote.ContainsKey("value")) throw new InvalidOperationException("Edge 未返回读取结果。");
                    return json.ConvertToType<T>(remote["value"]);
                }
            }
        }

        private string Receive(CancellationToken token)
        {
            var buffer = new byte[32768];
            using (var ms = new MemoryStream())
            {
                WebSocketReceiveResult part;
                do
                {
                    part = socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).GetAwaiter().GetResult();
                    if (part.MessageType != WebSocketMessageType.Text) throw new IOException("Edge 调试连接已断开。");
                    if (ms.Length + part.Count > MaxMessage) throw new IOException("云斗响应过大，未写入不完整的成绩表。");
                    ms.Write(buffer, 0, part.Count);
                } while (!part.EndOfMessage);
                return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            }
        }

        internal string Argument(object input) { return json.Serialize(input); }
        private static string Value(Dictionary<string, object> d, string k) { object val; return d.TryGetValue(k, out val) ? Convert.ToString(val) : ""; }

        public void Dispose()
        {
            // Close the CDP-enabled browser after the run; keep only Edge's normal session in its dedicated profile.
            if (process != null)
            {
                if (socket.State == WebSocketState.Open)
                {
                    try
                    {
                        using (var wait = new CancellationTokenSource(750))
                        {
                            var close = Encoding.UTF8.GetBytes("{\"id\":999999,\"method\":\"Browser.close\"}");
                            socket.SendAsync(new ArraySegment<byte>(close), WebSocketMessageType.Text, true, wait.Token).GetAwaiter().GetResult();
                        }
                    }
                    catch (Exception) { }
                }
                try
                {
                    if (!process.WaitForExit(2000) && process.CloseMainWindow()) process.WaitForExit(3000);
                }
                catch (InvalidOperationException) { }
                process.Dispose();
                process = null;
            }
            socket.Dispose();
        }
    }
}
