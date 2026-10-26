// v0.10：手机同步 API。
// 原生 WPF 里用 TcpListener 起一个很小的 HTTP 服务，供 Android 客户端通过
// 同一 Wi-Fi / Tailscale 读写 tasks.json。不引用第三方库，不把 Key 下发给手机。

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;

namespace AdhdLite
{
    public static partial class App
    {
        class MobileRequest
        {
            public string method = "";
            public string path = "/";
            public string query = "";
            public Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string body = "";
        }

        static TcpListener mobileListener;
        static Thread mobileThread;
        static volatile bool mobileRunning = false;
        static string mobileStartError = "";
        static long mobileRev = 1;
        const int MOBILE_MAX_BODY = 1024 * 1024;

        // ------------------------------------------------------------ 令牌 / 地址
        static string NewMobileToken()
        {
            var b = new byte[8];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(b);
            var sb = new StringBuilder();
            for (int i = 0; i < b.Length; i++) sb.Append(b[i].ToString("x2"));
            return sb.ToString();
        }

        static int MobilePort()
        {
            int p = settings.mobilePort;
            if (p < 1024 || p > 65535) p = 17890;
            return p;
        }

        // Tailscale 的 100.64/10 放前面，其次局域网地址
        static string MobileUrlText()
        {
            int port = MobilePort();
            var tail = new List<string>();
            var lan = new List<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        string ip = ua.Address.ToString();
                        if (ip.StartsWith("127.") || ip.StartsWith("169.254.")) continue;
                        string u = "http://" + ip + ":" + port;
                        if (IsTailscaleIp(ip)) tail.Add(u);
                        else lan.Add(u);
                    }
                }
            }
            catch { }
            var all = new List<string>();
            all.AddRange(tail);
            all.AddRange(lan);
            if (all.Count == 0) all.Add("http://<本机IP>:" + port);
            return string.Join("\n", all.ToArray());
        }

        static bool IsTailscaleIp(string ip)
        {
            string[] p = ip.Split('.');
            if (p.Length != 4) return false;
            int a, b;
            if (!int.TryParse(p[0], out a) || !int.TryParse(p[1], out b)) return false;
            return a == 100 && b >= 64 && b <= 127;
        }

        // ------------------------------------------------------------ 启停
        static void StartMobileServer(bool quiet)
        {
            if (mobileRunning) return;
            if (testMode) { mobileStartError = "测试模式不启动手机同步"; return; }
            int port = MobilePort();
            try
            {
                var l = new TcpListener(IPAddress.Any, port);
                l.Start();
                mobileListener = l;
                mobileRunning = true;
                mobileStartError = "";
                mobileRev = TaskItem.Now();
                mobileThread = new Thread(MobileLoop);
                mobileThread.IsBackground = true;
                mobileThread.Start();
                StartupLog("手机同步已启动: 0.0.0.0:" + port);
                if (!quiet) ShowToast("手机同步已开启", "端口 " + port + " · 手机 App 填设置页里的地址和令牌");
            }
            catch (Exception ex)
            {
                mobileRunning = false;
                mobileStartError = ex.Message;
                StartupLog("手机同步启动失败: " + ex.Message);
                if (!quiet) ShowToast("手机同步启动失败", ex.Message);
            }
        }

        static void ToggleMobileSync()
        {
            if (settings.mobileOn != 0)
            {
                StopMobileServer();
                settings.mobileOn = 0;
                settings.mobilePort = MobilePort();
                SaveSettings();
                RefreshMain();
                return;
            }
            if (string.IsNullOrEmpty(settings.mobileToken)) settings.mobileToken = NewMobileToken();
            settings.mobilePort = MobilePort();
            settings.mobileOn = 1;
            SaveSettings();
            StartMobileServer(false);
            if (!string.IsNullOrEmpty(mobileStartError))
            {
                settings.mobileOn = 0;
                SaveSettings();
            }
            RefreshMain();
        }

        static void RegenerateMobileToken()
        {
            settings.mobileToken = NewMobileToken();
            SaveSettings();
            if (mobileRunning) { StopMobileServer(); StartMobileServer(true); }
            RefreshMain();
            ShowToast("手机令牌已更新", "手机 App 里要重新填这个令牌");
        }

        static void StopMobileServer()
        {
            if (!mobileRunning && mobileListener == null) return;
            mobileRunning = false;
            try { if (mobileListener != null) mobileListener.Stop(); } catch { }
            mobileListener = null;
            mobileThread = null;
            mobileStartError = "";
            StartupLog("手机同步已停止");
        }

        static void MobileLoop()
        {
            while (mobileRunning)
            {
                try
                {
                    var client = mobileListener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(delegate(object state) { HandleMobileClient((TcpClient)state); }, client);
                }
                catch
                {
                    if (!mobileRunning) break;
                    Thread.Sleep(150);
                }
            }
        }

        static void HandleMobileClient(TcpClient client)
        {
            try
            {
                client.ReceiveTimeout = 10000;
                client.SendTimeout = 10000;
                using (client)
                using (var stream = client.GetStream())
                {
                    var req = ReadMobileRequest(stream);
                    if (req == null) return;
                    // /apk 是二进制下载，不能走 JSON 路由。
                    if (req.path == "/apk" && req.method == "GET")
                    {
                        if (!MobileAuthorized(req))
                        {
                            WriteMobileResponse(stream, "401 Unauthorized", MobileError("令牌无效"));
                            return;
                        }
                        long apkSize;
                        string apkVer;
                        string apkPath = MobileApkFile(out apkSize, out apkVer);
                        if (apkPath == null)
                        {
                            WriteMobileResponse(stream, "404 Not Found", MobileError("电脑上没找到 APK"));
                            return;
                        }
                        WriteMobileFile(stream, apkPath, apkSize, apkVer);
                        return;
                    }
                    string status = "200 OK";
                    string body;
                    try { body = RouteMobile(req, ref status); }
                    catch (Exception ex)
                    {
                        status = "500 Internal Server Error";
                        body = MobileError(ex.Message);
                        StartupLog("手机 API 异常: " + ex.Message);
                    }
                    WriteMobileResponse(stream, status, body);
                }
            }
            catch { }
        }

        static MobileRequest ReadMobileRequest(NetworkStream stream)
        {
            var ms = new MemoryStream();
            var buf = new byte[4096];
            int headerEnd = -1;
            int total = 0;
            while (total < MOBILE_MAX_BODY + 16384)
            {
                int n = stream.Read(buf, 0, buf.Length);
                if (n <= 0) return null;
                ms.Write(buf, 0, n);
                total += n;
                byte[] arr = ms.ToArray();
                headerEnd = IndexOfBytes(arr, new byte[] { 13, 10, 13, 10 });
                if (headerEnd >= 0) break;
            }
            if (headerEnd < 0) return null;
            byte[] all = ms.ToArray();
            string head = Encoding.UTF8.GetString(all, 0, headerEnd);
            string[] lines = head.Replace("\r\n", "\n").Split('\n');
            if (lines.Length < 1) return null;
            var req = new MobileRequest();
            string[] first = lines[0].Split(' ');
            if (first.Length < 2) return null;
            req.method = first[0].ToUpperInvariant();
            string target = first[1];
            int q = target.IndexOf('?');
            if (q >= 0) { req.path = target.Substring(0, q); req.query = target.Substring(q + 1); }
            else req.path = target;
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                req.headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }

            int contentLength = 0;
            string cl;
            if (req.headers.TryGetValue("Content-Length", out cl)) int.TryParse(cl, out contentLength);
            if (contentLength < 0) contentLength = 0;
            if (contentLength > MOBILE_MAX_BODY) contentLength = 0;

            int bodyStart = headerEnd + 4;
            int already = all.Length - bodyStart;
            if (already < 0) already = 0;
            var body = new MemoryStream();
            if (already > 0) body.Write(all, bodyStart, Math.Min(already, contentLength));
            int need = contentLength - Math.Min(already, contentLength);
            int got = 0;
            while (need > 0)
            {
                int n = stream.Read(buf, 0, Math.Min(buf.Length, need));
                if (n <= 0) break;
                body.Write(buf, 0, n);
                need -= n;
                got += n;
                if (got > MOBILE_MAX_BODY) break;
            }
            req.body = Encoding.UTF8.GetString(body.ToArray());
            return req;
        }

        static int IndexOfBytes(byte[] data, byte[] needle)
        {
            for (int i = 0; i <= data.Length - needle.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (data[i + j] != needle[j]) { ok = false; break; }
                }
                if (ok) return i;
            }
            return -1;
        }

        static string RouteMobile(MobileRequest req, ref string status)
        {
            if (req.method == "OPTIONS")
            {
                status = "204 No Content";
                return "";
            }
            if (req.path == "/health")
            {
                var h = new Dictionary<string, object>();
                h["ok"] = true;
                h["app"] = "ADHD-lite";
                h["version"] = "0.11";
                long healthApkSize;
                string healthApkVer;
                h["apkVersion"] = MobileApkFile(out healthApkSize, out healthApkVer) == null ? "" : healthApkVer;
                h["apkSize"] = healthApkSize;
                h["now"] = TaskItem.Now();
                return Json.Serialize(h);
            }
            if (!MobileAuthorized(req))
            {
                status = "401 Unauthorized";
                return MobileError("令牌无效");
            }
            if (req.path == "/state" && req.method == "GET") return MobileStateJson();
            if (req.path == "/add" && req.method == "POST") return MobileAdd(req.body);
            if (req.path == "/update" && req.method == "POST") return MobileUpdate(req.body);
            if (req.path == "/action" && req.method == "POST") return MobileAction(req.body);
            if (req.path == "/ai" && req.method == "POST") return MobileAi(req.body);
            status = "404 Not Found";
            return MobileError("未知接口");
        }

        static bool MobileAuthorized(MobileRequest req)
        {
            string token = req.headers.ContainsKey("X-ADHD-Token") ? req.headers["X-ADHD-Token"] : "";
            if (string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(req.query))
            {
                foreach (var part in req.query.Split('&'))
                {
                    if (part.StartsWith("t=")) token = Uri.UnescapeDataString(part.Substring(2));
                }
            }
            if (string.IsNullOrEmpty(token)) return false;
            return string.Equals(token, settings.mobileToken, StringComparison.Ordinal);
        }

        static string MobileError(string message)
        {
            var d = new Dictionary<string, object>();
            d["ok"] = false;
            d["error"] = message == null ? "未知错误" : message;
            return Json.Serialize(d);
        }

        static void WriteMobileResponse(NetworkStream stream, string status, string body)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body == null ? "" : body);
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append("\r\n");
            sb.Append("Content-Type: application/json; charset=utf-8\r\n");
            sb.Append("Content-Length: ").Append(bytes.Length).Append("\r\n");
            sb.Append("Access-Control-Allow-Origin: *\r\n");
            sb.Append("Access-Control-Allow-Headers: Content-Type, X-ADHD-Token\r\n");
            sb.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
            sb.Append("Connection: close\r\n\r\n");
            byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
            stream.Write(head, 0, head.Length);
            if (bytes.Length > 0) stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        // 最新的 Android APK 放在 exe 旁边的 android\dist；绿色版 exe 没有这个目录时返回 null。
        static string MobileApkFile(out long size, out string version)
        {
            size = 0;
            version = "";
            string best = null;
            DateTime bestTime = DateTime.MinValue;
            string[] roots = new string[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "android", "dist"),
                Path.Combine(Directory.GetCurrentDirectory(), "android", "dist")
            };
            foreach (var dir in roots)
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var f in Directory.GetFiles(dir, "ADHD-lite-android-v*.apk"))
                    {
                        DateTime t = File.GetLastWriteTimeUtc(f);
                        if (t > bestTime) { bestTime = t; best = f; }
                    }
                }
                catch { }
            }
            if (best == null) return null;
            try
            {
                size = new FileInfo(best).Length;
                var m = Regex.Match(Path.GetFileName(best), @"-v(\d+(?:\.\d+){1,3})\.apk$", RegexOptions.IgnoreCase);
                if (m.Success) version = m.Groups[1].Value;
            }
            catch { }
            return best;
        }

        static void WriteMobileFile(NetworkStream stream, string path, long size, string version)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 200 OK\r\n");
            sb.Append("Content-Type: application/vnd.android.package-archive\r\n");
            sb.Append("Content-Length: ").Append(size).Append("\r\n");
            sb.Append("Content-Disposition: attachment; filename=\"ADHD-lite-android-v")
              .Append(string.IsNullOrEmpty(version) ? "latest" : version).Append(".apk\"\r\n");
            sb.Append("Access-Control-Allow-Origin: *\r\n");
            sb.Append("Access-Control-Allow-Headers: Content-Type, X-ADHD-Token\r\n");
            sb.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
            sb.Append("Connection: close\r\n\r\n");
            byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
            stream.Write(head, 0, head.Length);
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] buf = new byte[65536];
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0) stream.Write(buf, 0, n);
            }
            stream.Flush();
        }

        // ------------------------------------------------------------ 数据
        static void MobileOnUi(Action a)
        {
            var app = Application.Current;
            if (app != null && app.Dispatcher != null && !app.Dispatcher.CheckAccess())
                app.Dispatcher.Invoke(a);
            else a();
        }

        static TaskItem MobileFind(string id)
        {
            foreach (var t in tasks) if (t != null && t.id == id) return t;
            return null;
        }

        static Dictionary<string, object> MobileTaskJson(TaskItem t)
        {
            var d = new Dictionary<string, object>();
            d["id"] = t.id;
            d["title"] = t.title;
            d["priority"] = t.priority;
            d["due"] = t.due.HasValue ? (object)t.due.Value : null;
            d["done"] = t.done;
            d["doneAt"] = t.doneAt;
            d["createdAt"] = t.createdAt;
            d["startedAt"] = t.startedAt;
            d["spentMs"] = t.spentMs;
            d["estimateMin"] = t.estimateMin;
            d["lastNote"] = t.lastNote == null ? "" : t.lastNote;
            d["snoozeCount"] = t.snoozeCount;
            d["postponeCount"] = t.postponeCount;
            d["kind"] = string.IsNullOrEmpty(t.kind) ? "other" : t.kind;
            d["resumeHint"] = t.resumeHint == null ? "" : t.resumeHint;
            d["updatedAt"] = t.updatedAt;
            return d;
        }

        static string MobileStateJson()
        {
            var arr = new List<object>();
            var planArr = new List<object>();
            string planSummary = "";
            int open = 0, done = 0;
            MobileOnUi(delegate()
            {
                foreach (var t in tasks)
                {
                    if (t == null) continue;
                    arr.Add(MobileTaskJson(t));
                    if (t.done) done++; else open++;
                }
                if (AiPlanOn() && pushPlan != null && pushPlan.items != null)
                {
                    planSummary = pushPlan.summary == null ? "" : pushPlan.summary;
                    long now = TaskItem.Now();
                    foreach (var it in pushPlan.items)
                    {
                        if (it == null || it.fired || it.at <= now - 60000) continue;
                        var x = new Dictionary<string, object>();
                        x["at"] = it.at;
                        x["kind"] = it.kind == null ? "nudge" : it.kind;
                        x["taskId"] = it.taskId == null ? "" : it.taskId;
                        x["message"] = it.message == null ? "" : it.message;
                        x["level"] = it.level;
                        planArr.Add(x);
                    }
                }
            });
            var d = new Dictionary<string, object>();
            d["ok"] = true;
            d["rev"] = mobileRev;
            d["now"] = TaskItem.Now();
            d["openCount"] = open;
            d["doneCount"] = done;
            d["tasks"] = arr;
            d["plan"] = planArr;
            d["planSummary"] = planSummary;
            long apkSize;
            string apkVer;
            string apkPath = MobileApkFile(out apkSize, out apkVer);
            d["apkVersion"] = apkPath == null ? "" : apkVer;
            d["apkSize"] = apkSize;
            d["apkUrl"] = apkPath == null ? "" : "/apk";
            return Json.Serialize(d);
        }

        static string MobileAdd(string body)
        {
            try
            {
                var d = Json.Deserialize<Dictionary<string, object>>(body);
                string title = MobileStr(d, "title").Trim();
                if (title.Length == 0) return MobileError("标题不能为空");
                if (title.Length > 200) title = title.Substring(0, 200);
                string pri = NormalizePriority(MobileStr(d, "priority"));
                string kind = MobileStr(d, "kind");
                if (kind.Length == 0) kind = TaskKind.Guess(title);
                long dueMs = MobileLong(d, "due", 0);
                int est = (int)MobileLong(d, "estimateMin", 0);
                TaskItem created = null;
                MobileOnUi(delegate()
                {
                    var t = new TaskItem();
                    t.title = title;
                    t.priority = pri;
                    t.kind = kind;
                    t.due = dueMs > 0 ? (long?)dueMs : null;
                    t.reminders = BuildReminders(t.due, t.priority);
                    t.estimateMin = est > 0 ? est : HabitStats.Compute(tasks, DateTime.Now).SuggestEstimate(t.priority, t.kind);
                    t.updatedAt = TaskItem.Now();
                    tasks.Add(t);
                    SaveTasks();
                    SchedulePreciseReminder(t);
                    UpdateSummary();
                    if (mainWin != null && mainWin.IsVisible) RefreshMain();
                    created = t;
                    mobileRev = TaskItem.Now();
                });
                var r = new Dictionary<string, object>();
                r["ok"] = true;
                r["task"] = MobileTaskJson(created);
                return Json.Serialize(r);
            }
            catch (Exception ex) { return MobileError("新增失败: " + ex.Message); }
        }

        static string MobileUpdate(string body)
        {
            try
            {
                var d = Json.Deserialize<Dictionary<string, object>>(body);
                string id = MobileStr(d, "id");
                string err = "";
                MobileOnUi(delegate()
                {
                    var t = MobileFind(id);
                    if (t == null) { err = "任务不存在"; return; }
                    bool dueChanged = false;
                    if (d.ContainsKey("title"))
                    {
                        string title = MobileStr(d, "title").Trim();
                        if (title.Length > 0) t.title = title.Length > 200 ? title.Substring(0, 200) : title;
                    }
                    if (d.ContainsKey("priority"))
                    {
                        string p = NormalizePriority(MobileStr(d, "priority"));
                        if (p != t.priority) { t.priority = p; dueChanged = true; }
                    }
                    if (d.ContainsKey("kind")) t.kind = MobileStr(d, "kind");
                    if (d.ContainsKey("estimateMin"))
                    {
                        int e = (int)MobileLong(d, "estimateMin", t.estimateMin);
                        if (e > 0 && e <= 480) t.estimateMin = e;
                    }
                    if (d.ContainsKey("due"))
                    {
                        long dueMs = MobileLong(d, "due", 0);
                        long? nd = dueMs > 0 ? (long?)dueMs : null;
                        if (t.due != nd) { t.due = nd; dueChanged = true; }
                    }
                    if (dueChanged) t.reminders = BuildReminders(t.due, t.priority);
                    t.updatedAt = TaskItem.Now();
                    SaveTasks();
                    UpdateSummary();
                    if (mainWin != null && mainWin.IsVisible) RefreshMain();
                });
                if (err.Length > 0) return MobileError(err);
                var r = new Dictionary<string, object>();
                r["ok"] = true;
                return Json.Serialize(r);
            }
            catch (Exception ex) { return MobileError("更新失败: " + ex.Message); }
        }

        static string MobileAction(string body)
        {
            try
            {
                var d = Json.Deserialize<Dictionary<string, object>>(body);
                string id = MobileStr(d, "id");
                string action = MobileStr(d, "action");
                int minutes = (int)MobileLong(d, "minutes", 30);
                if (minutes < 1) minutes = 1;
                if (minutes > 1440) minutes = 1440;
                string err = "";
                MobileOnUi(delegate()
                {
                    var t = MobileFind(id);
                    if (t == null) { err = "任务不存在"; return; }
                    if (action == "start") StartTask(t);
                    else if (action == "pause") { PauseTask(t); UpdateSummary(); if (mainWin != null && mainWin.IsVisible) RefreshMain(); }
                    else if (action == "done") FinishTask(t);
                    else if (action == "undo")
                    {
                        t.done = false;
                        t.doneAt = 0;
                        t.updatedAt = TaskItem.Now();
                        SaveTasks();
                        UpdateSummary();
                        if (mainWin != null && mainWin.IsVisible) RefreshMain();
                    }
                    else if (action == "delete")
                    {
                        if (t.startedAt > 0) t.startedAt = 0;
                        tasks.Remove(t);
                        SaveTasks();
                        UpdateSummary();
                        if (mainWin != null && mainWin.IsVisible) RefreshMain();
                    }
                    else if (action == "postpone") ApplyPostpone(t, TaskItem.Now() + minutes * 60000L);
                    else if (action == "snooze") SnoozeTask(t, minutes * 60000L);
                    else if (action == "note")
                    {
                        t.lastNote = MobileStr(d, "note").Trim();
                        t.resumeHint = "";
                        t.updatedAt = TaskItem.Now();
                        SaveTasks();
                        if (mainWin != null && mainWin.IsVisible) RefreshMain();
                    }
                    else err = "未知动作";
                    if (err.Length == 0) mobileRev = TaskItem.Now();
                });
                if (err.Length > 0) return MobileError(err);
                var r = new Dictionary<string, object>();
                r["ok"] = true;
                return Json.Serialize(r);
            }
            catch (Exception ex) { return MobileError("动作失败: " + ex.Message); }
        }

        static string MobileAi(string body)
        {
            try
            {
                var d = Json.Deserialize<Dictionary<string, object>>(body);
                string kind = MobileStr(d, "kind");
                string id = MobileStr(d, "id");
                if (kind == "split")
                {
                    string title = "", pri = "mid", due = "", note = "", hist = "", load = "";
                    int est = 0;
                    MobileOnUi(delegate()
                    {
                        var t = MobileFind(id);
                        if (t == null) return;
                        title = t.title;
                        pri = PriorityName(t.priority);
                        due = t.due.HasValue
                            ? DateTimeOffset.FromUnixTimeMilliseconds(t.due.Value).LocalDateTime.ToString("M月d日 HH:mm", Inv)
                            : "无截止";
                        est = t.estimateMin;
                        note = string.IsNullOrEmpty(t.lastNote) ? "（没记）" : t.lastNote;
                        hist = HabitBrief();
                        load = TodayLoadBrief();
                    });
                    if (title.Length == 0) return MobileError("任务不存在");
                    string user = "任务：" + title + "\n重要度：" + pri + "\n截止：" + due
                                + "\n预计用时：" + est + " 分钟\n上次做到：" + note
                                + "\n当前负担：" + load + "\n用户历史：" + hist;
                    string err;
                    string raw = AiChat(SPLIT_SYSTEM, user, out err);
                    if (string.IsNullOrEmpty(raw)) return MobileError(err == null ? "AI 无返回" : err);
                    string perr;
                    var subs = ParseDecompose(raw, out perr);
                    var steps = new List<object>();
                    foreach (var s in subs)
                    {
                        var x = new Dictionary<string, object>();
                        x["title"] = s.title;
                        x["priority"] = s.priority;
                        x["delayMinutes"] = s.minutes;
                        x["estimateMinutes"] = s.estimate;
                        x["kind"] = s.kind;
                        steps.Add(x);
                    }
                    var r = new Dictionary<string, object>();
                    r["ok"] = true;
                    r["steps"] = steps;
                    return Json.Serialize(r);
                }
                if (kind == "triage")
                {
                    string prompt = "";
                    MobileOnUi(delegate()
                    {
                        var hs = HabitStats.Compute(tasks, DateTime.Now);
                        prompt = BuildTriagePrompt(hs, RankOpenTasks(DateTime.Now, hs));
                    });
                    string err;
                    string raw = AiChat(TRIAGE_SYSTEM, prompt, out err);
                    if (string.IsNullOrEmpty(raw)) return MobileError(err == null ? "AI 无返回" : err);
                    var r = new Dictionary<string, object>();
                    r["ok"] = true;
                    r["text"] = raw.Trim();
                    return Json.Serialize(r);
                }
                if (kind == "resume")
                {
                    string title = "", note = "", hist = "", pri = "";
                    int est = 0;
                    MobileOnUi(delegate()
                    {
                        var t = MobileFind(id);
                        if (t == null) return;
                        title = t.title;
                        note = t.lastNote == null ? "" : t.lastNote;
                        pri = PriorityName(t.priority);
                        est = t.estimateMin;
                        hist = HabitBrief();
                    });
                    if (title.Length == 0 || note.Length == 0) return MobileError("这条任务没有备注");
                    string err;
                    string raw = AiChat(RESUME_SYSTEM,
                        "任务：" + title + "\n上次做到：" + note + "\n重要度：" + pri
                        + "\n预计用时：" + est + " 分钟\n用户历史：" + hist, out err);
                    if (string.IsNullOrEmpty(raw)) return MobileError(err == null ? "AI 无返回" : err);
                    var r = new Dictionary<string, object>();
                    r["ok"] = true;
                    r["text"] = Trim(raw.Trim().Replace("\r", " ").Replace("\n", " "), 80);
                    return Json.Serialize(r);
                }
                return MobileError("未知 AI 类型");
            }
            catch (Exception ex) { return MobileError("AI 失败: " + ex.Message); }
        }

        static string MobileStr(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null) return Convert.ToString(v);
            return "";
        }

        static long MobileLong(Dictionary<string, object> d, string key, long fallback)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null)
            {
                long n;
                if (long.TryParse(Convert.ToString(v), out n)) return n;
                double f;
                if (double.TryParse(Convert.ToString(v), out f)) return (long)f;
            }
            return fallback;
        }
    }
}
