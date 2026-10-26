// v0.6：AI 推进计划。
// 1) 让 AI（或本地规则）决定"截止前在哪些时间点提醒、提醒什么"
// 2) 到点用「汇总弹窗」告诉用户还有哪些没做完
// 3) 到期后用「到期汇总」列出没完成的任务
// 4) 弹窗按重要度分成 高 / 中 / 低 三套视觉与按钮

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace AdhdLite
{
    public class PushItem
    {
        public long at { get; set; }
        public string kind { get; set; }     // nudge = 截止前；overdue = 到期后
        public string taskId { get; set; }   // 这条提醒聚焦的任务
        public string message { get; set; }  // 一句话（AI 生成或本地模板）
        public int level { get; set; }       // 0 高 / 1 中 / 2 低
        public bool fired { get; set; }

        public PushItem()
        {
            kind = "nudge";
            taskId = "";
            message = "";
        }
    }

    public class PushPlan
    {
        public long generatedAt { get; set; }
        public string signature { get; set; }
        public string source { get; set; }   // ai / local
        public string summary { get; set; }
        public List<PushItem> items { get; set; }

        public PushPlan()
        {
            signature = "";
            source = "local";
            summary = "";
            items = new List<PushItem>();
        }
    }

    public static partial class App
    {
        static PushPlan pushPlan;
        static bool planBusy = false;
        static long lastPlanAttempt = 0;
        static Window digestWindow;
        static PushItem digestItem;

        static bool AiPlanOn() { return settings.aiPlan != 0; }

        // ------------------------------------------------------------ 存取
        static void LoadPlan()
        {
            if (!AiPlanOn()) return;
            try
            {
                if (!File.Exists(planFile)) return;
                var p = Json.Deserialize<PushPlan>(File.ReadAllText(planFile, System.Text.Encoding.UTF8));
                if (p != null && p.items != null && p.items.Count > 0)
                {
                    pushPlan = p;
                    StartupLog("读取提醒计划: " + p.items.Count + " 条 / " + p.source + " / " + p.summary);
                }
            }
            catch (Exception ex) { StartupLog("读取提醒计划失败: " + ex.Message); }
        }

        static void SavePlan()
        {
            try
            {
                if (string.IsNullOrEmpty(planFile)) return;
                if (pushPlan == null)
                {
                    try { if (File.Exists(planFile)) File.Delete(planFile); } catch { }
                    return;
                }
                string tmp = planFile + ".tmp";
                File.WriteAllText(tmp, Json.Serialize(pushPlan), new System.Text.UTF8Encoding(false));
                try { if (File.Exists(planFile)) File.Delete(planFile); } catch { }
                File.Move(tmp, planFile);
            }
            catch (Exception ex) { StartupLog("保存提醒计划失败: " + ex.Message); }
        }

        // 任务一变（新增/完成/改期），计划就该重排；用稳定哈希比较，避免每次启动都重算
        static string PlanSignature()
        {
            var open = new List<TaskItem>();
            foreach (var t in tasks) if (t != null && !t.done) open.Add(t);
            open.Sort(delegate(TaskItem a, TaskItem b) { return string.CompareOrdinal(a.id, b.id); });
            var sb = new System.Text.StringBuilder();
            sb.Append(open.Count);
            foreach (var t in open)
            {
                sb.Append('|').Append(t.id);
                sb.Append(',').Append(t.due.HasValue ? t.due.Value.ToString() : "0");
                sb.Append(',').Append(t.priority);
                sb.Append(',').Append(t.estimateMin);
            }
            ulong h = 14695981039346656037UL;
            string s = sb.ToString();
            for (int i = 0; i < s.Length; i++)
            {
                h ^= s[i];
                h *= 1099511628211UL;
            }
            return h.ToString("x16");
        }

        static bool PlanActive()
        {
            if (!AiPlanOn() || pushPlan == null || pushPlan.items == null) return false;
            if (pushPlan.items.Count == 0) return false;
            if (pushPlan.generatedAt <= TaskItem.Now() - 24L * 3600 * 1000) return false;
            long now = TaskItem.Now();
            foreach (var it in pushPlan.items)
            {
                if (it != null && !it.fired && it.at > now - 60000) return true;
            }
            return false;
        }

        static bool QuietHour(long ms)
        {
            int h = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.Hour;
            return h >= 23 || h < 7;
        }

        static long ShiftOutOfQuiet(long at)
        {
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(at).LocalDateTime;
            if (dt.Hour >= 23) dt = dt.Date.AddDays(1).AddHours(7).AddMinutes(15);
            else if (dt.Hour < 7) dt = dt.Date.AddHours(7).AddMinutes(15);
            return new DateTimeOffset(dt).ToUnixTimeMilliseconds();
        }

        // 夜间例外：高优先 + 3 小时内截止，允许在截止前提醒一次（不然会错过半夜的 deadline）
        static bool UrgentHigh(TaskItem t, long now)
        {
            if (t == null || t.priority != "high" || !t.due.HasValue) return false;
            long left = t.due.Value - now;
            return left > 0 && left <= 3L * 3600 * 1000;
        }

        static string PlanWhenText(long at)
        {
            var d = DateTimeOffset.FromUnixTimeMilliseconds(at).LocalDateTime;
            if (d.Date == DateTime.Today) return d.ToString("HH:mm", Inv);
            if (d.Date == DateTime.Today.AddDays(1)) return "明天 " + d.ToString("HH:mm", Inv);
            return d.ToString("M-d HH:mm", Inv);
        }

        // 清单顶部显示的一行："AI 计划：… · 下次 14:30"
        static string PlanStatusText()
        {
            if (!AiPlanOn()) return "提醒：固定时间（设置里可开「AI 安排提醒」）";
            if (planBusy) return "AI 正在排提醒…";
            if (pushPlan == null) return AiReady() ? "还没排提醒" : "提醒：按固定时间";
            string s = (pushPlan.source == "ai" ? "AI 计划" : "本地计划");
            if (!string.IsNullOrEmpty(pushPlan.summary)) s += "：" + Trim(pushPlan.summary, 40);
            long now = TaskItem.Now();
            long next = 0;
            foreach (var it in pushPlan.items)
            {
                if (it == null || it.fired) continue;
                if (it.at < now - 60000) continue;
                if (next == 0 || it.at < next) next = it.at;
            }
            if (next > 0) s += " · 下次 " + PlanWhenText(next);
            return s;
        }

        // ------------------------------------------------------------ 生成
        static void EnsurePlanFresh(bool force)
        {
            if (selftesting || !AiPlanOn()) return;
            if (planBusy) return;
            long now = TaskItem.Now();

            if (pushPlan == null)
            {
                if (AiReady()) { StartAiPlan(); return; }
                BuildLocalPlan();
                return;
            }

            string sig = PlanSignature();
            bool stale = now - pushPlan.generatedAt > 12L * 3600 * 1000 || pushPlan.signature != sig;
            if (!stale)
            {
                bool exhausted = true;
                foreach (var it in pushPlan.items)
                {
                    if (it != null && !it.fired && it.at > now - 60000) { exhausted = false; break; }
                }
                // 计划里的点都弹完了但任务还没做完：过 15 分钟再排一轮
                if (exhausted && now - lastPlanAttempt > 15 * 60 * 1000) stale = true;
            }
            if (!stale && !force) return;
            if (!force && now - lastPlanAttempt < 3 * 60 * 1000) return;
            if (AiReady()) StartAiPlan();
            else BuildLocalPlan();
        }

        static void BuildLocalPlan()
        {
            long now = TaskItem.Now();
            var hs = HabitStats.Compute(tasks, DateTime.Now);
            var ranked = RankOpenTasks(DateTime.Now, hs);
            var p = new PushPlan();
            p.generatedAt = now;
            p.signature = PlanSignature();
            p.source = "local";
            p.summary = "按截止时间提醒";

            foreach (var pick in ranked)
            {
                if (p.items.Count >= 6) break;
                var t = pick.task;
                long at;
                string kind = "nudge";
                string msg;
                if (t.due.HasValue && t.due.Value < now)
                {
                    at = now + 30 * 60000L;
                    kind = "overdue";
                    msg = "已经到期了，挑一件先做 5 分钟";
                }
                else if (!t.due.HasValue)
                {
                    at = now + 3 * 3600 * 1000L;
                    msg = "挑一件没截止的，先做 10 分钟";
                }
                else
                {
                    int lead = t.priority == "high" ? 90 : (t.priority == "low" ? 60 : 75);
                    at = t.due.Value - lead * 60000L;
                    if (at < now + 5 * 60000L) at = now + 5 * 60000L;
                    if (at > t.due.Value - 5 * 60000L) at = t.due.Value - 5 * 60000L;
                    if (at <= now) continue;
                    msg = t.priority == "high" ? "先做 5 分钟，把最难的开了头"
                        : (t.priority == "low" ? "顺手做掉一件" : "有空档就往前推一点");
                }
                if (QuietHour(at) && !UrgentHigh(t, now)) at = ShiftOutOfQuiet(at);
                if (at <= now) continue;
                if (kind == "nudge" && t.due.HasValue && at >= t.due.Value - 5 * 60000L) continue;

                var it = new PushItem();
                it.at = at;
                it.kind = kind;
                it.taskId = t.id;
                it.message = msg;
                it.level = HabitStats.Pi(t.priority);
                p.items.Add(it);
            }

            p.items.Sort(delegate(PushItem a, PushItem b) { return a.at.CompareTo(b.at); });
            if (p.items.Count == 0) { pushPlan = null; SavePlan(); return; }
            pushPlan = p;
            SavePlan();
            StartupLog("本地提醒计划: " + p.items.Count + " 条");
        }

        static string BuildPlanPrompt(List<TaskPick> cands)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("现在：").Append(DateTime.Now.ToString("M月d日 dddd HH:mm", Inv)).Append("\n");
            sb.Append("用户历史：").Append(HabitBrief()).Append("\n");
            sb.Append("今天剩余可用：").Append(AvailableMin()).Append(" 分钟；今天任务预计：").Append(TodayNeedMin()).Append(" 分钟。\n");
            sb.Append("清单里的未完成任务（index | 重要度 | 截止 | 预计 | 已推迟 | 标题）：\n");
            long now = TaskItem.Now();
            for (int i = 0; i < cands.Count; i++)
            {
                var t = cands[i].task;
                string due = "无";
                if (t.due.HasValue)
                {
                    due = DateTimeOffset.FromUnixTimeMilliseconds(t.due.Value).LocalDateTime.ToString("M-d HH:mm", Inv);
                    long diff = t.due.Value - now;
                    if (diff < 0)
                    {
                        long m = (-diff) / 60000;
                        due += "（已逾期 " + (m < 60 ? (m + " 分钟") : (m / 60 + " 小时")) + "）";
                    }
                    else if (diff < 3600000)
                    {
                        due += "（还有 " + Math.Max(1, diff / 60000) + " 分钟）";
                    }
                    else
                    {
                        due += "（还有 " + (diff / 3600000) + " 小时）";
                    }
                }
                sb.Append("- [").Append(i).Append("] ").Append(PriorityName(t.priority)).Append(" | ").Append(due)
                  .Append(" | 预计 ").Append(t.estimateMin > 0 ? t.estimateMin.ToString() : "?").Append(" 分")
                  .Append(" | ").Append(TaskKind.Name(GuessKindOf(t)))
                  .Append(" | ").Append(t.snoozeCount + t.postponeCount).Append(" 次")
                  .Append(" | ").Append(Trim(t.title, 40)).Append("\n");
            }
            if (!string.IsNullOrEmpty(settings.pinTaskId))
            {
                var pin = tasks.Find(delegate(TaskItem x) { return x.id == settings.pinTaskId && !x.done; });
                if (pin != null)
                    sb.Append("\n用户指定优先任务：").Append(Trim(pin.title, 40))
                      .Append("。请至少为它安排一次提醒，并在 summary 里优先提它。\n");
            }
            sb.Append("\n决定接下来 12 小时内的提醒时间点。只输出 JSON，不要解释。");
            return sb.ToString();
        }

        const string PLAN_SYSTEM =
            "你是 ADHD 执行教练。根据任务清单、截止时间和用户习惯，决定接下来 12 小时内什么时候提醒用户、提醒什么。" +
            "原则：1) 高优先/临近截止更密，但同一个任务两次提醒至少隔 60 分钟；中优先 2-3 小时，低优先至少 4 小时。" +
            "2) 23:00-07:00 默认不提醒；只有高优先且 3 小时内截止的任务，可以在截止前安排一次，其余遇到夜间就往后挪到 07:15。" +
            "3) 已经逾期的任务，在 30-90 分钟内安排一次 kind=overdue 的汇总提醒。" +
            "4) 最多 6 条，按时间从早到晚。" +
            "5) message 不超过 24 个字，是弹出时对用户说的一句具体的话，不要复述标题，不要空话。" +
            "6) 如果今天剩余时间明显不够，summary 要直接说“排不下，先做 X，其余改期”。" +
            "只输出严格 JSON：{\"summary\":\"一句话计划\",\"items\":[{\"atMinutes\":45,\"kind\":\"nudge|overdue\",\"index\":0,\"message\":\"...\"}]}";

        static void StartAiPlan()
        {
            if (planBusy || !AiReady() || !AiPlanOn()) return;
            var hs = HabitStats.Compute(tasks, DateTime.Now);
            var ranked = RankOpenTasks(DateTime.Now, hs);
            var cands = new List<TaskPick>();
            for (int i = 0; i < ranked.Count && i < 8; i++) cands.Add(ranked[i]);
            if (cands.Count == 0) { pushPlan = null; SavePlan(); return; }

            planBusy = true;
            lastPlanAttempt = TaskItem.Now();
            string sigAtStart = PlanSignature();
            if (mainWin != null && mainWin.IsVisible) RefreshMain();

            string user = BuildPlanPrompt(cands);
            var th = new Thread(delegate()
            {
                string err;
                string raw = AiChat(PLAN_SYSTEM, user, out err);
                string perr = null;
                PushPlan p = null;
                if (!string.IsNullOrEmpty(raw)) p = ParsePushPlan(raw, cands, out perr);
                Application.Current.Dispatcher.BeginInvoke((Action)delegate
                {
                    planBusy = false;
                    if (p != null && p.items.Count > 0)
                    {
                        p.signature = sigAtStart;   // 用发请求时的签名；期间任务变了会在下一轮重排
                        pushPlan = p;
                        SavePlan();
                        StartupLog("AI 提醒计划: " + p.items.Count + " 条 / " + p.summary);
                    }
                    else
                    {
                        bool needFallback = pushPlan == null
                            || (TaskItem.Now() - pushPlan.generatedAt > 12L * 3600 * 1000);
                        if (needFallback) BuildLocalPlan();
                        StartupLog("AI 提醒计划失败: " + (err == null ? perr : err));
                    }
                    if (mainWin != null && mainWin.IsVisible) RefreshMain();
                });
            });
            th.IsBackground = true;
            th.Start();
        }

        static PushPlan ParsePushPlan(string raw, List<TaskPick> cands, out string err)
        {
            err = null;
            try
            {
                int a = raw.IndexOf('{');
                int b = raw.LastIndexOf('}');
                if (a < 0 || b <= a) { err = "不是 JSON 对象"; return null; }
                var root = Json.Deserialize<Dictionary<string, object>>(raw.Substring(a, b - a + 1));
                if (root == null) { err = "JSON 解析为空"; return null; }
                var p = new PushPlan();
                p.generatedAt = TaskItem.Now();
                p.signature = "";   // 由 UI 线程在回调里补，避免后台线程读 tasks
                p.source = "ai";
                p.summary = Trim(StrOf(root, "summary"), 60);
                if (p.summary.Length == 0) p.summary = "按重要度和截止时间提醒";

                object itemsObj;
                if (!root.TryGetValue("items", out itemsObj) || itemsObj == null) { err = "没有 items"; return null; }
                var arr = AsArray(itemsObj);
                if (arr == null) { err = "items 不是数组"; return null; }

                long now = TaskItem.Now();
                foreach (var o in arr)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    int idx = IntOf(d, "index", -1);
                    if (idx < 0 || idx >= cands.Count) continue;
                    var task = cands[idx].task;
                    if (task == null || task.done) continue;

                    int atMin = IntOf(d, "atMinutes", -1);
                    if (atMin < 1) continue;
                    if (atMin > 1440) atMin = 1440;
                    long at = now + atMin * 60000L;

                    string kind = StrOf(d, "kind");
                    if (kind != "overdue") kind = "nudge";
                    if (task.due.HasValue && task.due.Value < now) kind = "overdue";

                    // 截止前的提醒不能晚于截止；太晚就当成到期汇总
                    if (kind == "nudge" && task.due.HasValue)
                    {
                        long latest = task.due.Value - 5 * 60000L;
                        if (at > latest) at = latest;
                        if (at <= now) at = now + 5 * 60000L;
                    }
                    if (QuietHour(at) && !(kind == "nudge" && UrgentHigh(task, now)))
                        at = ShiftOutOfQuiet(at);
                    if (at <= now) continue;

                    string msg = Trim(StrOf(d, "message"), 40);
                    if (msg.Length == 0)
                        msg = kind == "overdue" ? "已经到期了，挑一件先做 5 分钟" : "先做 5 分钟，开了头就不难";

                    var it = new PushItem();
                    it.at = at;
                    it.kind = kind;
                    it.taskId = task.id;
                    it.message = msg;
                    it.level = HabitStats.Pi(task.priority);
                    p.items.Add(it);
                }

                p.items.Sort(delegate(PushItem x, PushItem y) { return x.at.CompareTo(y.at); });
                var kept = new List<PushItem>();
                foreach (var it in p.items)
                {
                    bool near = false;
                    foreach (var k in kept)
                        if (Math.Abs(k.at - it.at) < 8 * 60000L) { near = true; break; }
                    if (!near) kept.Add(it);
                    if (kept.Count >= 6) break;
                }
                p.items = kept;
                if (p.items.Count == 0) { err = "没有可用的提醒点"; return null; }
                return p;
            }
            catch (Exception ex) { err = "解析失败: " + ex.Message; return null; }
        }

        // ------------------------------------------------------------ 触发
        static void MaintainPlan(long now)
        {
            if (selftesting) return;
            if (!AiPlanOn())
            {
                if (pushPlan != null) pushPlan = null;
                return;
            }
            EnsurePlanFresh(false);
            FirePlan(now);
        }

        static TaskItem FindOpenTask(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var t in tasks)
                if (t != null && !t.done && t.id == id) return t;
            return null;
        }

        static bool AnyReminderVisible()
        {
            return (reminderWindow != null && reminderWindow.IsVisible)
                || (digestWindow != null && digestWindow.IsVisible);
        }

        static void FirePlan(long now)
        {
            if (!PlanActive()) return;
            if (AnyReminderVisible()) return;

            var due = new List<PushItem>();
            foreach (var it in pushPlan.items)
            {
                if (it == null || it.fired) continue;
                if (it.at > now) continue;
                if (now - it.at > GRACE_MS) { it.fired = true; continue; }
                due.Add(it);
            }
            if (due.Count == 0) { SavePlan(); return; }
            var first = due[0];
            if (!selftesting && QuietHour(now))
            {
                PushItem urgent = null;
                foreach (var d in due)
                {
                    if (UrgentHigh(FindOpenTask(d.taskId), now)) { urgent = d; break; }
                }
                if (urgent == null) return;
                first = urgent;
            }

            foreach (var d in due) d.fired = true;
            SavePlan();

            var focus = FindOpenTask(first.taskId);
            if (focus == null) { StartupLog("AI 计划触发但焦点任务已完成/删除，跳过: " + first.message); return; }
            StartupLog("AI 计划触发: " + first.message);
            ShowDigest(first, first.kind);
        }

        // ------------------------------------------------------------ 弹窗
        static string[] LevelFg = { "#A4574A", "#9C7A35", "#557268" };
        static string[] LevelBg = { "#F8E9E6", "#F8F1E2", "#EAF1EE" };
        static string[] LevelLine = { "#ECD3CE", "#ECDFC6", "#CFE0D9" };
        static string[] LevelName = { "重要", "提醒", "顺手" };

        static void ShowDigest(PushItem it, string kind)
        {
            try
            {
                if (digestWindow != null) { try { digestWindow.Close(); } catch { } digestWindow = null; }
                digestItem = it;
                StartupLog("显示汇总: " + kind + " / " + (it == null ? "" : it.message));

                var hs = HabitStats.Compute(tasks, DateTime.Now);
                var ranked = RankOpenTasks(DateTime.Now, hs);
                if (ranked.Count == 0) return;
                bool overdue = kind == "overdue";
                long nowMs = TaskItem.Now();

                // 到期汇总只列已经过期的；截止前的汇总列全部未完成
                var pool = new List<TaskItem>();
                foreach (var pick in ranked)
                {
                    if (overdue && (!pick.task.due.HasValue || pick.task.due.Value >= nowMs)) continue;
                    pool.Add(pick.task);
                }
                if (pool.Count == 0) return;

                TaskItem focus = FindOpenTask(it.taskId);
                if (focus == null || (overdue && !pool.Contains(focus))) focus = pool[0];

                int level = HabitStats.Pi(focus.priority);

                var w = new Window();
                w.WindowStyle = WindowStyle.None;
                w.AllowsTransparency = true;
                w.Background = Brushes.Transparent;
                w.Topmost = true;
                w.ShowInTaskbar = false;
                w.ShowActivated = false;
                w.SizeToContent = SizeToContent.Height;
                w.Width = level == 0 ? 500 : (level == 2 ? 460 : 480);
                w.WindowStartupLocation = WindowStartupLocation.Manual;

                var card = new Border();
                card.CornerRadius = new CornerRadius(16);
                card.Background = B(AccentBg);
                card.BorderBrush = B(LevelLine[level]);
                card.BorderThickness = new Thickness(level == 0 ? 2 : 1);
                card.Padding = new Thickness(18, 15, 18, 14);

                var root = new StackPanel();
                card.Child = root;

                var head = new StackPanel();
                head.Orientation = Orientation.Horizontal;
                head.Margin = new Thickness(0, 0, 0, 8);

                var badge = new Border();
                badge.CornerRadius = new CornerRadius(9);
                badge.Padding = new Thickness(8, 1, 8, 1);
                badge.Background = B(LevelBg[level]);
                badge.BorderBrush = B(LevelLine[level]);
                badge.BorderThickness = new Thickness(1);
                var badgeText = new TextBlock();
                badgeText.Text = overdue ? "到期" : LevelName[level];
                badgeText.FontSize = 10.5;
                badgeText.Foreground = B(LevelFg[level]);
                badge.Child = badgeText;
                head.Children.Add(badge);

                var label = new TextBlock();
                label.Text = overdue ? "  已经过了截止时间" : "  接下来该做";
                label.FontSize = 11.5;
                label.Foreground = B(TextSub);
                label.VerticalAlignment = VerticalAlignment.Center;
                head.Children.Add(label);
                root.Children.Add(head);

                var title = new TextBlock();
                title.Text = overdue
                    ? ("已经到期，" + pool.Count + " 件没完成")
                    : ("还有 " + pool.Count + " 件没做完");
                title.FontSize = level == 0 ? 20 : 18;
                title.FontWeight = FontWeights.SemiBold;
                title.Foreground = B(TextMain);
                title.TextWrapping = TextWrapping.Wrap;
                root.Children.Add(title);

                if (!string.IsNullOrEmpty(it.message))
                {
                    var msg = new TextBlock();
                    msg.Text = (pushPlan != null && pushPlan.source == "ai" ? "AI：" : "") + it.message;
                    msg.FontSize = 11.5;
                    msg.Foreground = B(level == 0 ? LevelFg[0] : AccentDeep);
                    msg.TextWrapping = TextWrapping.Wrap;
                    msg.Margin = new Thickness(0, 4, 0, 10);
                    root.Children.Add(msg);
                }
                else root.Children.Add(new Border { Height = 8 });

                // 焦点 + 最多 2 条其它任务
                int shown = 0;
                var ordered = new List<TaskItem>();
                ordered.Add(focus);
                foreach (var t in pool)
                {
                    if (t.id == focus.id) continue;
                    ordered.Add(t);
                    if (ordered.Count >= 3) break;
                }
                foreach (var t in ordered)
                {
                    if (shown >= 3) break;
                    root.Children.Add(MakeDigestRow(t, t.id == focus.id, level));
                    shown++;
                }
                if (pool.Count > shown)
                {
                    var more = new TextBlock();
                    more.Text = "还有 " + (pool.Count - shown) + " 件，点「看清单」";
                    more.FontSize = 10.5;
                    more.Foreground = B(TextMuted);
                    more.Margin = new Thickness(20, 2, 0, 0);
                    root.Children.Add(more);
                }

                var bar = new StackPanel();
                bar.Orientation = Orientation.Horizontal;
                bar.Margin = new Thickness(0, 12, 0, 0);

                var start = FilledButton("开始做");
                start.FontSize = 12;
                if (level == 0) start.Background = B(LevelFg[0]);
                TaskItem focusRef = focus;
                start.Click += delegate
                {
                    HideDigest();
                    StartTask(focusRef);
                };
                bar.Children.Add(start);

                var list = TinyButton("看清单", AccentDeep);
                list.Click += delegate { HideDigest(); ShowMain("list"); };
                bar.Children.Add(list);

                var later = TinyButton("稍后 15 分", TextSub);
                later.Click += delegate
                {
                    HideDigest();
                    SnoozeDigest(it, focusRef, 15);
                };
                bar.Children.Add(later);

                var ignore = TinyButton("忽略", TextMuted);
                ignore.Click += delegate { HideDigest(); };
                bar.Children.Add(ignore);
                root.Children.Add(bar);

                w.Content = card;
                if (w.Content is UIElement) AttachManualDrag(w, (UIElement)w.Content);
                PlaceAtScreenCenter(w);
                w.Show();
                try
                {
                    w.UpdateLayout();
                    Rect wa = SystemParameters.WorkArea;
                    w.Left = wa.Left + (wa.Width - w.ActualWidth) / 2;
                    w.Top = wa.Top + (wa.Height - w.ActualHeight) / 2;
                }
                catch { }
                digestWindow = w;

                var zTop = new DispatcherTimer();
                zTop.Interval = TimeSpan.FromMilliseconds(600);
                int n = 0;
                zTop.Tick += delegate
                {
                    n++;
                    if (n > 8 || digestWindow == null || !digestWindow.IsVisible) { zTop.Stop(); return; }
                    digestWindow.Topmost = false;
                    digestWindow.Topmost = true;
                };
                zTop.Start();
            }
            catch (Exception ex) { StartupLog("汇总提醒窗失败: " + ex.Message); }
        }

        static Border MakeDigestRow(TaskItem t, bool focus, int focusLevel)
        {
            var row = new Border();
            row.CornerRadius = new CornerRadius(9);
            row.Background = focus ? B(AccentSoft) : B(CardBg);
            row.BorderBrush = focus ? B(LevelLine[focusLevel]) : B(LineCol);
            row.BorderThickness = new Thickness(focus ? 2 : 1);
            row.Padding = new Thickness(9, 6, 9, 6);
            row.Margin = new Thickness(0, 0, 0, 5);

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            g.ColumnDefinitions.Add(new ColumnDefinition());

            var stripe = new Border();
            stripe.CornerRadius = new CornerRadius(2);
            stripe.Background = B(PriorityColor(t.priority));
            stripe.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(stripe, 0);
            g.Children.Add(stripe);

            var sp = new StackPanel();
            var tt = new TextBlock();
            tt.Text = t.title;
            tt.FontSize = 12.5;
            tt.FontWeight = focus ? FontWeights.SemiBold : FontWeights.Normal;
            tt.Foreground = B(TextMain);
            tt.TextTrimming = TextTrimming.CharacterEllipsis;
            sp.Children.Add(tt);

            var meta = new TextBlock();
            meta.Text = PriorityName(t.priority) + " · " + DueText(t, TaskItem.Now())
                      + (t.estimateMin > 0 ? " · 预计 " + t.estimateMin + " 分" : "");
            meta.FontSize = 10.5;
            meta.Foreground = B(TextMuted);
            meta.Margin = new Thickness(0, 2, 0, 0);
            sp.Children.Add(meta);

            Grid.SetColumn(sp, 1);
            g.Children.Add(sp);
            row.Child = g;
            return row;
        }

        static void ShowOverdueDigest(List<TaskItem> list)
        {
            if (list == null || list.Count == 0) return;
            if (AnyReminderVisible()) return;

            int level = 2;
            foreach (var t in list)
            {
                int l = HabitStats.Pi(t.priority);
                if (l < level) level = l;
            }
            var it = new PushItem();
            it.kind = "overdue";
            it.taskId = list[0].id;
            it.level = level;
            it.message = "这些已经过了截止时间，挑一件先做 5 分钟，或者改期";
            it.at = TaskItem.Now();

            // 计划里指向这些任务的条目已经由汇总覆盖，标掉避免重复弹
            if (pushPlan != null && pushPlan.items != null)
            {
                var ids = new HashSet<string>();
                foreach (var t in list) ids.Add(t.id);
                bool changed = false;
                foreach (var pi in pushPlan.items)
                {
                    if (pi != null && !pi.fired && ids.Contains(pi.taskId)) { pi.fired = true; changed = true; }
                }
                if (changed) SavePlan();
            }
            ShowDigest(it, "overdue");
        }

        static void HideDigest()
        {
            digestItem = null;
            if (digestWindow == null) return;
            try { digestWindow.Close(); } catch { }
            digestWindow = null;
        }

        static void SnoozeDigest(PushItem it, TaskItem focus, int mins)
        {
            long at = TaskItem.Now() + mins * 60000L;
            if (pushPlan == null)
            {
                pushPlan = new PushPlan();
                pushPlan.generatedAt = TaskItem.Now();
                pushPlan.signature = PlanSignature();
                pushPlan.source = "local";
                pushPlan.summary = "稍后提醒";
            }
            if (it == null)
            {
                it = new PushItem();
                it.taskId = focus == null ? "" : focus.id;
            }
            it.at = at;
            it.fired = false;
            it.kind = (focus != null && focus.due.HasValue && focus.due.Value < TaskItem.Now()) ? "overdue" : "nudge";
            it.taskId = focus == null ? it.taskId : focus.id;
            it.level = focus == null ? it.level : HabitStats.Pi(focus.priority);
            it.message = "15 分钟后再看看这几件";
            if (!pushPlan.items.Contains(it)) pushPlan.items.Add(it);
            pushPlan.items.Sort(delegate(PushItem a, PushItem b) { return a.at.CompareTo(b.at); });
            SavePlan();
            ShowToast("稍后提醒", mins + " 分钟后再看这几件");
        }
    }
}
