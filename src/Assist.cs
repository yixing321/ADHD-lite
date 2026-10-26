// v0.7：AI 助理层。
// 任务类型（写作/代码/沟通/杂事/学习/其他）、超载判断、拖延任务拆小、
// 回到任务的"下一步"、个人规则、查重/含糊提示、AI 取舍。

using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AdhdLite
{
    public static class TaskKind
    {
        public static readonly string[] Keys = { "writing", "code", "comms", "errand", "study", "other" };
        public static readonly string[] Names = { "写作", "代码", "沟通", "杂事", "学习", "其他" };

        public static int Index(string k)
        {
            for (int i = 0; i < Keys.Length; i++) if (Keys[i] == k) return i;
            return Keys.Length - 1;
        }

        public static string Name(string k)
        {
            return Names[Index(k)];
        }

        static bool Has(string s, string[] keys)
        {
            for (int i = 0; i < keys.Length; i++) if (s.IndexOf(keys[i]) >= 0) return true;
            return false;
        }

        public static string Guess(string title)
        {
            string s = title == null ? "" : title;
            if (Has(s, new string[] { "写", "方案", "报告", "文档", "稿", "论文", "PPT", "ppt" })) return "writing";
            if (Has(s, new string[] { "代码", "改", "修", "调试", "接口", "提交", "测试", "部署", "报错", "bug", "BUG" })) return "code";
            if (Has(s, new string[] { "邮件", "微信", "QQ", "回复", "沟通", "电话", "开会", "发给", "确认", "问一下" })) return "comms";
            if (Has(s, new string[] { "买", "交", "取", "寄", "缴费", "房租", "网盘", "整理", "打扫", "报销" })) return "errand";
            if (Has(s, new string[] { "学", "刷题", "复习", "考试", "看", "读", "背", "课程" })) return "study";
            return "other";
        }

        public static bool IsVague(string title)
        {
            string s = (title == null ? "" : title).Trim();
            if (s.Length < 4) return true;
            string[] bad = { "看看", "弄一下", "那个", "处理一下", "搞一下", "整理一下", "想想", "再说" };
            for (int i = 0; i < bad.Length; i++) if (s == bad[i] || s.StartsWith(bad[i])) return true;
            return false;
        }

        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder();
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }
    }

    public static partial class App
    {
        static string pendingDupTitle = "";
        static string resumePendingId = "";

        // ------------------------------------------------------------ 类型
        static string GuessKindOf(TaskItem t)
        {
            if (t == null) return "other";
            if (!string.IsNullOrEmpty(t.kind) && t.kind != "other") return t.kind;
            return TaskKind.Guess(t.title);
        }

        // ------------------------------------------------------------ 查重 / 含糊
        static TaskItem FindSimilarOpen(string title)
        {
            string n = TaskKind.Normalize(title);
            if (n.Length < 3) return null;
            foreach (var t in tasks)
            {
                if (t == null || t.done) continue;
                string m = TaskKind.Normalize(t.title);
                if (m.Length < 3) continue;
                if (m == n || m.IndexOf(n) >= 0 || n.IndexOf(m) >= 0) return t;
            }
            return null;
        }

        // 返回 true = 这次先别提交（提示了重复）
        static bool CaptureGuard(TaskItem candidate, out string hint)
        {
            hint = null;
            string title = candidate == null ? "" : candidate.title;
            if (string.IsNullOrEmpty(title)) return false;
            if (pendingDupTitle == title)
            {
                pendingDupTitle = "";
                return false;
            }
            var dup = FindSimilarOpen(title);
            if (dup != null)
            {
                pendingDupTitle = title;
                hint = "已有类似任务：" + Trim(dup.title, 24) + "；再按一次 Enter 仍要添加";
                return true;
            }
            pendingDupTitle = "";
            if (TaskKind.IsVague(title))
            {
                // 提示放在提交之后用 toast 展示，避免录入窗立刻收起导致提示看不见
            }
            return false;
        }

        // ------------------------------------------------------------ 超载
        static int TodayNeedMin()
        {
            long end = DayStartMs(DateTime.Today.AddDays(1));
            var hs = HabitStats.Compute(tasks, DateTime.Now);
            int sum = 0;
            foreach (var t in tasks)
            {
                if (t == null || t.done || !t.due.HasValue) continue;
                if (t.due.Value >= end) continue;
                sum += Math.Max(5, EstimateOf(t, hs));
            }
            return sum;
        }

        static int AvailableMin()
        {
            DateTime end = DateTime.Today.AddHours(22);
            if (DateTime.Now >= end) return 0;
            return (int)(end - DateTime.Now).TotalMinutes;
        }

        static string OverloadText()
        {
            int need = TodayNeedMin();
            int avail = AvailableMin();
            if (need <= 0) return "";
            if (avail <= 0)
            {
                if (need >= 30) return "已经 22 点以后：今天还有约 " + FmtMinutes(need * 60000L) + " 的任务，建议改到明天";
                return "";
            }
            if (need >= 60 && need > avail * 0.8)
                return "今天剩余 " + FmtMinutes(avail * 60000L) + "，任务预计 " + FmtMinutes(need * 60000L) + "：排不下，建议改期或拆小";
            return "";
        }

        // ------------------------------------------------------------ 拖延任务拆小
        static bool IsStubborn(TaskItem t)
        {
            return t != null && !t.done && (t.snoozeCount + t.postponeCount) >= 2;
        }

        const string SPLIT_SYSTEM =
            "你是 ADHD 执行教练。用户反复拖延这条任务，通常是因为它太大、下一步不清楚。" +
            "把它拆成 2-3 个具体步骤，每步 5-25 分钟内能开始或完成；第一步必须是 2 分钟内能动手的动作。" +
            "步骤要以动词开头，保留原任务里的对象、文件名、人名等关键上下文；" +
            "不要写“准备一下”“制定计划”“继续努力”这种空步骤。" +
            "只输出严格的 JSON 数组，不要解释、不要代码块标记：格式 " +
            "[{\"title\":\"步骤\",\"priority\":\"high|mid|low\",\"delayMinutes\":60,\"estimateMinutes\":15,\"kind\":\"writing|code|comms|errand|study|other\"}]。" +
            "kind 按步骤内容选；delayMinutes 是建议截止（从现在起多少分钟）；" +
            "如果原任务有截止时间，尽量把步骤安排在它之前。";

        static void AiSplitStubborn(TaskItem t)
        {
            if (t == null || t.done) return;
            if (!AiReady()) { ShowHint("先填 API Key 才能让 AI 拆小"); return; }
            string due = t.due.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(t.due.Value).LocalDateTime.ToString("M月d日 HH:mm", Inv)
                : "无截止";
            string user = "任务：" + t.title
                        + "\n重要度：" + PriorityName(t.priority)
                        + "\n截止：" + due
                        + "\n已推迟：" + (t.snoozeCount + t.postponeCount) + " 次"
                        + "\n预计用时：" + (t.estimateMin > 0 ? t.estimateMin + " 分钟" : "未估")
                        + "\n上次做到：" + (string.IsNullOrEmpty(t.lastNote) ? "（没记）" : t.lastNote)
                        + "\n当前负担：" + TodayLoadBrief()
                        + "\n用户历史：" + HabitBrief();
            ShowToast("AI 正在拆小…", "几秒后弹出拆分结果");
            var th = new Thread(delegate()
            {
                string err;
                string raw = AiChat(SPLIT_SYSTEM, user, out err);
                string perr;
                List<AiSubTask> subs = ParseDecompose(raw, out perr);
                if (string.IsNullOrEmpty(raw)) StartupLog("AI 拆小失败: " + err);
                else StartupLog("AI 拆小 raw: " + Trim(raw, 600));
                Application.Current.Dispatcher.BeginInvoke((Action)delegate
                {
                    if (subs == null || subs.Count == 0)
                    {
                        ShowToast("拆小失败", (err == null ? perr : err));
                        return;
                    }
                    ShowDecomposeResult(t.title, subs, t);
                });
            });
            th.IsBackground = true;
            th.Start();
        }

        // ------------------------------------------------------------ 回到任务：下一步
        const string RESUME_SYSTEM =
            "你是 ADHD 执行教练。用户之前做到一半，现在回来了。" +
            "根据上次进度给一句具体的下一步（不超过 24 个字，能直接开始做），" +
            "不要复述任务、不要鼓励、不要解释。只回复这一句。";

        static void AskResumeHint(TaskItem t)
        {
            if (t == null || !AiReady() || string.IsNullOrEmpty(t.lastNote)) return;
            if (!string.IsNullOrEmpty(t.resumeHint)) return;
            if (resumePendingId == t.id) return;
            resumePendingId = t.id;
            if (ActiveTask() == t && mainWin != null && mainWin.IsVisible) RefreshMain();

            string user = "任务：" + t.title
                        + "\n上次做到：" + t.lastNote
                        + "\n重要度：" + PriorityName(t.priority)
                        + "\n预计用时：" + t.estimateMin + " 分钟"
                        + "\n用户历史：" + HabitBrief();
            var th = new Thread(delegate()
            {
                string err;
                string raw = AiChat(RESUME_SYSTEM, user, out err);
                if (!string.IsNullOrEmpty(raw)) StartupLog("AI 下一步 raw: " + Trim(raw, 300));
                string one = "";
                if (!string.IsNullOrEmpty(raw))
                {
                    one = raw.Trim().Replace("\r", " ").Replace("\n", " ");
                    one = Trim(one.Trim('“', '”', '"', ' '), 60);
                }
                Application.Current.Dispatcher.BeginInvoke((Action)delegate
                {
                    if (resumePendingId == t.id) resumePendingId = "";
                    if (one.Length > 0)
                    {
                        t.resumeHint = one;
                        SaveTasks();
                        if (ActiveTask() == t && mainWin != null && mainWin.IsVisible) RefreshMain();
                        StartupLog("AI 下一步: " + t.title + " -> " + one);
                    }
                    else if (ActiveTask() == t && mainWin != null && mainWin.IsVisible) RefreshMain();
                });
            });
            th.IsBackground = true;
            th.Start();
        }

        // ------------------------------------------------------------ 个人规则
        static void AddRule(string rule)
        {
            rule = (rule == null ? "" : rule.Trim().Trim('“', '”', '"', ' '));
            if (rule.Length < 4) return;
            if (rule.Length > 60) rule = rule.Substring(0, 60);
            var list = new List<string>();
            if (!string.IsNullOrEmpty(settings.rules))
            {
                foreach (var s in settings.rules.Split('\n'))
                {
                    string x = s.Trim();
                    if (x.Length > 0 && x != rule) list.Add(x);
                }
            }
            list.Insert(0, rule);
            while (list.Count > 5) list.RemoveAt(list.Count - 1);
            settings.rules = string.Join("\n", list.ToArray());
            SaveSettings();
        }

        static string[] RuleLines()
        {
            if (string.IsNullOrEmpty(settings.rules)) return new string[0];
            var list = new List<string>();
            foreach (var s in settings.rules.Split('\n'))
            {
                string x = s.Trim();
                if (x.Length > 0) list.Add(x);
            }
            return list.ToArray();
        }

        static void SplitRuleFromReview(string raw, out string text, out string rule)
        {
            text = "";
            rule = "";
            if (string.IsNullOrEmpty(raw)) return;
            var sb = new System.Text.StringBuilder();
            foreach (var line in raw.Replace("\r", "").Split('\n'))
            {
                string x = line.Trim();
                if (x.StartsWith("规则：") || x.StartsWith("规则:") || x.StartsWith("规则 ："))
                {
                    int p = x.IndexOf('：');
                    if (p < 0) p = x.IndexOf(':');
                    rule = p >= 0 ? x.Substring(p + 1).Trim() : x;
                    continue;
                }
                sb.AppendLine(line);
            }
            text = sb.ToString().Trim();
        }

        // ------------------------------------------------------------ AI 取舍
        const string TRIAGE_SYSTEM =
            "你是 ADHD 执行教练。今天任务排不下。只输出 2-3 句中文，不要 Markdown、不要列表、不要标题。" +
            "第 1 句点名先做哪一件，并说一个具体理由（逾期/截止/重要度/历史）；" +
            "第 2 句点名哪一件改期或拆小，给出改到什么时候或拆成什么；" +
            "第 3 句说今天明确不要做什么。具体、克制、不鸡汤，不要重复全部任务。" +
            "如果发现两条其实是同一件事、或某条太含糊，用半句指出，不要展开。";

        // 取舍只看今天：未来任务和无截止任务不该混进“今天怎么取舍”
        static string BuildTriagePrompt(HabitStats hs, List<TaskPick> ranked)
        {
            long end = DayStartMs(DateTime.Today.AddDays(1));
            var sb = new System.Text.StringBuilder();
            sb.Append("现在：").Append(DateTime.Now.ToString("M月d日 dddd HH:mm", Inv)).Append("\n");
            sb.Append("今天剩余可用：").Append(Math.Max(0, AvailableMin())).Append(" 分钟；今天到期任务预计：")
              .Append(TodayNeedMin()).Append(" 分钟。\n");
            sb.Append("用户历史：").Append(hs.SummaryForAi()).Append("\n");
            if (!string.IsNullOrEmpty(settings.rules))
                sb.Append("个人规则：").Append(settings.rules.Replace("\n", "；")).Append("\n");

            var lines = new System.Text.StringBuilder();
            int today = 0, future = 0, noDue = 0;
            for (int i = 0; i < ranked.Count; i++)
            {
                var t = ranked[i].task;
                if (!t.due.HasValue) { noDue++; continue; }
                if (t.due.Value >= end) { future++; continue; }
                if (today >= 8) continue;
                today++;
                lines.Append("- ").Append(PriorityName(t.priority)).Append(" · ")
                     .Append(TaskKind.Name(GuessKindOf(t))).Append(" · ")
                     .Append(DateTimeOffset.FromUnixTimeMilliseconds(t.due.Value).LocalDateTime.ToString("M-d HH:mm", Inv))
                     .Append(t.due.Value < TaskItem.Now() ? "（已逾期）" : "")
                     .Append(" · 预计 ").Append(t.estimateMin > 0 ? t.estimateMin.ToString() : "?").Append(" 分")
                     .Append(" · 推迟 ").Append(t.snoozeCount + t.postponeCount).Append(" 次 · ")
                     .Append(Trim(t.title, 36)).Append("\n");
            }
            sb.Append("今天未完成（按建议顺序，最多 8 条）：\n").Append(lines.ToString());
            if (future > 0) sb.Append("另有 ").Append(future).Append(" 条未来任务，不在今天取舍范围。\n");
            if (noDue > 0) sb.Append("另有 ").Append(noDue).Append(" 条无截止任务，不在今天取舍范围。\n");
            sb.Append("只对今天这些任务做取舍。");
            return sb.ToString();
        }

        static void AiTriage()
        {
            if (!AiReady()) { ShowHint("先填 API Key 才能让 AI 取舍"); return; }
            var hs = HabitStats.Compute(tasks, DateTime.Now);
            var ranked = RankOpenTasks(DateTime.Now, hs);
            string user = BuildTriagePrompt(hs, ranked);
            ShowToast("AI 正在做取舍…", "几秒后弹出建议");
            var th = new Thread(delegate()
            {
                string err;
                string raw = AiChat(TRIAGE_SYSTEM, user, out err);
                if (string.IsNullOrEmpty(raw)) StartupLog("AI 取舍失败: " + err);
                else StartupLog("AI 取舍 raw: " + Trim(raw, 600));
                Application.Current.Dispatcher.BeginInvoke((Action)delegate
                {
                    if (string.IsNullOrEmpty(raw)) { ShowToast("AI 取舍失败", err); return; }
                    ShowAdvice("今天怎么取舍", raw.Trim(), "AI");
                });
            });
            th.IsBackground = true;
            th.Start();
        }

        // ------------------------------------------------------------ 建议窗
        static void ShowAdvice(string title, string body, string source)
        {
            try
            {
                var w = new Window();
                w.WindowStyle = WindowStyle.None;
                w.AllowsTransparency = true;
                w.Background = Brushes.Transparent;
                w.Topmost = true;
                w.ShowInTaskbar = false;
                w.SizeToContent = SizeToContent.Height;
                w.Width = 500;

                var card = new Border();
                card.CornerRadius = new CornerRadius(14);
                card.Background = B(AccentBg);
                card.BorderBrush = B(AccentLine);
                card.BorderThickness = new Thickness(1);
                card.Padding = new Thickness(18, 14, 18, 14);

                var root = new StackPanel();
                card.Child = root;

                var head = new TextBlock();
                head.Text = "✦ " + title;
                head.FontSize = 14;
                head.FontWeight = FontWeights.SemiBold;
                head.Foreground = B(AccentDeep);
                root.Children.Add(head);

                var txt = new TextBlock();
                txt.Text = body;
                txt.FontSize = 12.5;
                txt.Foreground = B(TextMain);
                txt.TextWrapping = TextWrapping.Wrap;
                txt.Margin = new Thickness(0, 8, 0, 0);
                root.Children.Add(txt);

                var foot = new TextBlock();
                foot.Text = (source == "AI" ? "AI 建议 · 仅供参考" : "建议");
                foot.FontSize = 10;
                foot.Foreground = B(TextMuted);
                foot.Margin = new Thickness(0, 8, 0, 0);
                root.Children.Add(foot);

                var close = FilledButton("知道了");
                close.HorizontalAlignment = HorizontalAlignment.Right;
                close.Margin = new Thickness(0, 10, 0, 0);
                close.Click += delegate { try { w.Close(); } catch { } };
                root.Children.Add(close);

                w.Content = card;
                PlaceAtScreenCenter(w);
                w.Show();
                w.Activate();
            }
            catch (Exception ex) { StartupLog("建议窗失败: " + ex.Message); }
        }
    }
}
