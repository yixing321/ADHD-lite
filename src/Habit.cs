// v0.5：执行闭环。
// 这里放三件事：
//   1) 实际计时（开始 / 暂停 / 完成，spentMs 落到 tasks.json）
//   2) 习惯统计（从真实完成记录里算：超时比例、按时率、高效时段、连续天数）
//   3) 按习惯推荐「现在做哪件」，并把历史喂给 AI（Ai.cs 里调用）

using System;
using System.Collections.Generic;
using System.Windows;

namespace AdhdLite
{
    // 一次推荐的候选
    public class TaskPick
    {
        public TaskItem task;
        public int score;
        public string reason = "";
    }

    public class HabitStats
    {
        public int done7, doneAll, openCount, overdueCount;
        public long focusMs7;
        public int nEst, nOnTime, nOnTimeOk;
        public double overrun;
        public int[] doneByHour = new int[24];
        public string topBucket = "";
        public int topBucketCount;
        public int streakDays;
        public int snoozeTotal, postponeTotal;

        // 0 高 / 1 中 / 2 低（完成数按 30 天，超时/用时也按 30 天）
        public int[] doneCount = new int[3];
        public int[] estCount = new int[3];
        public long[] spentSum = new long[3];
        public int[] estSumMin = new int[3];
        public double[] overrunSum = new double[3];

        // v0.7：按任务类型（6 类）的 30 天统计
        public int[] kindDone = new int[6];
        public int[] kindEstN = new int[6];
        public long[] kindSpent = new long[6];
        public int[] kindEstSum = new int[6];
        public double[] kindOverrun = new double[6];

        const long DAY = 24L * 3600 * 1000;

        public static HabitStats Compute(List<TaskItem> tasks, DateTime now)
        {
            var hs = new HabitStats();
            long nowMs = new DateTimeOffset(now).ToUnixTimeMilliseconds();
            long weekAgo = nowMs - 7 * DAY;
            long monthAgo = nowMs - 30 * DAY;
            int[] bucket7 = new int[4];
            hs.nEst = 0;

            foreach (var t in tasks)
            {
                if (t == null) continue;
                if (t.done)
                {
                    if (t.doneAt <= 0) continue;
                    hs.doneAll++;
                    long at = t.doneAt;
                    bool in7 = at >= weekAgo;
                    if (in7) { hs.done7++; hs.focusMs7 += t.spentMs; }
                    int pi = Pi(t.priority);
                    if (at >= monthAgo) hs.doneCount[pi]++;
                    int ki = TaskKind.Index(t.kind);
                    if (in7) hs.kindDone[ki]++;
                    if (in7 && t.due.HasValue)
                    {
                        hs.nOnTime++;
                        if (at <= t.due.Value + 60000) hs.nOnTimeOk++;
                    }
                    if (in7)
                    {
                        int hour = DateTimeOffset.FromUnixTimeMilliseconds(at).LocalDateTime.Hour;
                        hs.doneByHour[hour]++;
                        bucket7[BucketOf(hour)]++;
                    }
                    // 计划 vs 实际：只统计 30 天内、同时有"预计"和"实际"的记录
                    if (at >= monthAgo && t.spentMs > 0 && t.estimateMin > 0)
                    {
                        double estMs = t.estimateMin * 60000.0;
                        double r = (t.spentMs - estMs) / estMs;
                        if (r < -0.95) r = -0.95;
                        if (r > 3.0) r = 3.0;
                        hs.overrunSum[pi] += r;
                        hs.estCount[pi]++;
                        hs.spentSum[pi] += t.spentMs;
                        hs.estSumMin[pi] += t.estimateMin;
                        hs.overrun += r;
                        hs.nEst++;

                        hs.kindOverrun[ki] += r;
                        hs.kindEstN[ki]++;
                        hs.kindSpent[ki] += t.spentMs;
                        hs.kindEstSum[ki] += t.estimateMin;
                    }
                }
                else
                {
                    hs.openCount++;
                    if (t.due.HasValue && t.due.Value < nowMs) hs.overdueCount++;
                    if (t.snoozeCount > 0) hs.snoozeTotal += t.snoozeCount;
                    if (t.postponeCount > 0) hs.postponeTotal += t.postponeCount;
                }
            }

            if (hs.nEst > 0) hs.overrun = hs.overrun / hs.nEst;

            // 最常完成的时段（近 7 天，至少 3 条才当作信号）
            int best = -1, bestN = 0;
            for (int i = 0; i < 4; i++)
            {
                if (bucket7[i] > bestN) { bestN = bucket7[i]; best = i; }
            }
            if (best >= 0 && bestN >= 3)
            {
                hs.topBucket = BucketName(best);
                hs.topBucketCount = bestN;
            }

            // 连续有完成的天数（今天还没做的话，从昨天开始算）
            DateTime d = now.Date;
            bool todayHas = false;
            foreach (var t in tasks)
            {
                if (t != null && t.done && t.doneAt > 0 &&
                    DateTimeOffset.FromUnixTimeMilliseconds(t.doneAt).LocalDateTime.Date == d)
                { todayHas = true; break; }
            }
            if (!todayHas) d = d.AddDays(-1);
            for (int i = 0; i < 400; i++)
            {
                bool has = false;
                foreach (var t in tasks)
                {
                    if (t != null && t.done && t.doneAt > 0 &&
                        DateTimeOffset.FromUnixTimeMilliseconds(t.doneAt).LocalDateTime.Date == d)
                    { has = true; break; }
                }
                if (!has) break;
                hs.streakDays++;
                d = d.AddDays(-1);
            }
            return hs;
        }

        public static int Pi(string p)
        {
            if (p == "high") return 0;
            if (p == "low") return 2;
            return 1;
        }

        public static string PName(int i)
        {
            return i == 0 ? "高" : (i == 2 ? "低" : "中");
        }

        public static int BucketOf(int hour)
        {
            if (hour >= 6 && hour < 12) return 0;    // 上午
            if (hour >= 12 && hour < 18) return 1;   // 下午
            if (hour >= 18) return 2;                // 晚上
            return 3;                                // 凌晨
        }

        public static string BucketName(int i)
        {
            return i == 0 ? "上午" : (i == 1 ? "下午" : (i == 2 ? "晚上" : "凌晨"));
        }

        public static string Pct(double r)
        {
            int p = (int)Math.Round(r * 100);
            return (p >= 0 ? "+" : "") + p + "%";
        }

        // 某个重要度的历史超时比例（0 = 没数据）
        public double OverrunOf(int pi)
        {
            if (estCount[pi] == 0) return 0;
            return overrunSum[pi] / estCount[pi];
        }

        // 新建任务时的预计用时：基础值 × 你的历史偏差（优先按同类任务，至少 2 条才生效）
        public int SuggestEstimate(string p, string kind = "other")
        {
            int pi = Pi(p);
            int ki = TaskKind.Index(kind);
            int baseMin = pi == 0 ? 30 : (pi == 2 ? 10 : 20);
            if (kind == "comms" || kind == "errand") baseMin = Math.Max(5, baseMin - 5);
            double f = 1.0;
            if (kindEstN[ki] >= 2)
            {
                f = 1.0 + kindOverrun[ki] / kindEstN[ki];
                if (f < 0.6) f = 0.6;
                if (f > 2.5) f = 2.5;
            }
            else if (estCount[pi] >= 2)
            {
                f = 1.0 + OverrunOf(pi);
                if (f < 0.6) f = 0.6;
                if (f > 2.5) f = 2.5;
            }
            int est = (int)Math.Ceiling(baseMin * f / 5.0) * 5;
            if (est < 5) est = 5;
            if (est > 180) est = 180;
            return est;
        }

        public string KindLine(int ki)
        {
            string n = TaskKind.Names[ki];
            if (kindDone[ki] == 0) return n + "：近 7 天还没有完成记录";
            if (kindEstN[ki] == 0) return n + "：完成 " + kindDone[ki] + " 条 · 还没计时";
            long avg = kindSpent[ki] / kindEstN[ki];
            int avgEst = kindEstSum[ki] / kindEstN[ki];
            return n + "：完成 " + kindDone[ki] + " 条 · 平均 " + (avg / 60000)
                 + " 分（预计 " + avgEst + " 分，" + Pct(kindOverrun[ki] / kindEstN[ki]) + "）";
        }

        public string OverrunText(int pi)
        {
            if (estCount[pi] == 0) return "";
            return Pct(OverrunOf(pi));
        }

        public bool InGoodHours(DateTime t)
        {
            if (topBucketCount < 3) return false;
            return topBucket == BucketName(BucketOf(t.Hour));
        }

        public string TimeLine()
        {
            int[] n = new int[4];
            for (int h = 0; h < 24; h++) n[BucketOf(h)] += doneByHour[h];
            return "上午 " + n[0] + " · 下午 " + n[1] + " · 晚上 " + n[2] + " · 凌晨 " + n[3];
        }

        public string SummaryForAi()
        {
            var sb = new System.Text.StringBuilder();
            if (doneAll == 0)
                return "还没有完成记录（0 条）。";
            sb.Append("近 7 天完成 ").Append(done7).Append(" 条");
            if (focusMs7 > 0) sb.Append("，累计专注 ").Append(focusMs7 / 60000).Append(" 分钟");
            if (nEst >= 3) sb.Append("；最近 30 天平均超时 ").Append(Pct(overrun)).Append("（").Append(nEst).Append(" 条有计时）");
            if (nOnTime >= 3)
                sb.Append("；按时率 ").Append((int)Math.Round(100.0 * nOnTimeOk / nOnTime)).Append("%");
            if (topBucketCount >= 3) sb.Append("；最常完成时段：").Append(topBucket);
            if (streakDays >= 2) sb.Append("；连续 ").Append(streakDays).Append(" 天有完成");
            if (snoozeTotal + postponeTotal > 0)
                sb.Append("；被推迟 ").Append(snoozeTotal + postponeTotal).Append(" 次");
            int shownKinds = 0;
            for (int i = 0; i < 6 && shownKinds < 2; i++)
            {
                if (kindEstN[i] < 2) continue;
                sb.Append("；").Append(TaskKind.Names[i]).Append("类平均超时 ")
                  .Append(Pct(kindOverrun[i] / kindEstN[i])).Append("（").Append(kindEstN[i]).Append(" 条）");
                shownKinds++;
            }
            sb.Append("。");
            return sb.ToString();
        }

        public string EstimateNote()
        {
            var parts = new List<string>();
            for (int i = 0; i < 3; i++)
            {
                if (estCount[i] < 2) continue;
                double f = 1.0 + OverrunOf(i);
                if (f < 0.6) f = 0.6;
                if (f > 2.5) f = 2.5;
                parts.Add(PName(i) + " ×" + f.ToString("0.0"));
            }
            if (parts.Count == 0) return "";
            return "新建任务会按你的历史估时：" + string.Join("，", parts.ToArray());
        }

        public string PriorityLine(int pi)
        {
            string n = PName(pi);
            if (doneCount[pi] == 0) return n + "：近 30 天还没有完成记录";
            if (estCount[pi] == 0) return n + "：完成 " + doneCount[pi] + " 条 · 还没计时";
            long avgMs = spentSum[pi] / estCount[pi];
            int avgEst = estSumMin[pi] / estCount[pi];
            return n + "：完成 " + doneCount[pi] + " 条 · 平均实际 " + (avgMs / 60000)
                 + " 分（预计 " + avgEst + " 分，" + Pct(OverrunOf(pi)) + "）";
        }
    }

    public static partial class App
    {
        static bool selftesting = false;
        static int recCursor = 0;                 // 「换一个」用

        // AI 选任务 / 卡住求救的临时结果（只存在内存）
        static bool aiPickBusy = false;
        static string aiPickTaskId = "";
        static string aiPickReason = "";
        static string aiPickStep = "";
        static string aiNudgeTaskId = "";
        static string aiNudgeText = "";

        // -------------------------------------------------------- 计时
        static TaskItem ActiveTask()
        {
            TaskItem best = null;
            foreach (var t in tasks)
            {
                if (t == null || t.done || t.startedAt <= 0) continue;
                if (best == null || t.startedAt > best.startedAt) best = t;
            }
            return best;
        }

        static long ActiveElapsedMs()
        {
            var t = ActiveTask();
            if (t == null) return 0;
            long ms = t.spentMs;
            if (t.startedAt > 0)
            {
                long add = TaskItem.Now() - t.startedAt;
                if (add > 0) ms += add;
            }
            return ms;
        }

        static string FmtMinutes(long ms)
        {
            if (ms <= 0) return "0 分钟";
            long m = ms / 60000;
            if (m < 1) return "不到 1 分钟";
            if (m < 60) return m + " 分钟";
            return (m / 60) + " 小时 " + (m % 60) + " 分";
        }

        static string FmtClock(long ms)
        {
            if (ms < 0) ms = 0;
            long s = ms / 1000;
            long h = s / 3600, m = (s % 3600) / 60, sec = s % 60;
            if (h > 0) return h + ":" + m.ToString("00") + ":" + sec.ToString("00");
            return m.ToString("00") + ":" + sec.ToString("00");
        }

        static int EstimateOf(TaskItem t, HabitStats hs)
        {
            if (t == null) return 0;
            if (t.estimateMin > 0) return t.estimateMin;
            return hs.SuggestEstimate(t.priority, t.kind);
        }

        static int TodayDoneCount()
        {
            string key = DayKey(TaskItem.Now());
            int n = 0;
            foreach (var t in tasks)
                if (t != null && t.done && t.doneAt > 0 && DayKey(t.doneAt) == key) n++;
            return n;
        }

        static void StartTask(TaskItem t)
        {
            if (t == null || t.done) return;
            var cur = ActiveTask();
            if (cur != null && cur != t) PauseTask(cur);
            if (t.startedAt <= 0) t.startedAt = TaskItem.Now();
            if (t.estimateMin <= 0)
                t.estimateMin = HabitStats.Compute(tasks, DateTime.Now).SuggestEstimate(t.priority, GuessKindOf(t));
            SaveTasks();

            aiPickTaskId = "";
            aiPickReason = "";
            aiPickStep = "";
            aiNudgeTaskId = "";
            aiNudgeText = "";
            recCursor = 0;
            StartupLog("开始计时: " + t.title);

            if (selftesting) return;
            UpdateSummary();
            if (mainWin != null && mainWin.IsVisible) RefreshMain();
            AskResumeHint(t);
            ShowToast("开始 · " + t.title, "预计 " + t.estimateMin + " 分钟 · 先做 5 分钟也算开头");
        }

        static long PauseTask(TaskItem t)
        {
            if (t == null || t.startedAt <= 0) return 0;
            long add = TaskItem.Now() - t.startedAt;
            if (add < 0) add = 0;
            // 睡一觉再回来 / 改过系统时钟：不要记成十几个小时
            if (add > 4L * 3600 * 1000) add = 4L * 3600 * 1000;
            t.spentMs = t.spentMs + add;
            t.startedAt = 0;
            SaveTasks();
            StartupLog("暂停计时: " + t.title + " +" + (add / 60000) + " 分");
            return add;
        }

        static void FinishTask(TaskItem t)
        {
            if (t == null) return;
            PauseTask(t);
            t.done = true;
            t.doneAt = TaskItem.Now();
            SaveTasks();

            if (aiPickTaskId == t.id) { aiPickTaskId = ""; aiPickReason = ""; aiPickStep = ""; }
            if (aiNudgeTaskId == t.id) { aiNudgeTaskId = ""; aiNudgeText = ""; }

            if (selftesting) return;
            UpdateSummary();
            if (mainWin != null && mainWin.IsVisible) RefreshMain();

            string sub;
            int est = t.estimateMin;
            if (t.spentMs > 0)
            {
                long mins = Math.Max(1, t.spentMs / 60000);
                sub = "专注 " + FmtMinutes(t.spentMs);
                if (est > 0)
                {
                    long diff = mins - est;
                    if (diff >= 2) sub += " · 预计 " + est + " 分，多了 " + diff + " 分";
                    else if (diff <= -2) sub += " · 预计 " + est + " 分，提前 " + (-diff) + " 分";
                    else sub += " · 预计 " + est + " 分，刚好";
                }
            }
            else
            {
                sub = est > 0 ? "已标记完成（预计 " + est + " 分，没计时）" : "已标记完成";
            }
            sub += " · 今天第 " + TodayDoneCount() + " 件";
            ShowToast("完成 · " + t.title, sub);
        }

        static void PauseActiveForExit()
        {
            if (selftesting) return;   // 截图/自检里的演示任务不能写进真实 tasks.json
            var t = ActiveTask();
            if (t != null) PauseTask(t);
        }

        // 启动时恢复：只恢复 4 小时内的计时；更长的一律按暂停处理（大概率是忘了关）
        static void NormalizeActiveTasks()
        {
            long now = TaskItem.Now();
            bool dirty = false;
            TaskItem keep = null;
            foreach (var t in tasks)
            {
                if (t == null || t.done || t.startedAt <= 0) continue;
                long span = now - t.startedAt;
                if (span < 0 || span > 4L * 3600 * 1000)
                {
                    t.startedAt = 0;
                    dirty = true;
                    StartupLog("计时恢复：超过 4 小时，按暂停处理 " + t.title);
                }
                else if (keep == null) keep = t;
                else
                {
                    t.spentMs = t.spentMs + span;
                    t.startedAt = 0;
                    dirty = true;
                    StartupLog("计时恢复：同时只有一条能计时，暂停 " + t.title);
                }
            }
            if (dirty) SaveTasks();
            if (keep != null) StartupLog("恢复进行中的任务: " + keep.title);
        }

        // 老任务（v0.4 之前）没有预计用时：按当前习惯补一个，复盘才有"计划"可比
        static void NormalizeTaskEstimates()
        {
            var hs = HabitStats.Compute(tasks, DateTime.Now);
            bool dirty = false;
            foreach (var t in tasks)
            {
                if (t == null || t.done || t.estimateMin > 0) continue;
                if (string.IsNullOrEmpty(t.kind)) t.kind = TaskKind.Guess(t.title);
                t.estimateMin = hs.SuggestEstimate(t.priority, t.kind);
                dirty = true;
            }
            if (dirty) SaveTasks();
        }

        // -------------------------------------------------------- 按习惯推荐
        static string AgoText(long mins)
        {
            if (mins < 60) return Math.Max(1, mins) + " 分钟";
            if (mins < 24 * 60) return (mins / 60) + " 小时";
            return (mins / 60 / 24) + " 天";
        }

        static List<TaskPick> RankOpenTasks(DateTime now, HabitStats hs)
        {
            long nowMs = new DateTimeOffset(now).ToUnixTimeMilliseconds();
            long todayEnd = new DateTimeOffset(now.Date.AddDays(1)).ToUnixTimeMilliseconds();
            var list = new List<TaskPick>();

            foreach (var t in tasks)
            {
                if (t == null || t.done) continue;
                int score = 0;
                var whys = new List<KeyValuePair<int, string>>();

                if (t.due.HasValue)
                {
                    long due = t.due.Value;
                    if (due < nowMs)
                    {
                        long min = (nowMs - due) / 60000;
                        score += 6000 + (int)Math.Min(3000, min / 10);
                        whys.Add(new KeyValuePair<int, string>(100, "已逾期 " + AgoText(min)));
                    }
                    else if (due < todayEnd)
                    {
                        score += 2500;
                        string hh = DateTimeOffset.FromUnixTimeMilliseconds(due).LocalDateTime.ToString("HH:mm", Inv);
                        whys.Add(new KeyValuePair<int, string>(90, "今天 " + hh + " 到期"));
                    }
                    else if (due < todayEnd + 2 * 86400000L)
                    {
                        score += 800;
                        whys.Add(new KeyValuePair<int, string>(50, "两天内到期"));
                    }
                }
                else
                {
                    score += 50;
                    whys.Add(new KeyValuePair<int, string>(10, "没有硬截止"));
                }

                int pi = HabitStats.Pi(t.priority);
                if (pi == 0) { score += 700; whys.Add(new KeyValuePair<int, string>(80, "高优先")); }
                else if (pi == 1) score += 350;
                else score += 120;

                if (t.snoozeCount > 0)
                {
                    int n = Math.Min(5, t.snoozeCount);
                    score += 60 * n;
                    whys.Add(new KeyValuePair<int, string>(40 + 10 * n, "已推迟 " + t.snoozeCount + " 次"));
                }
                if (t.postponeCount > 0)
                {
                    score += 30 * Math.Min(5, t.postponeCount);
                }

                long ageDays = (nowMs - t.createdAt) / 86400000L;
                if (ageDays >= 2)
                {
                    score += (int)Math.Min(300, ageDays * 20);
                    if (ageDays >= 3) whys.Add(new KeyValuePair<int, string>(30, "放了 " + ageDays + " 天"));
                }

                if (hs.InGoodHours(now))
                {
                    score += 250;
                    whys.Add(new KeyValuePair<int, string>(60, "现在是你的高效时段"));
                }

                int est = EstimateOf(t, hs);
                if (est > 0 && est <= 15)
                {
                    score += 80;
                    whys.Add(new KeyValuePair<int, string>(20, "预计 " + est + " 分能收尾"));
                }
                if (!string.IsNullOrEmpty(t.lastNote)) score += 30;

                whys.Sort(delegate(KeyValuePair<int, string> a, KeyValuePair<int, string> b)
                {
                    return b.Key.CompareTo(a.Key);
                });
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < whys.Count && i < 3; i++)
                {
                    if (sb.Length > 0) sb.Append(" · ");
                    sb.Append(whys[i].Value);
                }
                var p = new TaskPick();
                p.task = t;
                p.score = score;
                p.reason = sb.ToString();
                list.Add(p);
            }

            list.Sort(delegate(TaskPick a, TaskPick b)
            {
                if (a.score != b.score) return b.score.CompareTo(a.score);
                int pa = PriorityWeight(a.task.priority), pb = PriorityWeight(b.task.priority);
                if (pa != pb) return pb.CompareTo(pa);
                long da = a.task.due.HasValue ? a.task.due.Value : long.MaxValue;
                long db = b.task.due.HasValue ? b.task.due.Value : long.MaxValue;
                return da.CompareTo(db);
            });
            return list;
        }

        static TaskItem PickNext(DateTime now, out string reason)
        {
            reason = "";
            var hs = HabitStats.Compute(tasks, now);
            var ranked = RankOpenTasks(now, hs);
            if (ranked.Count == 0) return null;
            int idx = recCursor % ranked.Count;
            if (idx < 0) idx = 0;
            reason = ranked[idx].reason;
            return ranked[idx].task;
        }
    }
}
