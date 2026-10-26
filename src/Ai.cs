// v0.4：DeepSeek 接入（一句话拆解任务 / 按重要度分级的提醒文案 / 稍后提醒智能决策）
// 全部走可选开关：没填 API Key 时自动退回本地规则，不会影响记任务和提醒。

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AdhdLite
{
    // AI 拆出来的子任务
    public class AiSubTask
    {
        public string title = "";
        public string priority = "mid";
        public int minutes = 30;
        public int estimate = 0;   // v0.5：预计用时（分钟）
        public string kind = "other";
    }

    public static partial class App
    {
        // ------------------------------------------------------------ 基础
        static string AiKey() { return (settings.aiKey ?? "").Trim(); }
        static bool AiReady() { return AiKey().Length > 0; }

        static string AiBaseUrl()
        {
            string b = (settings.aiBase ?? "").Trim();
            if (b.Length == 0) b = "https://api.deepseek.com";
            if (b.EndsWith("/")) b = b.Substring(0, b.Length - 1);
            return b;
        }

        static string AiModelName()
        {
            string m = (settings.aiModel ?? "").Trim();
            return m.Length == 0 ? "deepseek-flash" : m;
        }

        static bool AiDecomposeOn() { return settings.aiDecompose != 0; }
        static bool AiSnoozeOn() { return settings.aiSnooze != 0; }

        // 把"这个人的真实习惯"压成一段短提示，喂给所有 AI 调用。
        // 这是反馈闭环真正闭合的地方：复盘结论 + 计时统计会改变下一次拆解和选择。
        static string HabitBrief()
        {
            var hs = HabitStats.Compute(tasks, DateTime.Now);
            string s = hs.SummaryForAi();
            string est = hs.EstimateNote();
            if (!string.IsNullOrEmpty(est)) s += " " + est + "。";
            if (!string.IsNullOrEmpty(settings.lastInsight)) s += "\n上次复盘结论：" + settings.lastInsight;
            if (!string.IsNullOrEmpty(settings.rules)) s += "\n个人规则：" + settings.rules.Replace("\n", "；");
            return s;
        }

        static string TodayLoadBrief()
        {
            var hs = HabitStats.Compute(tasks, DateTime.Now);
            return "清单里还有 " + hs.openCount + " 条未完成（" + hs.overdueCount + " 条已逾期）。";
        }

        // ------------------------------------------------------------ 调用
        // 同步调用 DeepSeek /chat/completions（调用方负责放到后台线程里跑）
        static string AiChat(string system, string user, out string err)
        {
            err = null;
            if (!AiReady()) { err = "没填 API Key"; return null; }
            try
            {
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }

                string url = AiBaseUrl() + "/chat/completions";
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Timeout = 25000;
                req.ReadWriteTimeout = 25000;
                req.Headers.Add("Authorization", "Bearer " + AiKey());

                var msgs = new List<object>();
                var m1 = new Dictionary<string, object>();
                m1["role"] = "system";
                m1["content"] = system;
                msgs.Add(m1);
                var m2 = new Dictionary<string, object>();
                m2["role"] = "user";
                m2["content"] = user;
                msgs.Add(m2);

                var body = new Dictionary<string, object>();
                body["model"] = AiModelName();
                body["temperature"] = 0.3;
                body["max_tokens"] = 800;
                body["messages"] = msgs;

                byte[] bytes = Encoding.UTF8.GetBytes(Json.Serialize(body));
                req.ContentLength = bytes.Length;
                using (var s = req.GetRequestStream()) { s.Write(bytes, 0, bytes.Length); }

                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var rs = resp.GetResponseStream())
                using (var sr = new StreamReader(rs, Encoding.UTF8))
                {
                    string raw = sr.ReadToEnd();
                    return ExtractContent(raw, out err);
                }
            }
            catch (WebException wex)
            {
                err = "网络/接口错误: " + wex.Message;
                try
                {
                    if (wex.Response != null)
                    {
                        using (var rs = wex.Response.GetResponseStream())
                        using (var sr = new StreamReader(rs, Encoding.UTF8))
                            err += "  " + Trim(sr.ReadToEnd(), 200);
                    }
                }
                catch { }
                return null;
            }
            catch (Exception ex) { err = ex.Message; return null; }
        }

        // JavaScriptSerializer 把嵌套数组给成 object[] 或 ArrayList，两种都收
        static object[] AsArray(object o)
        {
            if (o == null) return null;
            var a1 = o as object[];
            if (a1 != null) return a1;
            var al = o as System.Collections.ArrayList;
            if (al != null) return al.ToArray();
            var en = o as System.Collections.IEnumerable;
            if (en != null)
            {
                var list = new List<object>();
                foreach (var x in en) list.Add(x);
                return list.ToArray();
            }
            return null;
        }

        static string Trim(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }

        // 从 {"choices":[{"message":{"content":"..."}}]} 里取正文
        static string ExtractContent(string raw, out string err)
        {
            err = null;
            if (string.IsNullOrEmpty(raw)) { err = "接口返回为空"; return null; }
            try
            {
                var root = Json.Deserialize<Dictionary<string, object>>(raw);
                if (root == null) { err = "返回不是 JSON"; return null; }

                object chosen;
                if (!root.TryGetValue("choices", out chosen) || chosen == null)
                {
                    object eobj;
                    if (root.TryGetValue("error", out eobj))
                    {
                        var ed = eobj as Dictionary<string, object>;
                        err = ed != null && ed.ContainsKey("message") ? ("接口报错: " + ed["message"]) : "接口报错";
                    }
                    else err = "返回里没有 choices";
                    return null;
                }
                var arr = AsArray(chosen);
                if (arr == null || arr.Length == 0) { err = "choices 为空"; return null; }
                var first = arr[0] as Dictionary<string, object>;
                if (first == null) { err = "choices[0] 解析失败"; return null; }
                object mobj;
                if (!first.TryGetValue("message", out mobj) || mobj == null) { err = "没有 message"; return null; }
                var msg = mobj as Dictionary<string, object>;
                if (msg == null || !msg.ContainsKey("content")) { err = "没有 content"; return null; }
                return Convert.ToString(msg["content"]);
            }
            catch (Exception ex) { err = "解析返回失败: " + ex.Message; return null; }
        }

        // ------------------------------------------------------------ 拆解
        const string DECOMPOSE_SYSTEM =
            "你是任务拆解助手。把用户的一句话拆成 1-4 个可独立完成的子任务。" +
            "只输出严格的 JSON 数组，不要任何解释、不要代码块标记。格式：" +
            "[{\"title\":\"子任务标题\",\"priority\":\"high|mid|low\",\"delayMinutes\":30,\"estimateMinutes\":25,\"kind\":\"writing|code|comms|errand|study|other\"}]" +
            "。delayMinutes 是从现在起多少分钟后截止（整数，1-10080）；" +
            "estimateMinutes 是这个子任务预计要做多少分钟（整数，5-480），参考用户历史，不要给过于乐观的估计；" +
            "kind 是任务类型（写作/代码/沟通/杂事/学习/其他），按内容选；" +
            "priority 按紧急程度给出 high/mid/low。" +
            "用户历史只用于校准时长和优先级，不要照抄。";

        static List<AiSubTask> ParseDecompose(string raw, out string err)
        {
            err = null;
            var list = new List<AiSubTask>();
            if (string.IsNullOrEmpty(raw)) { err = "AI 返回为空"; return list; }

            string s = raw.Trim();
            int a = s.IndexOf('[');
            int b = s.LastIndexOf(']');
            if (a < 0 || b <= a) { err = "AI 返回的不是 JSON 数组"; return list; }
            s = s.Substring(a, b - a + 1);

            try
            {
                var arr = AsArray(Json.Deserialize<object>(s));
                if (arr == null) { err = "解析成空数组"; return list; }
                foreach (var o in arr)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    var st = new AiSubTask();
                    st.title = StrOf(d, "title");
                    if (st.title.Length == 0) st.title = StrOf(d, "任务");
                    st.priority = NormalizePriority(StrOf(d, "priority"));
                    st.minutes = IntOf(d, "delayMinutes", 30);
                    if (st.minutes < 1) st.minutes = 1;
                    if (st.minutes > 10080) st.minutes = 10080;
                    st.estimate = IntOf(d, "estimateMinutes", 0);
                    if (st.estimate < 0) st.estimate = 0;
                    if (st.estimate > 480) st.estimate = 480;
                    st.kind = StrOf(d, "kind");
                    if (st.kind.Length == 0) st.kind = TaskKind.Guess(st.title);
                    if (st.title.Length > 0) list.Add(st);
                }
            }
            catch (Exception ex) { err = "解析失败: " + ex.Message; }
            return list;
        }

        static string StrOf(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null) return Convert.ToString(v).Trim();
            return "";
        }

        static int IntOf(Dictionary<string, object> d, string key, int fallback)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null)
            {
                int n;
                if (int.TryParse(Convert.ToString(v).Trim(), out n)) return n;
                double f;
                if (double.TryParse(Convert.ToString(v).Trim(), out f)) return (int)Math.Round(f);
            }
            return fallback;
        }

        static string NormalizePriority(string p)
        {
            if (string.IsNullOrEmpty(p)) return "mid";
            p = p.Trim().ToLower();
            if (p == "high" || p == "高" || p == "h" || p == "重要") return "high";
            if (p == "low" || p == "低" || p == "l") return "low";
            return "mid";
        }

        // ------------------------------------------------------------ 本地规则（没 Key 也能用）
        // 稍后提醒建议时长：重要度 + 任务计划跨度 + 当前时段
        static int LocalSnoozeMinutes(TaskItem t)
        {
            int baseMin = 15;
            if (t != null)
            {
                if (t.priority == "high") baseMin = 10;
                else if (t.priority == "low") baseMin = 30;

                if (t.due.HasValue)
                {
                    long span = t.due.Value - t.createdAt;
                    if (span > 0 && span < 5 * 60 * 1000) baseMin = 5;   // 本来就是个 5 分钟内的短任务
                }
                // 如果你这类任务历史上平均超时 40% 以上，10 分钟明显不够，往后放一点
                var hs = HabitStats.Compute(tasks, DateTime.Now);
                int pi = HabitStats.Pi(t.priority);
                if (hs.estCount[pi] >= 2 && hs.OverrunOf(pi) > 0.4) baseMin += 5;
            }
            int hour = DateTime.Now.Hour;
            if (hour >= 23 || hour < 7) baseMin = Math.Max(baseMin, 60);  // 半夜别 10 分钟一弹
            if (baseMin < 5) baseMin = 5;
            if (baseMin > 120) baseMin = 120;
            return baseMin;
        }

        // 按重要度给不同措辞（提醒窗里那行小字）
        static string ReminderTip(TaskItem t, string label)
        {
            bool overdue = t != null && t.due.HasValue && t.due.Value < TaskItem.Now();
            if (overdue) return "已经过期了：现在做掉，或者直接改期";
            string p = t == null ? "mid" : t.priority;
            if (p == "high") return "这条很重要：先做 5 分钟，开了头就不难";
            if (p == "low") return "顺手做掉，别让它继续占脑子";
            return "有空档就往前推一点，不用一次做完";
        }

        // ------------------------------------------------------------ 拆解流程
        static void StartDecompose()
        {
            if (!AiReady())
            {
                ShowHint("先用悬浮球右键 → 设置 → 填 DeepSeek API Key");
                return;
            }
            string sentence = (titleBox.Text ?? "").Trim();
            if (sentence.Length == 0)
            {
                ShowHint("先把要做的事写一句话，再点拆解");
                titleBox.Focus();
                return;
            }

            ShowHint("AI 正在拆解…");
            var ui = new List<Button>();
            string usr = sentence + "\n\n【用户历史】" + HabitBrief() + "\n【当前负担】" + TodayLoadBrief();
            var th = new Thread(delegate()
            {
                string err;
                string raw = AiChat(DECOMPOSE_SYSTEM, usr, out err);
                List<AiSubTask> parsed = ParseDecompose(raw, out err);
                Application.Current.Dispatcher.BeginInvoke((Action)delegate
                {
                    try
                    {
                        if (parsed.Count == 0)
                        {
                            ShowHint("拆解失败：" + (err == null ? "没有可用子任务" : err));
                            return;
                        }
                        ShowDecomposeResult(sentence, parsed);
                    }
                    catch (Exception ex) { StartupLog("拆解结果窗失败: " + ex.Message); }
                });
            });
            th.IsBackground = true;
            th.Start();
        }

        // 拆解结果：勾选后批量加入；replace != null 表示"拆小并替换原任务"
        static void ShowDecomposeResult(string source, List<AiSubTask> items, TaskItem replace = null)
        {
            var w = new Window();
            w.WindowStyle = WindowStyle.None;
            w.AllowsTransparency = true;
            w.Background = Brushes.Transparent;
            w.Topmost = true;
            w.ShowInTaskbar = false;
            w.SizeToContent = SizeToContent.WidthAndHeight;
            w.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var card = new Border();
            card.CornerRadius = new CornerRadius(14);
            card.Background = B(CardBg);
            card.BorderBrush = B(LineCol);
            card.BorderThickness = new Thickness(1);
            card.Padding = new Thickness(16, 14, 16, 14);

            var root = new StackPanel();
            card.Child = root;

            var head = new TextBlock();
            head.Text = replace == null ? ("AI 拆成 " + items.Count + " 步")
                                        : ("AI 把这条拆成 " + items.Count + " 步");
            head.FontSize = 13;
            head.FontWeight = FontWeights.SemiBold;
            head.Foreground = B(TextMain);
            root.Children.Add(head);

            var src = new TextBlock();
            src.Text = Trim(source, 60);
            src.FontSize = 11;
            src.Foreground = B(TextMuted);
            src.Margin = new Thickness(0, 3, 0, 10);
            src.TextTrimming = TextTrimming.CharacterEllipsis;
            root.Children.Add(src);

            var checks = new List<CheckBox>();
            foreach (var it in items)
            {
                var row = new StackPanel();
                row.Orientation = Orientation.Horizontal;
                row.Margin = new Thickness(0, 2, 0, 4);

                var cb = new CheckBox();
                cb.IsChecked = true;
                cb.VerticalAlignment = VerticalAlignment.Center;
                cb.Margin = new Thickness(0, 0, 8, 0);
                checks.Add(cb);
                row.Children.Add(cb);

                var t = new TextBlock();
                t.Text = it.title;
                t.FontSize = 12.5;
                t.Foreground = B(TextMain);
                t.VerticalAlignment = VerticalAlignment.Center;
                t.MaxWidth = 300;
                t.TextTrimming = TextTrimming.CharacterEllipsis;
                row.Children.Add(t);

                var meta = new TextBlock();
                meta.Text = "  " + TaskKind.Name(it.kind) + " · "
                          + (it.priority == "high" ? "高" : it.priority == "low" ? "低" : "中")
                          + " · " + it.minutes + " 分钟后"
                          + (it.estimate > 0 ? " · 预计 " + it.estimate + " 分" : "");
                meta.FontSize = 11;
                meta.Foreground = B(TextMuted);
                meta.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(meta);

                root.Children.Add(row);
            }

            var bar = new StackPanel();
            bar.Orientation = Orientation.Horizontal;
            bar.HorizontalAlignment = HorizontalAlignment.Right;
            bar.Margin = new Thickness(0, 12, 0, 0);

            var cancel = TinyButton("取消", TextSub);
            cancel.VerticalAlignment = VerticalAlignment.Center;
            cancel.Click += delegate { try { w.Close(); } catch { } };
            bar.Children.Add(cancel);

            var ok = FilledButton(replace == null ? "全部加入" : "替换原任务");
            ok.Click += delegate
            {
                int added = 0;
                var hsNow = HabitStats.Compute(tasks, DateTime.Now);
                for (int i = 0; i < items.Count; i++)
                {
                    if (checks[i].IsChecked != true) continue;
                    var task = new TaskItem();
                    task.title = items[i].title;
                    task.priority = items[i].priority;
                    task.kind = string.IsNullOrEmpty(items[i].kind) ? TaskKind.Guess(items[i].title) : items[i].kind;
                    task.due = TaskItem.Now() + items[i].minutes * 60L * 1000;
                    task.reminders = BuildReminders(task.due, task.priority);
                    task.estimateMin = items[i].estimate > 0 ? items[i].estimate : hsNow.SuggestEstimate(task.priority, task.kind);
                    tasks.Add(task);
                    SchedulePreciseReminder(task);
                    added++;
                }
                if (added > 0)
                {
                    if (replace != null)
                    {
                        if (replace.startedAt > 0) replace.startedAt = 0;
                        tasks.Remove(replace);
                        ShowToast("已拆成 " + added + " 步", "原任务已替换，先做第一步");
                    }
                    SaveTasks();
                    UpdateSummary();
                    try { if (mainWin != null && mainWin.IsVisible) RefreshMain(); } catch { }
                    titleBox.Text = "";
                    popup.Hide();
                }
                try { w.Close(); } catch { }
            };
            bar.Children.Add(ok);
            root.Children.Add(bar);

            w.Content = card;
            w.Show();
            w.Activate();
        }

        // ------------------------------------------------------------ 设置页：测试连接
        static void StartAiPing(TextBlock status)
        {
            var th = new Thread(delegate()
            {
                string err;
                string raw = AiChat("你是连通性测试助手。", "只回复两个字：可以", out err);
                Application.Current.Dispatcher.BeginInvoke((Action)delegate
                {
                    try
                    {
                        if (status == null) return;
                        if (raw != null)
                        {
                            status.Foreground = B(TextSub);
                            status.Text = "连接成功 · 模型 " + AiModelName() + " · 返回：" + Trim(raw, 40);
                        }
                        else
                        {
                            status.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C47B6D"));
                            status.Text = "连接失败：" + (err == null ? "未知错误" : err);
                        }
                    }
                    catch { }
                });
            });
            th.IsBackground = true;
            th.Start();
        }

        // ------------------------------------------------------------ 稍后时长：AI 建议（可选）
        static void AskAiSnoozeSuggestion(TaskItem t)
        {
            if (t == null || !AiSnoozeOn() || !AiReady()) return;
            string title = t.title;
            string pri = t.priority;
            string now = DateTime.Now.ToString("HH:mm", Inv);
            string sys = "你是时间管理助手。回答只用一行：一个 5-120 之间的整数，表示" + "建议多少分钟后再提醒" + "，不要任何其它文字。";
            string usr = "任务：" + title + "\n重要度：" + pri + "\n现在：" + now
                       + "\n背景：用户刚被提醒，想稍后再做。\n用户历史：" + HabitBrief();
            var th = new Thread(delegate()
            {
                string err;
                string raw = AiChat(sys, usr, out err);
                int mins = 0;
                if (raw != null)
                {
                    var mm = System.Text.RegularExpressions.Regex.Match(raw, "\\d+");
                    if (mm.Success) int.TryParse(mm.Value, out mins);
                }
                if (mins < 5) mins = 5;
                if (mins > 120) mins = 120;
                Application.Current.Dispatcher.BeginInvoke((Action)delegate
                {
                    try
                    {
                        if (current != t) return;      // 提醒窗已经换任务了
                        snoozeMinutes = mins;
                        var b = reminderWindow.FindName("BtnSnooze") as Button;
                        if (b != null) b.Content = "稍后 " + mins + " 分钟（AI）";
                        StartupLog("AI 建议稍后 " + mins + " 分钟");
                    }
                    catch { }
                });
            });
            th.IsBackground = true;
            th.Start();
        }

        // ------------------------------------------------------------ AI 帮我选（结合习惯）
        const string PICK_SYSTEM =
            "你是 ADHD 执行教练。根据候选任务和用户的真实习惯，选出现在最该开始的一个。" +
            "只输出严格 JSON，不要解释、不要代码块标记。格式：" +
            "{\"index\":0,\"reason\":\"不超过30字\",\"firstStep\":\"不超过30字\"}。" +
            "reason 要说出为什么是它（逾期/截止/重要度/历史习惯至少占一条），" +
            "firstStep 必须是一个 2 分钟内能开始的具体动作，不要写“专注一点”这种空话。";

        static void StartAiPick()
        {
            if (!AiReady())
            {
                ShowHint("先填 API Key 才能让 AI 选");
                return;
            }
            if (aiPickBusy) return;

            var ranked = RankOpenTasks(DateTime.Now, HabitStats.Compute(tasks, DateTime.Now));
            if (ranked.Count == 0) return;
            var cands = new List<TaskPick>();
            for (int i = 0; i < ranked.Count && i < 6; i++) cands.Add(ranked[i]);

            aiPickBusy = true;
            aiPickTaskId = "";
            aiPickReason = "";
            aiPickStep = "";
            if (mainWin != null && mainWin.IsVisible) RefreshMain();

            string user = BuildPickPrompt(cands);
            var th = new Thread(delegate()
            {
                string err;
                string raw = AiChat(PICK_SYSTEM, user, out err);
                int idx = -1;
                string reason = null, step = null;
                if (!string.IsNullOrEmpty(raw)) ParsePick(raw, out idx, out reason, out step);
                Application.Current.Dispatcher.BeginInvoke((Action)delegate
                {
                    aiPickBusy = false;
                    if (idx >= 0 && idx < cands.Count && !cands[idx].task.done)
                    {
                        aiPickTaskId = cands[idx].task.id;
                        aiPickReason = string.IsNullOrEmpty(reason) ? cands[idx].reason : Trim(reason, 40);
                        aiPickStep = step == null ? "" : Trim(step, 40);
                        StartupLog("AI 选任务: " + cands[idx].task.title + " / " + aiPickReason);
                    }
                    else
                    {
                        ShowToast("AI 没选出来", "先用本地规则；" + (err == null ? "返回格式不对" : err));
                    }
                    if (mainWin != null && mainWin.IsVisible) RefreshMain();
                });
            });
            th.IsBackground = true;
            th.Start();
        }

        static string BuildPickPrompt(List<TaskPick> cands)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("现在：").Append(DateTime.Now.ToString("M月d日 dddd HH:mm")).Append("\n");
            sb.Append("用户历史：").Append(HabitBrief()).Append("\n");
            sb.Append("候选（index 从 0 开始）：\n");
            for (int i = 0; i < cands.Count; i++)
            {
                var t = cands[i].task;
                string due = t.due.HasValue
                    ? DateTimeOffset.FromUnixTimeMilliseconds(t.due.Value).LocalDateTime.ToString("M-d HH:mm", Inv)
                    : "无";
                sb.Append("- [").Append(i).Append("] ").Append(Trim(t.title, 50))
                  .Append("（").Append(PriorityName(t.priority)).Append("，").Append(TaskKind.Name(GuessKindOf(t))).Append("，截止 ").Append(due)
                  .Append("，预计 ").Append(t.estimateMin > 0 ? t.estimateMin.ToString() : "?")
                  .Append(" 分，推迟 ").Append(t.snoozeCount + t.postponeCount).Append(" 次）\n");
            }
            sb.Append("\n只选一个。输出 JSON：{\"index\":0,\"reason\":\"...\",\"firstStep\":\"...\"}");
            return sb.ToString();
        }

        static void ParsePick(string raw, out int idx, out string reason, out string step)
        {
            idx = -1;
            reason = "";
            step = "";
            try
            {
                int a = raw.IndexOf('{');
                int b = raw.LastIndexOf('}');
                if (a < 0 || b <= a) return;
                var d = Json.Deserialize<Dictionary<string, object>>(raw.Substring(a, b - a + 1));
                if (d == null) return;
                object v;
                if (d.TryGetValue("index", out v) && v != null) idx = Convert.ToInt32(v);
                reason = StrOf(d, "reason");
                step = StrOf(d, "firstStep");
                if (step.Length == 0) step = StrOf(d, "first_step");
            }
            catch { }
        }

        // ------------------------------------------------------------ 卡住了：给一个 2 分钟的动作
        const string UNSTUCK_SYSTEM =
            "你是 ADHD 执行教练。用户卡在任务上。只回复一句话，不超过 30 个字，" +
            "必须是一个 2 分钟内就能开始的具体动作（例如“先打开文件，只写标题”）。" +
            "不要解释、不要鼓励、不要列步骤。";

        static void AskAiUnstuck(TaskItem t)
        {
            if (t == null || !AiReady()) return;
            long elapsed = ActiveElapsedMs();
            string user = "任务：" + t.title
                        + "\n已做：" + (elapsed / 60000) + " 分钟；预计：" + t.estimateMin + " 分钟"
                        + "\n上次做到：" + (string.IsNullOrEmpty(t.lastNote) ? "（没记）" : t.lastNote)
                        + "\n用户历史：" + HabitBrief();

            aiNudgeTaskId = t.id;
            aiNudgeText = "AI 正在想一个 2 分钟的动作…";
            if (mainWin != null && mainWin.IsVisible) RefreshMain();

            var th = new Thread(delegate()
            {
                string err;
                string raw = AiChat(UNSTUCK_SYSTEM, user, out err);
                string one = "";
                if (!string.IsNullOrEmpty(raw))
                {
                    one = raw.Trim().Replace("\r", " ").Replace("\n", " ");
                    one = Trim(one.Trim('“', '”', '"', ' '), 60);
                }
                Application.Current.Dispatcher.BeginInvoke((Action)delegate
                {
                    if (aiNudgeTaskId != t.id) return;
                    aiNudgeText = one.Length > 0 ? one : "AI 没回应，先做 2 分钟再说";
                    StartupLog("卡住求救: " + t.title + " -> " + aiNudgeText);
                    if (mainWin != null && mainWin.IsVisible) RefreshMain();
                });
            });
            th.IsBackground = true;
            th.Start();
        }

        // ------------------------------------------------------------ AI 复盘（会写回 settings，影响之后所有提示词）
        static bool reviewBusy = false;
        static string reviewError = "";

        const string REVIEW_SYSTEM =
            "你是 ADHD 时间管理教练。根据真实记录说话，具体、克制、不鸡汤。" +
            "只输出 2-3 句中文：一句指出可验证的模式，一句给出下次能执行的小调整。" +
            "最后另起一行输出一条可长期执行的个人规则，格式严格为：规则：<不超过 20 字>。" +
            "不要列清单、不要重复数字、不要鼓励式空话。";

        static void StartWeeklyReview(HabitStats hs)
        {
            if (!AiReady())
            {
                reviewError = "先在设置里填 DeepSeek API Key";
                RefreshMain();
                return;
            }
            if (reviewBusy) return;
            reviewBusy = true;
            reviewError = "";
            RefreshMain();

            string user = BuildReviewPrompt(hs);
            var th = new Thread(delegate()
            {
                string err;
                string raw = AiChat(REVIEW_SYSTEM, user, out err);
                Application.Current.Dispatcher.BeginInvoke((Action)delegate
                {
                    reviewBusy = false;
                    if (!string.IsNullOrEmpty(raw))
                    {
                        string txt, rule;
                        SplitRuleFromReview(raw.Trim(), out txt, out rule);
                        if (string.IsNullOrEmpty(txt)) txt = raw.Trim();
                        if (txt.Length > 500) txt = txt.Substring(0, 500);
                        settings.lastInsight = txt;
                        settings.insightAt = TaskItem.Now();
                        if (!string.IsNullOrEmpty(rule)) AddRule(rule);
                        SaveSettings();
                        ShowToast("复盘已更新", string.IsNullOrEmpty(rule)
                            ? "之后的选任务 / 拆解 / 稍后都会参考这条"
                            : "记住一条规则：" + rule);
                        StartupLog("AI 复盘: " + txt + (string.IsNullOrEmpty(rule) ? "" : " / 规则: " + rule));
                    }
                    else
                    {
                        reviewError = "AI 复盘失败：" + (err == null ? "返回为空" : err);
                    }
                    RefreshMain();
                });
            });
            th.IsBackground = true;
            th.Start();
        }

        static string BuildReviewPrompt(HabitStats hs)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("现在：").Append(DateTime.Now.ToString("M月d日 dddd HH:mm")).Append("\n");
            sb.Append("用户历史：").Append(hs.SummaryForAi()).Append("\n");
            if (!string.IsNullOrEmpty(settings.rules))
                sb.Append("已记住的个人规则：").Append(settings.rules.Replace("\n", "；")).Append("\n");
            sb.Append("时段分布：").Append(hs.TimeLine()).Append("\n");
            sb.Append("最近完成（标题 | 预计 | 实际 | 截止）：\n");

            var recent = new List<TaskItem>();
            foreach (var t in tasks) if (t != null && t.done && t.doneAt > 0) recent.Add(t);
            recent.Sort(delegate(TaskItem a, TaskItem b) { return b.doneAt.CompareTo(a.doneAt); });
            int n = 0;
            foreach (var t in recent)
            {
                if (n >= 12) break;
                n++;
                sb.Append("- ").Append(Trim(t.title, 40)).Append(" | ")
                  .Append(t.estimateMin > 0 ? (t.estimateMin + " 分") : "未估").Append(" | ")
                  .Append(t.spentMs > 0 ? (t.spentMs / 60000 + " 分") : "未计时");
                if (t.due.HasValue)
                {
                    sb.Append(" | ");
                    if (t.doneAt <= t.due.Value + 60000) sb.Append("按时");
                    else sb.Append("晚 ").Append((t.doneAt - t.due.Value) / 60000).Append(" 分");
                }
                sb.Append("\n");
            }
            sb.Append("\n请给出一个模式 + 一个下次能执行的小调整。");
            return sb.ToString();
        }

        // 只给开发用：--ai-smoke。会用真实 Key 各调一次（约 0.003 元），把原始返回写进 diagnostics.log
        static void SelfAiSmoke()
        {
            selftesting = true;
            var done = new TaskItem();
            done.title = "自检演示：改接口";
            done.priority = "high";
            done.estimateMin = 30;
            done.spentMs = 42 * 60000L;
            done.done = true;
            done.doneAt = TaskItem.Now() - 2 * 3600 * 1000;
            done.due = done.doneAt + 10 * 60000L;
            done.createdAt = done.doneAt - 86400000L;
            tasks.Add(done);

            var open = new TaskItem();
            open.title = "自检演示：把方案发给张三";
            open.priority = "high";
            open.due = TaskItem.Now() - 3600 * 1000;
            open.estimateMin = 30;
            tasks.Add(open);

            // 造出“今天排不下”的条件，让取舍 prompt 真的进入超载分支
            var big = new TaskItem();
            big.title = "自检演示：写季度总结";
            big.priority = "high";
            big.due = TaskItem.Now() + 3 * 3600 * 1000;
            big.estimateMin = 420;
            big.snoozeCount = 2;
            tasks.Add(big);

            var hs = HabitStats.Compute(tasks, DateTime.Now);
            var ranked = RankOpenTasks(DateTime.Now, hs);
            var cands = new List<TaskPick>();
            for (int i = 0; i < ranked.Count && i < 6; i++) cands.Add(ranked[i]);

            string pickUser = BuildPickPrompt(cands);
            string reviewUser = BuildReviewPrompt(hs);
            string unstuckUser = "任务：把方案发给张三\n已做：35 分钟；预计：30 分钟\n上次做到：改完一半\n用户历史：" + hs.SummaryForAi();
            string splitUser = "任务：写季度总结\n重要度：高\n截止：今天 18:00\n已推迟：2 次\n预计用时：240 分钟"
                             + "\n上次做到：打开文档，写了两行\n当前负担：" + TodayLoadBrief()
                             + "\n用户历史：" + HabitBrief();
            string triageUser = BuildTriagePrompt(hs, ranked);
            string resumeUser = "任务：写季度总结\n上次做到：写完第一部分，第二部分没有数据\n重要度：高\n预计用时：240 分钟"
                              + "\n用户历史：" + HabitBrief();

            var th = new Thread(delegate()
            {
                string e1, e2, e3;
                string r1 = AiChat(PICK_SYSTEM, pickUser, out e1);
                int idx = -1;
                string pr = "", ps = "";
                if (!string.IsNullOrEmpty(r1)) ParsePick(r1, out idx, out pr, out ps);
                Trace("AI-SMOKE-PICK " + (idx >= 0 ? "OK" : "FAIL") + " idx=" + idx
                      + " reason=" + pr + " step=" + ps + (e1 == null ? "" : (" err=" + e1))
                      + " raw=" + Trim(r1, 160));

                string r2 = AiChat(REVIEW_SYSTEM, reviewUser, out e2);
                Trace("AI-SMOKE-REVIEW " + (string.IsNullOrEmpty(r2) ? "FAIL " + e2 : "OK " + Trim(r2, 200)));

                string r3 = AiChat(UNSTUCK_SYSTEM, unstuckUser, out e3);
                Trace("AI-SMOKE-UNSTUCK " + (string.IsNullOrEmpty(r3) ? "FAIL " + e3 : "OK " + Trim(r3, 80)));

                string e4;
                string r4 = AiChat(SPLIT_SYSTEM, splitUser, out e4);
                string pe4;
                List<AiSubTask> smokeSubs = ParseDecompose(r4, out pe4);
                Trace("AI-SMOKE-SPLIT " + (smokeSubs.Count >= 2 ? "OK" : "FAIL")
                      + " n=" + smokeSubs.Count + " err=" + (e4 == null ? "" : e4)
                      + " parse=" + (pe4 == null ? "" : pe4) + " raw=" + Trim(r4, 300));

                string e5;
                string r5 = AiChat(TRIAGE_SYSTEM, triageUser, out e5);
                Trace("AI-SMOKE-TRIAGE " + (string.IsNullOrEmpty(r5) ? "FAIL " + e5 : "OK " + Trim(r5, 300)));

                string e6;
                string r6 = AiChat(RESUME_SYSTEM, resumeUser, out e6);
                Trace("AI-SMOKE-RESUME " + (string.IsNullOrEmpty(r6) ? "FAIL " + e6 : "OK " + Trim(r6, 80)));

                Application.Current.Dispatcher.BeginInvoke((Action)delegate { Quit("ai-smoke"); });
            });
            th.IsBackground = true;
            th.Start();
            application.Run();
        }
    }
}
