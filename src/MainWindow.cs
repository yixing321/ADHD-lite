// 主界面部分（v0.3）：左侧导航 + 清单 / 月历 / 设置
// 由 Program.cs 通过 partial 合并；这里只放 UI 构建与视图切换。

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AdhdLite
{
    public static partial class App
    {
        static Window mainWin;
        static StackPanel mainContent;
        static TextBlock mainTitle;
        static string mainView = "list";
        static StackPanel navPanel;
        // 换主题时要重刷的"骨架"元素（这些只在建窗时设过一次，不重建就不会变）
        static Border mainCard, mainSide;
        static TextBlock mainBrand;
        static Button mainAddBtn, mainCloseX;
        static ScrollViewer mainScroll;
        static Border mainTitleDecoHost, sideDecoHost, mainSideLine, settingsDeco;

        static void ShowMain(string view)
        {
            if (mainWin == null) BuildMainWindow();
            if (!string.IsNullOrEmpty(view)) mainView = view;
            RefreshMain();
            mainWin.Show();
            mainWin.Activate();
        }

        // -------------------------------------------------------- 主界面骨架
        static void BuildMainWindow()
        {
            mainWin = new Window();
            mainWin.WindowStyle = WindowStyle.None;
            mainWin.AllowsTransparency = true;
            mainWin.Background = Brushes.Transparent;
            mainWin.Width = 760;
            mainWin.Height = 580;
            mainWin.MinWidth = 560;
            mainWin.MinHeight = 420;
            mainWin.ResizeMode = ResizeMode.NoResize;   // 缩放由 EnableResize 的 8 个热区自己实现
            mainWin.ShowInTaskbar = true;
            mainWin.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var card = new Border();
            mainCard = card;
            card.CornerRadius = new CornerRadius(18);
            card.Background = B(AccentBg);
            card.BorderBrush = B(LineCol);
            card.BorderThickness = new Thickness(1);

            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(158) });
            root.ColumnDefinitions.Add(new ColumnDefinition());

            // ---------------- 左侧
            var sideBorder = new Border();
            mainSide = sideBorder;
            sideBorder.Background = B(SideBg);
            sideBorder.BorderBrush = B(SideLine);
            sideBorder.BorderThickness = new Thickness(0, 0, 1, 0);
            sideBorder.CornerRadius = new CornerRadius(18, 0, 0, 18);
            sideBorder.Padding = new Thickness(12, 16, 12, 12);

            var side = new StackPanel();
            navPanel = side;

            var brand = new TextBlock();
            mainBrand = brand;
            brand.Text = "ADHD-lite";
            brand.FontSize = 14;
            brand.FontWeight = FontWeights.SemiBold;
            brand.Foreground = B(SideBrand);
            brand.Margin = new Thickness(6, 0, 0, 8);
            side.Children.Add(brand);

            sideDecoHost = new Border();
            sideDecoHost.Height = 52;
            sideDecoHost.Margin = new Thickness(2, 0, 2, 8);
            sideDecoHost.IsHitTestVisible = false;
            sideDecoHost.Child = Deco.Make(StyleIndex(), ColorIndex(), 46, SideBrand, 0.6);
            side.Children.Add(sideDecoHost);

            side.Children.Add(NavButton("清单", "list"));
            side.Children.Add(NavButton("月历", "calendar"));
            side.Children.Add(NavButton("复盘", "review"));
            side.Children.Add(NavButton("设置", "settings"));

            var addBtn = new Button();
            mainAddBtn = addBtn;
            addBtn.Content = "＋ 记一件事";
            addBtn.FontSize = 12.5;
            addBtn.Foreground = B("#FBFDFC");
            addBtn.Background = B(Accent);
            addBtn.BorderThickness = new Thickness(0);
            addBtn.Padding = new Thickness(0, 9, 0, 9);
            addBtn.Margin = new Thickness(0, 12, 0, 0);
            addBtn.Cursor = Cursors.Hand;
            addBtn.Template = MakeFlatButtonTemplate();
            addBtn.MouseEnter += delegate { addBtn.Opacity = 0.88; };
            addBtn.MouseLeave += delegate { addBtn.Opacity = 1.0; };
            addBtn.Click += delegate { ShowPopup(); };
            side.Children.Add(addBtn);

            var spacer = new Border();
            mainSideLine = spacer;
            spacer.Height = 1;
            spacer.Margin = new Thickness(4, 18, 4, 10);
            spacer.Background = B(SideLine);
            side.Children.Add(spacer);

            var diag = TinyButton("诊断", SideTextSub);
            diag.HorizontalAlignment = HorizontalAlignment.Left;
            diag.Click += delegate { OpenDiagnostics(); };
            diag.MouseEnter += delegate { diag.Background = B(SideHover); };
            diag.MouseLeave += delegate { diag.Background = Brushes.Transparent; };
            side.Children.Add(diag);

            var quit = TinyButton("退出程序", "#C47B6D");
            quit.HorizontalAlignment = HorizontalAlignment.Left;
            quit.Click += delegate { Quit("主界面退出"); };
            side.Children.Add(quit);

            sideBorder.Child = side;
            Grid.SetColumn(sideBorder, 0);
            root.Children.Add(sideBorder);

            // ---------------- 右侧
            var right = new Grid();
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition());

            // 标题栏（整条可拖，留出边距当"窗口边框"）
            var titleBar = new Border();
            titleBar.Background = Brushes.Transparent;
            titleBar.Padding = new Thickness(22, 14, 16, 10);
            AttachManualDrag(mainWin, titleBar);

            var titleGrid = new Grid();
            mainTitle = new TextBlock();
            mainTitle.Text = "清单";
            mainTitle.FontSize = 17;
            mainTitle.FontWeight = FontWeights.SemiBold;
            mainTitle.Foreground = B(TextMain);
            titleGrid.Children.Add(mainTitle);

            mainTitleDecoHost = new Border();
            mainTitleDecoHost.Width = 24;
            mainTitleDecoHost.Height = 24;
            mainTitleDecoHost.HorizontalAlignment = HorizontalAlignment.Left;
            mainTitleDecoHost.VerticalAlignment = VerticalAlignment.Center;
            mainTitleDecoHost.Margin = new Thickness(50, 0, 0, 0);
            mainTitleDecoHost.IsHitTestVisible = false;
            mainTitleDecoHost.Child = Deco.Make(StyleIndex(), ColorIndex(), 22, Accent, 0.9);
            titleGrid.Children.Add(mainTitleDecoHost);

            var closeX = new Button();
            mainCloseX = closeX;
            closeX.Content = "✕";
            closeX.FontFamily = new FontFamily("Segoe UI Symbol, Microsoft YaHei UI");
            closeX.FontSize = 12;
            closeX.Foreground = B(TextMuted);
            closeX.Background = Brushes.Transparent;
            closeX.BorderThickness = new Thickness(0);
            closeX.Padding = new Thickness(0);
            closeX.Width = 30;
            closeX.Height = 30;
            closeX.HorizontalContentAlignment = HorizontalAlignment.Center;
            closeX.VerticalContentAlignment = VerticalAlignment.Center;
            closeX.Cursor = Cursors.Hand;
            closeX.HorizontalAlignment = HorizontalAlignment.Right;
            closeX.VerticalAlignment = VerticalAlignment.Center;
            closeX.Template = MakeCenteredButtonTemplate();
            closeX.MouseEnter += delegate { closeX.Foreground = B("#C47B6D"); closeX.Background = B(AccentSoft); };
            closeX.MouseLeave += delegate { closeX.Foreground = B(TextMuted); closeX.Background = Brushes.Transparent; };
            closeX.Click += delegate { mainWin.Hide(); };
            titleGrid.Children.Add(closeX);

            titleBar.Child = titleGrid;
            Grid.SetRow(titleBar, 0);
            right.Children.Add(titleBar);

            mainContent = new StackPanel();
            mainContent.Margin = new Thickness(22, 4, 22, 18);

            var sv = new ScrollViewer();
            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            var sbStyle = MakeSoftScrollBarStyle();
            if (sbStyle != null) sv.Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), sbStyle);
            mainScroll = sv;
            sv.Content = mainContent;
            Grid.SetRow(sv, 1);
            right.Children.Add(sv);

            // 设置页右侧留白：放一块大的淡纹样（只在该页显示）
            settingsDeco = new Border();
            settingsDeco.Width = 220;
            settingsDeco.Height = 440;
            settingsDeco.HorizontalAlignment = HorizontalAlignment.Right;
            settingsDeco.VerticalAlignment = VerticalAlignment.Center;
            settingsDeco.Margin = new Thickness(0, 20, 10, 0);
            settingsDeco.IsHitTestVisible = false;
            settingsDeco.Visibility = Visibility.Collapsed;
            settingsDeco.Child = Deco.MakeScene(StyleIndex(), ColorIndex(), 210, AccentLine, 0.18, 1);
            Grid.SetRow(settingsDeco, 1);
            right.Children.Add(settingsDeco);
            mainWin.SizeChanged += delegate
            {
                if (settingsDeco != null)
                    settingsDeco.Visibility = (mainView == "settings" && mainWin.Width >= 700)
                        ? Visibility.Visible : Visibility.Collapsed;
            };

            Grid.SetColumn(right, 1);
            root.Children.Add(right);

            card.Child = root;
            mainWin.Content = card;
            AttachManualDrag(mainWin, card);
            EnableResize(mainWin, root);
            ApplyTheme();
        }

        static Button NavButton(string text, string view)
        {
            var b = new Button();
            b.Content = text;
            b.Tag = view;
            b.FontSize = 13;
            b.Foreground = B(SideText);
            b.Background = Brushes.Transparent;
            b.BorderThickness = new Thickness(0);
            b.Padding = new Thickness(12, 8, 12, 8);
            b.Margin = new Thickness(0, 1, 0, 1);
            b.HorizontalContentAlignment = HorizontalAlignment.Left;
            b.Cursor = Cursors.Hand;
            b.Template = MakeFlatButtonTemplate();
            b.MouseEnter += delegate { if ((string)b.Tag != mainView) b.Background = B(SideHover); };
            b.MouseLeave += delegate { if ((string)b.Tag != mainView) b.Background = Brushes.Transparent; };
            b.Click += delegate { mainView = (string)b.Tag; RefreshMain(); };
            return b;
        }

        // -------------------------------------------------------- 视图刷新
        static void RefreshMain()
        {
            if (mainContent == null) return;
            mainContent.Children.Clear();

            if (mainTitle != null)
                mainTitle.Text = mainView == "list" ? "清单"
                               : (mainView == "calendar" ? "月历"
                               : (mainView == "review" ? "复盘" : "设置"));
            if (settingsDeco != null)
                settingsDeco.Visibility = (mainView == "settings" && mainWin != null && mainWin.Width >= 700)
                    ? Visibility.Visible : Visibility.Collapsed;

            if (navPanel != null)
            {
                foreach (var child in navPanel.Children)
                {
                    var nb = child as Button;
                    if (nb == null || nb.Tag == null) continue;
                    bool on = (string)nb.Tag == mainView;
                    nb.Background = on ? B(Accent) : Brushes.Transparent;
                    nb.Foreground = on ? B("#FBFDFC") : B(SideText);
                }
            }

            if (mainView == "list") BuildListView();
            else if (mainView == "calendar") BuildCalendarView();
            else if (mainView == "review") BuildReviewView();
            else BuildSettingsView();
            if (mainView != "list") EnsureActiveTimer(false);
        }

        // ---------------- 清单视图（待办 / 已完成 两个独立页）
        static void BuildListView()
        {
            long now = TaskItem.Now();
            var hs = HabitStats.Compute(tasks, DateTime.Now);
            var open = new List<TaskItem>();
            var done = new List<TaskItem>();
            foreach (var t in tasks) { if (t.done) done.Add(t); else open.Add(t); }

            open.Sort(delegate(TaskItem a, TaskItem b)
            {
                int ra = Rank(a, now), rb = Rank(b, now);
                if (ra != rb) return ra.CompareTo(rb);
                int pa = PriorityWeight(a.priority), pb = PriorityWeight(b.priority);
                if (pa != pb) return pb.CompareTo(pa);
                long da = a.due.HasValue ? a.due.Value : long.MaxValue;
                long db = b.due.HasValue ? b.due.Value : long.MaxValue;
                return da.CompareTo(db);
            });

            long endToday = DayStartMs(DateTime.Today.AddDays(1));
            int todayOpen = 0;
            foreach (var t in open)
            {
                if (t.due.HasValue && t.due.Value < endToday) todayOpen++;
            }

            var head = new Grid();
            head.Margin = new Thickness(0, 0, 0, 12);

            var tabs = new StackPanel();
            tabs.Orientation = Orientation.Horizontal;
            tabs.VerticalAlignment = VerticalAlignment.Center;

            var tabDeco = Deco.MakeScene(StyleIndex(), ColorIndex(), 18, Accent, 0.9, listToday ? 1 : 0);
            tabDeco.VerticalAlignment = VerticalAlignment.Center;
            tabDeco.Margin = new Thickness(0, 0, 7, 0);
            tabs.Children.Add(tabDeco);

            var tabOpen = SegPill("待办 " + open.Count, !listShowDone && !listToday);
            var tabToday = SegPill("今天 " + todayOpen, listToday && !listShowDone);
            var tabDone = SegPill("已完成 " + done.Count, listShowDone);
            tabOpen.Padding = new Thickness(12, 5, 12, 5);
            tabToday.Padding = new Thickness(12, 5, 12, 5);
            tabDone.Padding = new Thickness(12, 5, 12, 5);
            tabOpen.Margin = new Thickness(0, 0, 6, 0);
            tabToday.Margin = new Thickness(0, 0, 6, 0);
            tabDone.Margin = new Thickness(0);
            tabOpen.Click += delegate { listShowDone = false; listToday = false; RefreshMain(); };
            tabToday.Click += delegate { listShowDone = false; listToday = true; RefreshMain(); };
            tabDone.Click += delegate { listShowDone = true; listToday = false; RefreshMain(); };
            tabs.Children.Add(tabOpen);
            tabs.Children.Add(tabToday);
            tabs.Children.Add(tabDone);
            head.Children.Add(tabs);

            var statLine = new TextBlock();
            statLine.FontSize = 11;
            statLine.Foreground = B(TextMuted);
            statLine.HorizontalAlignment = HorizontalAlignment.Right;
            statLine.VerticalAlignment = VerticalAlignment.Center;
            statLine.Text = hs.done7 > 0
                ? "近 7 天完成 " + hs.done7 + " 件 · 专注 " + FmtMinutes(hs.focusMs7)
                : "还没有完成记录";
            var statRow = new StackPanel();
            statRow.Orientation = Orientation.Horizontal;
            statRow.HorizontalAlignment = HorizontalAlignment.Right;
            statRow.VerticalAlignment = VerticalAlignment.Center;
            var statDeco = Deco.Make(StyleIndex(), ColorIndex(), 16, AccentLine, 0.9);
            statDeco.Margin = new Thickness(0, 0, 6, 0);
            statRow.Children.Add(statDeco);
            statRow.Children.Add(statLine);
            head.Children.Add(statRow);
            mainContent.Children.Add(head);

            // 计时中：显示「正在做」，不再推荐别的任务（减少分心）
            if (!listShowDone)
            {
                var active = ActiveTask();
                if (active != null)
                {
                    mainContent.Children.Add(MakeActiveBar(active, hs));
                    EnsureActiveTimer(true);
                }
                else
                {
                    EnsureActiveTimer(false);
                    mainContent.Children.Add(MakeNowCard(hs));
                }
            }
            else EnsureActiveTimer(false);

            var page = listShowDone ? done : open;
            if (!listShowDone && listToday)
            {
                var todayPage = new List<TaskItem>();
                foreach (var t in page)
                    if (t.due.HasValue && t.due.Value < endToday) todayPage.Add(t);
                page = todayPage;
            }
            if (page.Count == 0)
            {
                if (listShowDone)
                {
                    var emptyBox = new StackPanel();
                    emptyBox.Margin = new Thickness(2, 18, 0, 0);
                    var ed = Deco.Make(StyleIndex(), ColorIndex(), 46, AccentLine, 0.75);
                    ed.Margin = new Thickness(0, 0, 0, 8);
                    emptyBox.Children.Add(ed);
                    var empty = new TextBlock();
                    empty.Text = "还没有完成的任务";
                    empty.FontSize = 12;
                    empty.Foreground = B(TextMuted);
                    emptyBox.Children.Add(empty);
                    mainContent.Children.Add(emptyBox);
                }
                else if (listToday)
                {
                    var emptyBox = new StackPanel();
                    emptyBox.Margin = new Thickness(2, 18, 0, 0);
                    var ed = Deco.MakeScene(StyleIndex(), ColorIndex(), 54, AccentLine, 0.7, 1);
                    ed.Margin = new Thickness(0, 0, 0, 8);
                    emptyBox.Children.Add(ed);
                    var empty = new TextBlock();
                    empty.Text = "今天没有到期任务，想到再记";
                    empty.FontSize = 12;
                    empty.Foreground = B(TextMuted);
                    emptyBox.Children.Add(empty);
                    mainContent.Children.Add(emptyBox);
                }
                return;
            }

            foreach (var t in page) mainContent.Children.Add(MakeRow(t, now, listShowDone));

            if (!listShowDone)
            {
                var tip = new TextBlock();
                tip.Text = "点「开始」计时；完成之后，复盘页会告诉你「计划和实际差多少」。";
                tip.FontSize = 10.5;
                tip.Foreground = B(TextMuted);
                tip.Margin = new Thickness(2, 8, 0, 0);
                mainContent.Children.Add(tip);
            }
        }

        // ---------------- 正在做：计时条
        static Border MakeActiveBar(TaskItem t, HabitStats hs)
        {
            var card = new Border();
            card.CornerRadius = new CornerRadius(12);
            card.Background = B(CardBg);
            card.BorderBrush = B(AccentLine);
            card.BorderThickness = new Thickness(1);
            card.Padding = new Thickness(12, 10, 12, 10);
            card.Margin = new Thickness(0, 0, 0, 8);

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(9) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dot = new Border();
            dot.Width = 9;
            dot.Height = 9;
            dot.CornerRadius = new CornerRadius(5);
            dot.Background = B(Accent);
            dot.VerticalAlignment = VerticalAlignment.Top;
            dot.Margin = new Thickness(0, 6, 0, 0);
            Grid.SetColumn(dot, 0);
            g.Children.Add(dot);

            var info = new StackPanel();
            var label = new TextBlock();
            label.Text = "正在做 · " + PriorityName(t.priority);
            label.FontSize = 10.5;
            label.Foreground = B(TextMuted);
            info.Children.Add(label);

            var title = new TextBlock();
            title.Text = t.title;
            title.FontSize = 15;
            title.FontWeight = FontWeights.SemiBold;
            title.Foreground = B(TextMain);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            info.Children.Add(title);

            int est = EstimateOf(t, hs);
            string sub = est > 0 ? "预计 " + est + " 分" : "";
            if (resumePendingId == t.id)
                sub += (sub.Length > 0 ? " · " : "") + "AI 正在想下一步…";
            else if (!string.IsNullOrEmpty(t.resumeHint))
                sub += (sub.Length > 0 ? " · " : "") + "下一步：" + t.resumeHint;
            else if (!string.IsNullOrEmpty(t.lastNote))
                sub += (sub.Length > 0 ? " · " : "") + "上次：" + t.lastNote;
            if (sub.Length > 0)
            {
                var s = new TextBlock();
                s.Text = sub;
                s.FontSize = 10.5;
                s.Foreground = B(TextMuted);
                s.TextTrimming = TextTrimming.CharacterEllipsis;
                s.Margin = new Thickness(0, 2, 0, 0);
                info.Children.Add(s);
            }
            Grid.SetColumn(info, 1);
            g.Children.Add(info);

            var clock = new StackPanel();
            clock.VerticalAlignment = VerticalAlignment.Center;
            clock.Margin = new Thickness(10, 0, 4, 0);
            var timer = new TextBlock();
            timer.FontFamily = new FontFamily("Consolas, Microsoft YaHei UI");
            timer.FontSize = 19;
            timer.FontWeight = FontWeights.SemiBold;
            timer.Foreground = B(AccentDeep);
            timer.HorizontalAlignment = HorizontalAlignment.Right;
            activeTimerText = timer;
            long elapsed = ActiveElapsedMs();
            timer.Text = FmtClock(elapsed);
            clock.Children.Add(timer);

            var over = new TextBlock();
            over.FontSize = 10;
            over.Foreground = B(TextMuted);
            over.HorizontalAlignment = HorizontalAlignment.Right;
            activeOverrunText = over;
            over.Text = OverrunLine(elapsed, est);
            clock.Children.Add(over);
            Grid.SetColumn(clock, 2);
            g.Children.Add(clock);

            var btns = new StackPanel();
            btns.Orientation = Orientation.Horizontal;
            btns.VerticalAlignment = VerticalAlignment.Center;
            btns.Margin = new Thickness(8, 0, 0, 0);

            var done = FilledButton("完成");
            done.FontSize = 11.5;
            done.Padding = new Thickness(11, 6, 11, 6);
            done.Click += delegate { FinishTask(t); };
            btns.Children.Add(done);

            var pause = TinyButton("暂停", TextSub);
            pause.Click += delegate
            {
                long add = PauseTask(t);
                EnsureActiveTimer(false);
                RefreshMain();
                if (add > 0) ShowToast("暂停 · " + t.title, "这次 " + FmtMinutes(add) + "，下次可继续");
            };
            btns.Children.Add(pause);

            var note = TinyButton("记一句", AccentDeep);
            note.Click += delegate { ShowNoteDialog(t); };
            btns.Children.Add(note);

            if (AiReady())
            {
                var stuck = TinyButton("卡住了", TextSub);
                stuck.Click += delegate { AskAiUnstuck(t); };
                btns.Children.Add(stuck);
            }
            Grid.SetColumn(btns, 3);
            g.Children.Add(btns);

            card.Child = g;

            if (aiNudgeTaskId == t.id && !string.IsNullOrEmpty(aiNudgeText))
            {
                var host = new StackPanel();
                host.Margin = new Thickness(0, 0, 0, 8);
                host.Children.Add(card);
                var nudge = new TextBlock();
                nudge.Text = "先试这一步：" + aiNudgeText;
                nudge.FontSize = 11.5;
                nudge.Foreground = B(AccentDeep);
                nudge.TextWrapping = TextWrapping.Wrap;
                nudge.Margin = new Thickness(20, -2, 0, 0);
                host.Children.Add(nudge);
                // 返回值类型是 Border，这里用一个透明外框包住，避免改调用方
                var wrap = new Border();
                wrap.Child = host;
                wrap.Margin = new Thickness(0);
                return wrap;
            }
            return card;
        }

        static string OverrunLine(long elapsed, int est)
        {
            if (est <= 0) return "";
            long plan = est * 60000L;
            long diff = elapsed - plan;
            if (diff > 60000) return "已超预计 " + (diff / 60000) + " 分";
            if (diff < -60000) return "剩 " + ((-diff) / 60000) + " 分";
            return "刚好到预计";
        }

        // ---------------- 现在做哪件
        static Border MakeNowCard(HabitStats hs)
        {
            var card = new Border();
            card.CornerRadius = new CornerRadius(12);
            card.Background = B(AccentSoft);
            card.BorderBrush = B(AccentLine);
            card.BorderThickness = new Thickness(1);
            card.Padding = new Thickness(14, 12, 14, 12);
            card.Margin = new Thickness(0, 0, 0, 10);

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            g.ColumnDefinitions.Add(new ColumnDefinition());

            var stripe = new Border();
            stripe.CornerRadius = new CornerRadius(2);
            stripe.Background = B(Accent);
            stripe.Margin = new Thickness(0, 0, 10, 0);
            Grid.SetColumn(stripe, 0);
            g.Children.Add(stripe);

            var sp = new StackPanel();

            var label = new TextBlock();
            label.Text = "现在做哪件";
            label.FontSize = 10.5;
            label.Foreground = B(TextSub);
            sp.Children.Add(label);

            string why;
            var pick = PickNext(DateTime.Now, out why);
            if (!string.IsNullOrEmpty(aiPickTaskId))
            {
                foreach (var t in tasks)
                {
                    if (t != null && t.id == aiPickTaskId && !t.done) { pick = t; break; }
                }
            }

            if (pick == null)
            {
                var doneAll = new TextBlock();
                doneAll.Text = "清单已经清空了";
                doneAll.FontSize = 16;
                doneAll.FontWeight = FontWeights.SemiBold;
                doneAll.Foreground = B(TextMain);
                doneAll.Margin = new Thickness(0, 3, 0, 0);
                sp.Children.Add(doneAll);

                var hint = new TextBlock();
                hint.Text = "按 " + hotkeyName + " 记下一件，或者去月历看看。";
                hint.FontSize = 11;
                hint.Foreground = B(TextMuted);
                hint.Margin = new Thickness(0, 4, 0, 0);
                sp.Children.Add(hint);
                Grid.SetColumn(sp, 1);
                g.Children.Add(sp);
                card.Child = g;
                return card;
            }

            int est = EstimateOf(pick, hs);
            var title = new TextBlock();
            title.Text = pick.title;
            title.FontSize = 16;
            title.FontWeight = FontWeights.SemiBold;
            title.Foreground = B(TextMain);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            title.Margin = new Thickness(0, 3, 0, 0);
            sp.Children.Add(title);

            string meta = TaskKind.Name(GuessKindOf(pick)) + " · " + PriorityName(pick.priority) + " · " + DueText(pick, TaskItem.Now());
            if (est > 0) meta += " · 预计 " + est + " 分";
            var metaTb = new TextBlock();
            metaTb.Text = meta;
            metaTb.FontSize = 10.5;
            metaTb.Foreground = B(TextSub);
            metaTb.Margin = new Thickness(0, 3, 0, 0);
            sp.Children.Add(metaTb);

            if (!string.IsNullOrEmpty(pick.lastNote))
            {
                var nt = new TextBlock();
                nt.Text = "上次做到：" + pick.lastNote;
                nt.FontSize = 10.5;
                nt.Foreground = B(TextMuted);
                nt.TextTrimming = TextTrimming.CharacterEllipsis;
                nt.Margin = new Thickness(0, 2, 0, 0);
                sp.Children.Add(nt);
            }

            string reasonText;
            if (aiPickTaskId == pick.id && !string.IsNullOrEmpty(aiPickReason))
                reasonText = "AI 建议：" + aiPickReason;
            else if (aiPickBusy)
                reasonText = "AI 正在看你的清单和习惯…";
            else
                reasonText = "为什么是它：" + why;
            var reason = new TextBlock();
            reason.Text = reasonText;
            reason.FontSize = 11.5;
            reason.Foreground = B(AccentDeep);
            reason.TextWrapping = TextWrapping.Wrap;
            reason.Margin = new Thickness(0, 5, 0, 0);
            sp.Children.Add(reason);

            if (aiPickTaskId == pick.id && !string.IsNullOrEmpty(aiPickStep))
            {
                var step = new TextBlock();
                step.Text = "第一步：" + aiPickStep;
                step.FontSize = 10.5;
                step.Foreground = B(TextSub);
                step.TextWrapping = TextWrapping.Wrap;
                step.Margin = new Thickness(0, 2, 0, 0);
                sp.Children.Add(step);
            }

            var planRow = new StackPanel();
            planRow.Orientation = Orientation.Horizontal;
            planRow.Margin = new Thickness(0, 6, 0, 0);

            var planTb = new TextBlock();
            planTb.Text = PlanStatusText();
            planTb.FontSize = 10.5;
            planTb.Foreground = B(TextMuted);
            planTb.TextWrapping = TextWrapping.Wrap;
            planTb.VerticalAlignment = VerticalAlignment.Center;
            planTb.MaxWidth = 360;
            planRow.Children.Add(planTb);

            var replan = TinyButton(AiPlanOn() ? "重排" : "去设置", TextSub);
            replan.VerticalAlignment = VerticalAlignment.Center;
            replan.Click += delegate
            {
                if (!AiPlanOn() || !AiReady()) { mainView = "settings"; RefreshMain(); return; }
                EnsurePlanFresh(true);
            };
            planRow.Children.Add(replan);
            sp.Children.Add(planRow);

            string overload = OverloadText();
            if (!string.IsNullOrEmpty(overload))
            {
                var oRow = new StackPanel();
                oRow.Orientation = Orientation.Horizontal;
                oRow.Margin = new Thickness(0, 5, 0, 0);

                var oTb = new TextBlock();
                oTb.Text = overload;
                oTb.FontSize = 10.5;
                oTb.Foreground = B(AccentDeep);
                oTb.TextWrapping = TextWrapping.Wrap;
                oTb.MaxWidth = 330;
                oTb.VerticalAlignment = VerticalAlignment.Center;
                oRow.Children.Add(oTb);

                if (AiReady())
                {
                    var triage = TinyButton("AI 取舍", AccentDeep);
                    triage.VerticalAlignment = VerticalAlignment.Center;
                    triage.Click += delegate { AiTriage(); };
                    oRow.Children.Add(triage);
                }
                sp.Children.Add(oRow);
            }

            var btns = new StackPanel();
            btns.Orientation = Orientation.Horizontal;
            btns.Margin = new Thickness(0, 8, 0, 0);

            var start = FilledButton("开始做");
            start.FontSize = 11.5;
            start.Padding = new Thickness(12, 6, 12, 6);
            var picked = pick;
            start.Click += delegate { StartTask(picked); };
            btns.Children.Add(start);

            var swap = TinyButton("换一个", TextSub);
            swap.Click += delegate
            {
                recCursor++;
                aiPickTaskId = "";
                aiPickReason = "";
                aiPickStep = "";
                RefreshMain();
            };
            btns.Children.Add(swap);

            if (AiReady() && settings.aiPick != 0)
            {
                var ai = TinyButton(aiPickBusy ? "AI 正在选…" : "AI 帮我选", AccentDeep);
                ai.IsEnabled = !aiPickBusy;
                ai.Click += delegate { StartAiPick(); };
                btns.Children.Add(ai);
            }
            sp.Children.Add(btns);

            Grid.SetColumn(sp, 1);
            g.Children.Add(sp);
            card.Child = g;
            return card;
        }

        // ---------------- 记一句：做到哪了
        static void ShowNoteDialog(TaskItem t)
        {
            try
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
                card.Width = 420;

                var root = new StackPanel();
                card.Child = root;

                var head = new TextBlock();
                head.Text = "记一句：做到哪了";
                head.FontSize = 13;
                head.FontWeight = FontWeights.SemiBold;
                head.Foreground = B(TextMain);
                root.Children.Add(head);

                var box = new TextBox();
                box.Text = t.lastNote == null ? "" : t.lastNote;
                box.FontSize = 12.5;
                box.Height = 72;
                box.AcceptsReturn = true;
                box.TextWrapping = TextWrapping.Wrap;
                box.Padding = new Thickness(8, 6, 8, 6);
                box.Margin = new Thickness(0, 10, 0, 0);
                box.Background = B(AccentBg);
                box.Foreground = B(TextMain);
                box.BorderBrush = B(LineCol);
                box.BorderThickness = new Thickness(1);
                root.Children.Add(box);

                var hint = new TextBlock();
                hint.Text = "下次打开清单 / 推荐到它时，会显示这句话。";
                hint.FontSize = 10.5;
                hint.Foreground = B(TextMuted);
                hint.Margin = new Thickness(0, 6, 0, 0);
                root.Children.Add(hint);

                var bar = new StackPanel();
                bar.Orientation = Orientation.Horizontal;
                bar.HorizontalAlignment = HorizontalAlignment.Right;
                bar.Margin = new Thickness(0, 12, 0, 0);

                var cancel = TinyButton("取消", TextSub);
                cancel.Click += delegate { try { w.Close(); } catch { } };
                bar.Children.Add(cancel);

                var ok = FilledButton("保存");
                ok.Click += delegate
                {
                    t.lastNote = (box.Text ?? "").Trim();
                    t.resumeHint = "";
                    SaveTasks();
                    try { w.Close(); } catch { }
                    RefreshMain();
                };
                bar.Children.Add(ok);
                root.Children.Add(bar);

                w.Content = card;
                w.Show();
                w.Activate();
                box.Focus();
                box.SelectAll();
            }
            catch (Exception ex) { StartupLog("记一句失败: " + ex.Message); }
        }

        // ---------------- 计时器：只刷新数字，不重建界面
        static TextBlock activeTimerText;
        static TextBlock activeOverrunText;
        static DispatcherTimer activeUiTimer;

        static void EnsureActiveTimer(bool on)
        {
            if (activeUiTimer == null)
            {
                activeUiTimer = new DispatcherTimer();
                activeUiTimer.Interval = TimeSpan.FromSeconds(1);
                activeUiTimer.Tick += delegate { UpdateActiveTimerUi(); };
            }
            if (on)
            {
                if (!activeUiTimer.IsEnabled) activeUiTimer.Start();
            }
            else activeUiTimer.Stop();
        }

        static void UpdateActiveTimerUi()
        {
            var t = ActiveTask();
            if (t == null) { activeUiTimer.Stop(); return; }
            long elapsed = ActiveElapsedMs();
            if (activeTimerText != null) activeTimerText.Text = FmtClock(elapsed);
            if (activeOverrunText != null)
                activeOverrunText.Text = OverrunLine(elapsed, t.estimateMin);
        }

        // ---------------- 复盘：计划 vs 实际 + 习惯画像 + AI 解读
        static void BuildReviewView()
        {
            var hs = HabitStats.Compute(tasks, DateTime.Now);

            if (hs.doneAll == 0)
            {
                var empty = new TextBlock();
                empty.Text = "还没有完成记录。\n先完成一条任务（点「开始」计时），这里就会长出你的习惯。";
                empty.FontSize = 12.5;
                empty.Foreground = B(TextMuted);
                empty.TextWrapping = TextWrapping.Wrap;
                empty.Margin = new Thickness(2, 24, 0, 0);
                mainContent.Children.Add(empty);
                return;
            }

            mainContent.Children.Add(SectionTitle("近 7 天"));
            var tiles = new System.Windows.Controls.Primitives.UniformGrid();
            tiles.Columns = 4;
            tiles.Margin = new Thickness(0, 0, 0, 2);
            tiles.Children.Add(StatTile(hs.done7.ToString(), "完成", hs.streakDays >= 2 ? ("连续 " + hs.streakDays + " 天") : "件"));
            tiles.Children.Add(StatTile(hs.focusMs7 > 0 ? (hs.focusMs7 / 60000) + " 分" : "—", "专注", "有计时的任务"));
            tiles.Children.Add(StatTile(hs.nOnTime > 0 ? ((int)Math.Round(100.0 * hs.nOnTimeOk / hs.nOnTime)) + "%" : "—", "按时率", "有截止的任务"));
            tiles.Children.Add(StatTile(hs.nEst > 0 ? HabitStats.Pct(hs.overrun) : "—", "计划偏差", "30 天 · 实际 vs 预计"));
            mainContent.Children.Add(tiles);

            mainContent.Children.Add(SectionTitle("AI 解读"));
            if (reviewBusy)
            {
                var busy = new TextBlock();
                busy.Text = "AI 正在读你这 7 天的完成记录…";
                busy.FontSize = 11.5;
                busy.Foreground = B(TextSub);
                busy.Margin = new Thickness(2, 2, 0, 0);
                mainContent.Children.Add(busy);
            }
            else if (!string.IsNullOrEmpty(settings.lastInsight))
            {
                var card = new Border();
                card.CornerRadius = new CornerRadius(10);
                card.Background = B(AccentSoft);
                card.BorderBrush = B(AccentLine);
                card.BorderThickness = new Thickness(1);
                card.Padding = new Thickness(12, 10, 12, 8);
                card.Margin = new Thickness(0, 0, 0, 2);

                var sp = new StackPanel();
                var txt = new TextBlock();
                txt.Text = settings.lastInsight;
                txt.FontSize = 12.5;
                txt.Foreground = B(TextMain);
                txt.TextWrapping = TextWrapping.Wrap;
                sp.Children.Add(txt);

                string when = settings.insightAt > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(settings.insightAt).LocalDateTime.ToString("M-d HH:mm", Inv)
                    : "";
                var hint = new TextBlock();
                hint.Text = (when.Length > 0 ? ("更新于 " + when + " · ") : "") + "这条结论会带进之后的选任务 / 拆解 / 稍后建议";
                hint.FontSize = 10;
                hint.Foreground = B(TextMuted);
                hint.Margin = new Thickness(0, 5, 0, 0);
                sp.Children.Add(hint);

                var bar = new StackPanel();
                bar.Orientation = Orientation.Horizontal;
                bar.Margin = new Thickness(0, 8, 0, 0);

                var again = TinyButton("重新看看", AccentDeep);
                again.Click += delegate { StartWeeklyReview(hs); };
                bar.Children.Add(again);

                var clear = TinyButton("清空", TextMuted);
                clear.Click += delegate
                {
                    settings.lastInsight = "";
                    settings.insightAt = 0;
                    SaveSettings();
                    RefreshMain();
                };
                bar.Children.Add(clear);
                sp.Children.Add(bar);

                card.Child = sp;
                mainContent.Children.Add(card);
            }
            else
            {
                var hint = new TextBlock();
                hint.Text = "让 AI 把「完成 / 超时 / 时段」串起来，给一句下次能用的调整。会发送最近的任务标题和用时。";
                hint.FontSize = 11;
                hint.Foreground = B(TextMuted);
                hint.TextWrapping = TextWrapping.Wrap;
                hint.Margin = new Thickness(2, 0, 0, 0);
                mainContent.Children.Add(hint);

                var go = TinyButton(AiReady() ? "让 AI 看看这周" : "先填 API Key", AccentDeep);
                go.Margin = new Thickness(2, 6, 0, 0);
                go.Click += delegate
                {
                    if (!AiReady()) { mainView = "settings"; RefreshMain(); return; }
                    StartWeeklyReview(hs);
                };
                mainContent.Children.Add(go);
            }

            if (!string.IsNullOrEmpty(reviewError))
            {
                var err = new TextBlock();
                err.Text = reviewError;
                err.FontSize = 10.5;
                err.Foreground = B(AccentDeep);
                err.TextWrapping = TextWrapping.Wrap;
                err.Margin = new Thickness(2, 5, 0, 0);
                mainContent.Children.Add(err);
            }

            mainContent.Children.Add(SectionTitle("习惯画像（近 30 天）"));
            for (int i = 0; i < 3; i++)
            {
                var line = new TextBlock();
                line.Text = hs.PriorityLine(i);
                line.FontSize = 11.5;
                line.Foreground = B(TextSub);
                line.Margin = new Thickness(2, 0, 0, 4);
                mainContent.Children.Add(line);
            }

            bool anyKind = false;
            for (int i = 0; i < 6; i++) if (hs.kindDone[i] > 0 || hs.kindEstN[i] > 0) anyKind = true;
            if (anyKind)
            {
                var kindTitle = new TextBlock();
                kindTitle.Text = "按类型";
                kindTitle.FontSize = 11.5;
                kindTitle.FontWeight = FontWeights.SemiBold;
                kindTitle.Foreground = B(TextSub);
                kindTitle.Margin = new Thickness(2, 7, 0, 3);
                mainContent.Children.Add(kindTitle);
                for (int i = 0; i < 6; i++)
                {
                    if (hs.kindDone[i] == 0 && hs.kindEstN[i] == 0) continue;
                    var line = new TextBlock();
                    line.Text = hs.KindLine(i);
                    line.FontSize = 11;
                    line.Foreground = B(TextMuted);
                    line.Margin = new Thickness(2, 0, 0, 3);
                    mainContent.Children.Add(line);
                }
            }

            var ruleLines = RuleLines();
            if (ruleLines.Length > 0)
            {
                mainContent.Children.Add(SectionTitle("我的规则"));
                foreach (var r in ruleLines)
                {
                    var line = new TextBlock();
                    line.Text = "· " + r;
                    line.FontSize = 11.5;
                    line.Foreground = B(AccentDeep);
                    line.TextWrapping = TextWrapping.Wrap;
                    line.Margin = new Thickness(2, 0, 0, 4);
                    mainContent.Children.Add(line);
                }
                var clearRules = TinyButton("清空规则", TextMuted);
                clearRules.Margin = new Thickness(2, 2, 0, 0);
                clearRules.HorizontalAlignment = HorizontalAlignment.Left;
                clearRules.Click += delegate
                {
                    settings.rules = "";
                    SaveSettings();
                    RefreshMain();
                };
                mainContent.Children.Add(clearRules);
            }

            var time = new TextBlock();
            time.Text = "完成时段（近 7 天）：" + hs.TimeLine();
            time.FontSize = 11;
            time.Foreground = B(TextMuted);
            time.Margin = new Thickness(2, 2, 0, 0);
            mainContent.Children.Add(time);

            if (!string.IsNullOrEmpty(hs.topBucket))
            {
                var best = new TextBlock();
                best.Text = "你最常在" + hs.topBucket + "完成（" + hs.topBucketCount + " 条）";
                best.FontSize = 11;
                best.Foreground = B(TextMuted);
                best.Margin = new Thickness(2, 3, 0, 0);
                mainContent.Children.Add(best);
            }

            string note = hs.EstimateNote();
            if (!string.IsNullOrEmpty(note))
            {
                var est = new TextBlock();
                est.Text = note;
                est.FontSize = 11;
                est.Foreground = B(AccentDeep);
                est.TextWrapping = TextWrapping.Wrap;
                est.Margin = new Thickness(2, 5, 0, 0);
                mainContent.Children.Add(est);
            }

            mainContent.Children.Add(SectionTitle("最近完成"));
            var recent = new List<TaskItem>();
            foreach (var t in tasks) if (t != null && t.done && t.doneAt > 0) recent.Add(t);
            recent.Sort(delegate(TaskItem a, TaskItem b) { return b.doneAt.CompareTo(a.doneAt); });

            if (recent.Count == 0)
            {
                var none = new TextBlock();
                none.Text = "还没有带时间戳的完成记录";
                none.FontSize = 11;
                none.Foreground = B(TextMuted);
                mainContent.Children.Add(none);
            }
            else
            {
                int shown = 0;
                foreach (var t in recent)
                {
                    if (shown >= 8) break;
                    shown++;
                    mainContent.Children.Add(MakeReviewRow(t));
                }
            }

            var foot = new TextBlock();
            foot.Text = "推荐的顺序：先看逾期 / 今天到期 / 重要度，再叠加你的高效时段和推迟次数。";
            foot.FontSize = 10;
            foot.Foreground = B(TextMuted);
            foot.Margin = new Thickness(2, 10, 0, 0);
            foot.TextWrapping = TextWrapping.Wrap;
            mainContent.Children.Add(foot);
        }

        static Border StatTile(string value, string label, string hint)
        {
            var b = new Border();
            b.CornerRadius = new CornerRadius(10);
            b.Background = B(CardBg);
            b.BorderBrush = B(LineCol);
            b.BorderThickness = new Thickness(1);
            b.Padding = new Thickness(10, 8, 10, 8);
            b.Margin = new Thickness(3);

            var sp = new StackPanel();
            var v = new TextBlock();
            v.Text = value;
            v.FontSize = 19;
            v.FontWeight = FontWeights.SemiBold;
            v.Foreground = B(TextMain);
            v.TextTrimming = TextTrimming.CharacterEllipsis;
            sp.Children.Add(v);

            var l = new TextBlock();
            l.Text = label;
            l.FontSize = 11;
            l.Foreground = B(TextSub);
            l.Margin = new Thickness(0, 2, 0, 0);
            sp.Children.Add(l);

            var h = new TextBlock();
            h.Text = hint;
            h.FontSize = 9.5;
            h.Foreground = B(TextMuted);
            h.Margin = new Thickness(0, 1, 0, 0);
            h.TextWrapping = TextWrapping.Wrap;
            sp.Children.Add(h);

            b.Child = sp;
            return b;
        }

        static Border MakeReviewRow(TaskItem t)
        {
            var b = new Border();
            b.CornerRadius = new CornerRadius(9);
            b.Background = B(CardBg);
            b.BorderBrush = B(LineCol);
            b.BorderThickness = new Thickness(1);
            b.Padding = new Thickness(10, 7, 10, 7);
            b.Margin = new Thickness(0, 0, 0, 5);

            var sp = new StackPanel();
            var title = new TextBlock();
            title.Text = t.title;
            title.FontSize = 12;
            title.Foreground = B(TextMain);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            sp.Children.Add(title);

            string plan = t.estimateMin > 0 ? ("预计 " + t.estimateMin + " 分") : "没估时";
            string actual = t.spentMs > 0 ? ("实际 " + FmtMinutes(t.spentMs)) : "没计时";
            string diff = "";
            if (t.estimateMin > 0 && t.spentMs > 0)
            {
                long d = t.spentMs / 60000 - t.estimateMin;
                if (d >= 1) diff = " · 多了 " + d + " 分";
                else if (d <= -1) diff = " · 提前 " + (-d) + " 分";
                else diff = " · 刚好";
            }
            string deadline = "";
            if (t.due.HasValue && t.doneAt > 0)
            {
                if (t.doneAt <= t.due.Value + 60000) deadline = " · 按时";
                else deadline = " · 晚了 " + AgoText((t.doneAt - t.due.Value) / 60000);
            }

            var sub = new TextBlock();
            sub.Text = plan + " → " + actual + diff + deadline;
            sub.FontSize = 10.5;
            sub.Foreground = B(TextMuted);
            sub.Margin = new Thickness(0, 2, 0, 0);
            sp.Children.Add(sub);

            b.Child = sp;
            return b;
        }

        // ---------------- 月历视图（v0.3：显示任务 + 翻月 + 点某天看当天清单）
        static DateTime calMonth = DateTime.MinValue;      // 正在显示的月份（该月 1 日）
        static DateTime calSelected = DateTime.MinValue;   // 选中的那一天

        static void EnsureCalState()
        {
            DateTime today = DateTime.Today;
            if (calMonth == DateTime.MinValue) calMonth = new DateTime(today.Year, today.Month, 1);
            if (calSelected == DateTime.MinValue) calSelected = today;
        }

        static long DayStartOf(DateTime d)
        {
            return new DateTimeOffset(d.Date).ToUnixTimeMilliseconds();
        }

        static List<TaskItem> TasksOnDay(DateTime day)
        {
            long s = DayStartOf(day);
            long e = s + 24L * 3600 * 1000;
            var list = new List<TaskItem>();
            foreach (var t in tasks)
            {
                if (!t.due.HasValue) continue;
                if (t.due.Value >= s && t.due.Value < e) list.Add(t);
            }
            list.Sort(delegate(TaskItem a, TaskItem b)
            {
                if (a.done != b.done) return a.done ? 1 : -1;   // 未完成在前
                int pa = PriorityWeight(a.priority), pb = PriorityWeight(b.priority);
                if (pa != pb) return pb.CompareTo(pa);
                return a.due.Value.CompareTo(b.due.Value);
            });
            return list;
        }

        static void BuildCalendarView()
        {
            EnsureCalState();
            DateTime today = DateTime.Today;

            // ---------------- 头部：上个月 / 当前月 / 下个月 / 今天
            // 第一行：月份（可点开选年月） + 回到今天 / 上个月 / 下个月
            var head = new Grid();
            head.Margin = new Thickness(2, 0, 0, 6);

            var title = new Button();
            title.Content = calMonth.ToString("yyyy 年 M 月", Inv) + "  ▾";
            title.FontSize = 15;
            title.FontWeight = FontWeights.SemiBold;
            title.Foreground = B(TextMain);
            title.Background = Brushes.Transparent;
            title.BorderThickness = new Thickness(0);
            title.Padding = new Thickness(0, 2, 6, 2);
            title.Cursor = Cursors.Hand;
            title.HorizontalAlignment = HorizontalAlignment.Left;
            title.VerticalAlignment = VerticalAlignment.Center;
            title.Template = MakeFlatButtonTemplate();
            title.ToolTip = "点这里选年份 / 月份";
            title.Click += delegate { ShowMonthPicker(); };
            head.Children.Add(title);

            var nav = new StackPanel();
            nav.Orientation = Orientation.Horizontal;
            nav.HorizontalAlignment = HorizontalAlignment.Right;
            nav.VerticalAlignment = VerticalAlignment.Center;

            var todayBtn = TinyButton("回到今天", AccentDeep);
            todayBtn.Click += delegate
            {
                calMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
                calSelected = DateTime.Today;
                RefreshMain();
            };
            nav.Children.Add(todayBtn);

            var prev = TinyButton("‹ 上个月", TextSub);
            prev.Click += delegate { calMonth = calMonth.AddMonths(-1); RefreshMain(); };
            nav.Children.Add(prev);

            var next = TinyButton("下个月 ›", TextSub);
            next.Click += delegate { calMonth = calMonth.AddMonths(1); RefreshMain(); };
            nav.Children.Add(next);

            head.Children.Add(nav);
            mainContent.Children.Add(head);

            // 第二行：显眼的「＋ 记在 10 月 24 日」+ 一句提示
            var bar2 = new StackPanel();
            bar2.Orientation = Orientation.Horizontal;
            bar2.Margin = new Thickness(2, 0, 0, 10);

            var addOnDay = FilledButton("＋ 记在 " + calSelected.ToString("M 月 d 日", Inv));
            addOnDay.Padding = new Thickness(16, 7, 16, 7);
            addOnDay.Margin = new Thickness(0, 0, 12, 0);
            addOnDay.FontSize = 12.5;
            addOnDay.VerticalAlignment = VerticalAlignment.Center;
            addOnDay.ToolTip = "在这一天记一件事（录入窗会预填日期）";
            addOnDay.Click += delegate { ShowPopupForDate(calSelected); };
            bar2.Children.Add(addOnDay);

            var hintPick = new TextBlock();
            hintPick.Text = "点日期选年月 · 点格子选中那一天";
            hintPick.FontSize = 10.5;
            hintPick.Foreground = B(TextMuted);
            hintPick.VerticalAlignment = VerticalAlignment.Center;
            bar2.Children.Add(hintPick);

            mainContent.Children.Add(bar2);

            // ---------------- 网格：星期行 + 6 周
            var grid = new Grid();
            for (int c = 0; c < 7; c++) grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
            for (int r = 0; r < 6; r++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(63) });

            string[] zh = { "一", "二", "三", "四", "五", "六", "日" };
            for (int c = 0; c < 7; c++)
            {
                var w = new TextBlock();
                w.Text = zh[c];
                w.FontSize = 11;
                w.Foreground = B(TextMuted);
                w.HorizontalAlignment = HorizontalAlignment.Center;
                Grid.SetRow(w, 0);
                Grid.SetColumn(w, c);
                grid.Children.Add(w);
            }

            int offset = ((int)calMonth.DayOfWeek + 6) % 7;   // 周一为第一列
            int daysInMonth = DateTime.DaysInMonth(calMonth.Year, calMonth.Month);

            for (int d = 1; d <= daysInMonth; d++)
            {
                DateTime day = new DateTime(calMonth.Year, calMonth.Month, d);
                int idx = offset + d - 1;
                int row = idx / 7 + 1;
                int col = idx % 7;
                bool isToday = day == today;
                bool isSel = day == calSelected;

                var cell = new Border();
                cell.CornerRadius = new CornerRadius(10);
                cell.Margin = new Thickness(2);
                cell.Padding = new Thickness(5, 3, 5, 3);
                if (isSel) cell.Background = B(AccentSoft);
                else if (isToday) cell.Background = B(AccentSoft);
                else cell.Background = B(CardBg);
                cell.BorderBrush = isSel ? B(AccentDeep) : (isToday ? B(AccentLine) : B(LineCol));
                cell.BorderThickness = new Thickness(isSel ? 2 : 1);
                cell.Cursor = Cursors.Hand;
                DateTime captured = day;
                cell.ToolTip = "点一下选中这一天";
                cell.MouseLeftButtonUp += delegate { calSelected = captured; RefreshMain(); };

                var stack = new StackPanel();

                var num = new TextBlock();
                num.Text = d.ToString();
                num.FontSize = 11.5;
                num.FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal;
                num.Foreground = B(isToday ? AccentDeep : TextMain);
                stack.Children.Add(num);

                var dayTasks = TasksOnDay(day);
                int shown = 0;
                foreach (var t in dayTasks)
                {
                    if (shown >= 2) break;
                    var chip = new TextBlock();
                    chip.Text = (t.done ? "✓ " : "") + t.title;
                    chip.FontSize = 9.5;
                    chip.TextTrimming = TextTrimming.CharacterEllipsis;
                    chip.Margin = new Thickness(0, 1, 0, 0);
                    chip.Foreground = B(t.done ? TextMuted : PriorityColor(t.priority));
                    if (t.done) chip.TextDecorations = TextDecorations.Strikethrough;
                    stack.Children.Add(chip);
                    shown++;
                }
                if (dayTasks.Count > shown)
                {
                    var more = new TextBlock();
                    more.Text = "+" + (dayTasks.Count - shown) + " 件";
                    more.FontSize = 9;
                    more.Foreground = B(TextMuted);
                    more.Margin = new Thickness(0, 1, 0, 0);
                    stack.Children.Add(more);
                }

                cell.Child = stack;
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, col);
                grid.Children.Add(cell);
            }

            mainContent.Children.Add(grid);

            // ---------------- 选中那天：列出全部任务
            var selHeadRow = new Grid();
            selHeadRow.Margin = new Thickness(2, 12, 0, 6);

            var selHead = new TextBlock();
            selHead.Text = calSelected.ToString("M 月 d 日", Inv) + "　" + WeekdayZh(calSelected);
            selHead.FontSize = 12;
            selHead.FontWeight = FontWeights.SemiBold;
            selHead.Foreground = B(TextSub);
            selHead.VerticalAlignment = VerticalAlignment.Center;
            selHeadRow.Children.Add(selHead);

            mainContent.Children.Add(selHeadRow);

            var selTasks = TasksOnDay(calSelected);
            if (selTasks.Count == 0)
            {
                var empty = new TextBlock();
                empty.Text = "这天没有任务";
                empty.FontSize = 11.5;
                empty.Foreground = B(TextMuted);
                empty.Margin = new Thickness(4, 0, 0, 0);
                mainContent.Children.Add(empty);
            }
            else
            {
                foreach (var t in selTasks)
                {
                    var row = new Border();
                    row.CornerRadius = new CornerRadius(9);
                    row.Background = B(CardBg);
                    row.BorderBrush = B(LineCol);
                    row.BorderThickness = new Thickness(1);
                    row.Padding = new Thickness(10, 7, 10, 7);
                    row.Margin = new Thickness(0, 0, 0, 6);

                    var g = new Grid();
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
                    g.ColumnDefinitions.Add(new ColumnDefinition());
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var bar = new Border();
                    bar.CornerRadius = new CornerRadius(2);
                    bar.Background = B(PriorityColor(t.priority));
                    bar.Margin = new Thickness(0, 0, 9, 0);
                    Grid.SetColumn(bar, 0);
                    g.Children.Add(bar);

                    var left = new StackPanel();
                    var tt = new TextBlock();
                    tt.Text = t.title;
                    tt.FontSize = 12.5;
                    tt.Foreground = B(t.done ? TextMuted : TextMain);
                    tt.TextTrimming = TextTrimming.CharacterEllipsis;
                    if (t.done) tt.TextDecorations = TextDecorations.Strikethrough;
                    left.Children.Add(tt);

                    var sub = new TextBlock();
                    sub.Text = DateTimeOffset.FromUnixTimeMilliseconds(t.due.Value).LocalDateTime.ToString("HH:mm", Inv)
                             + "　" + PriorityName(t.priority) + (t.done ? "　已完成" : "");
                    sub.FontSize = 10.5;
                    sub.Foreground = B(TextMuted);
                    sub.Margin = new Thickness(0, 2, 0, 0);
                    left.Children.Add(sub);
                    Grid.SetColumn(left, 1);
                    g.Children.Add(left);

                    var btns = new StackPanel();
                    btns.Orientation = Orientation.Horizontal;
                    btns.VerticalAlignment = VerticalAlignment.Center;

                    if (!t.done)
                    {
                        var taskRef = t;
                        var editBtn = TinyButton("编辑", AccentDeep);
                        editBtn.Click += delegate { ShowEditTaskDialog(taskRef); };
                        btns.Children.Add(editBtn);

                        var moveBtn = TinyButton("改期", TextSub);
                        moveBtn.Click += delegate { ShowRescheduleMenu(moveBtn, taskRef); };
                        btns.Children.Add(moveBtn);

                        var doneBtn = TinyButton("完成", AccentDeep);
                        doneBtn.Click += delegate
                        {
                            taskRef.done = true;
                            taskRef.doneAt = TaskItem.Now();
                            SaveTasks();
                            UpdateSummary();
                            RefreshMain();
                        };
                        btns.Children.Add(doneBtn);
                    }
                    else
                    {
                        var taskRef2 = t;
                        var undoBtn = TinyButton("撤销", TextSub);
                        undoBtn.Click += delegate
                        {
                            taskRef2.done = false;
                            taskRef2.doneAt = 0;
                            SaveTasks();
                            UpdateSummary();
                            RefreshMain();
                        };
                        btns.Children.Add(undoBtn);
                    }

                    var delBtn = TinyButton("删除", "#C47B6D");
                    delBtn.Click += delegate
                    {
                        tasks.Remove(t);
                        SaveTasks();
                        UpdateSummary();
                        RefreshMain();
                    };
                    btns.Children.Add(delBtn);

                    Grid.SetColumn(btns, 2);
                    g.Children.Add(btns);

                    row.Child = g;
                    mainContent.Children.Add(row);
                }
            }
        }

        // 年月选择：年份可加减，12 个月一格一点
        static void ShowMonthPicker()
        {
            try
            {
                int[] year = { calMonth.Year };
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
                card.Padding = new Thickness(14, 12, 14, 12);
                var root = new StackPanel();
                card.Child = root;

                // 年份行
                var yrRow = new StackPanel();
                yrRow.Orientation = Orientation.Horizontal;
                yrRow.Margin = new Thickness(0, 0, 0, 10);

                var yrText = new TextBlock();
                yrText.FontSize = 13;
                yrText.FontWeight = FontWeights.SemiBold;
                yrText.Foreground = B(TextMain);
                yrText.VerticalAlignment = VerticalAlignment.Center;
                yrText.MinWidth = 78;
                yrText.TextAlignment = TextAlignment.Center;

                var prevY = TinyButton("‹", TextSub);
                prevY.FontSize = 14;
                prevY.Padding = new Thickness(10, 0, 10, 2);
                var nextY = TinyButton("›", TextSub);
                nextY.FontSize = 14;
                nextY.Padding = new Thickness(10, 0, 10, 2);

                yrRow.Children.Add(prevY);
                yrRow.Children.Add(yrText);
                yrRow.Children.Add(nextY);

                var thisYearBtn = TinyButton("今年", AccentDeep);
                thisYearBtn.Margin = new Thickness(8, 0, 0, 0);
                thisYearBtn.Click += delegate { year[0] = DateTime.Today.Year; yrText.Text = year[0] + " 年"; };
                yrRow.Children.Add(thisYearBtn);
                root.Children.Add(yrRow);

                // 12 个月
                var grid = new System.Windows.Controls.Primitives.UniformGrid();
                grid.Columns = 4;
                grid.Rows = 3;
                for (int m = 1; m <= 12; m++)
                {
                    int mm = m;
                    bool on = (year[0] == calMonth.Year && mm == calMonth.Month);
                    var b = SegPill(mm + " 月", on);
                    b.Margin = new Thickness(3);
                    b.MinWidth = 62;
                    b.Click += delegate
                    {
                        calMonth = new DateTime(year[0], mm, 1);
                        DateTime today = DateTime.Today;
                        calSelected = (year[0] == today.Year && mm == today.Month) ? today : new DateTime(year[0], mm, 1);
                        try { w.Close(); } catch { }
                        RefreshMain();
                    };
                    grid.Children.Add(b);
                }
                root.Children.Add(grid);

                yrText.Text = year[0] + " 年";
                prevY.Click += delegate
                {
                    year[0]--;
                    yrText.Text = year[0] + " 年";
                    // 年份变了以后，把"选中月"的高亮重算一遍（重建整窗最省事）
                    try { w.Close(); } catch { }
                    calMonth = new DateTime(year[0], calMonth.Month, 1);
                    calSelected = new DateTime(year[0], calMonth.Month, Math.Min(calSelected.Day, DateTime.DaysInMonth(year[0], calMonth.Month)));
                    ShowMonthPicker();
                };
                nextY.Click += delegate
                {
                    year[0]++;
                    yrText.Text = year[0] + " 年";
                    try { w.Close(); } catch { }
                    calMonth = new DateTime(year[0], calMonth.Month, 1);
                    calSelected = new DateTime(year[0], calMonth.Month, Math.Min(calSelected.Day, DateTime.DaysInMonth(year[0], calMonth.Month)));
                    ShowMonthPicker();
                };

                var cancel = TinyButton("取消", TextSub);
                cancel.HorizontalAlignment = HorizontalAlignment.Right;
                cancel.Margin = new Thickness(0, 6, 0, 0);
                cancel.Click += delegate { try { w.Close(); } catch { } };
                root.Children.Add(cancel);

                w.Content = card;
                w.Show();
                w.Activate();
            }
            catch (Exception ex) { StartupLog("年月选择窗失败: " + ex.Message); }
        }

        static string WeekdayZh(DateTime d)
        {
            string[] zh = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
            return zh[(int)d.DayOfWeek];
        }

        // ---------------- 设置视图
        static void BuildSettingsView()
        {
            mainContent.Children.Add(SectionTitle("快捷键"));

            var hkRow = new StackPanel();
            hkRow.Orientation = Orientation.Horizontal;
            hkRow.Margin = new Thickness(0, 0, 0, 4);
            var hkValue = new TextBlock();
            hkValue.Text = hotkeyName;
            hkValue.FontSize = 20;
            hkValue.FontWeight = FontWeights.SemiBold;
            hkValue.Foreground = B(TextMain);
            hkValue.VerticalAlignment = VerticalAlignment.Center;
            hkRow.Children.Add(hkValue);

            var changeBtn = TinyButton("修改", AccentDeep);
            changeBtn.Margin = new Thickness(14, 0, 0, 0);
            changeBtn.VerticalAlignment = VerticalAlignment.Center;
            changeBtn.Click += delegate { OpenHotkeyPicker(); };
            hkRow.Children.Add(changeBtn);
            mainContent.Children.Add(hkRow);

            mainContent.Children.Add(SectionHint("点「修改」后按下你想要的组合，回车保存"));

            // ---------------- 启动：开机自启（v0.8）
            mainContent.Children.Add(SectionTitle("启动"));
            var startupRow = new WrapPanel();
            startupRow.Margin = new Thickness(0, 4, 0, 0);
            bool autoStartOn = IsAutoStartOn();
            startupRow.Children.Add(Pill(autoStartOn ? "开机自启（登录后静默运行）" : "开机自启（当前关闭，点一下开启）", autoStartOn, delegate
            {
                bool want = !IsAutoStartOn();
                if (!SetAutoStart(want))
                {
                    ShowToast("开机自启设置失败", "无法写入 HKCU 的 Run 项，检查安全软件是否拦截");
                    return;
                }
                settings.autoStart = want ? 1 : 0;
                SaveSettings();
                RefreshMain();
                ShowToast(want ? "已设置开机自启" : "已关闭开机自启",
                    want ? "登录后静默运行；按 " + hotkeyName + " 记一件事" : "下次登录不再自动启动");
            }));
            mainContent.Children.Add(startupRow);
            mainContent.Children.Add(SectionHint("当前：" + (autoStartOn ? "已开启" : "已关闭") + " · 只写当前用户的 Run 项，不需要管理员；移动 exe 后下次启动会自动更新路径。"));

            // ---------------- 默认截止时间
            mainContent.Children.Add(SectionTitle("默认时间"));
            mainContent.Children.Add(SectionHint("按快捷键唤出录入窗时预选这个时间：写完标题直接回车就存进清单。"));

            var presets = new WrapPanel();
            presets.Margin = new Thickness(0, 4, 0, 0);
            AddAutoChip(presets, "不预设", "none", 0);
            AddAutoChip(presets, "今天 23:59", "today", 0);
            AddAutoChip(presets, "明天 21:00", "tomorrow", 0);
            AddAutoChip(presets, "5 分钟后", "in5", 0);
            AddAutoChip(presets, "10 分钟后", "in10", 0);
            AddAutoChip(presets, "延时 " + (settings.autoDueMinutes > 0 ? settings.autoDueMinutes : 30) + " 分钟", "delay", 0);
            mainContent.Children.Add(presets);

            var delayRow = new StackPanel();
            delayRow.Orientation = Orientation.Horizontal;
            delayRow.Margin = new Thickness(2, 8, 0, 0);

            var dl = new TextBlock();
            dl.Text = "延时";
            dl.FontSize = 12;
            dl.Foreground = B(TextSub);
            dl.VerticalAlignment = VerticalAlignment.Center;
            dl.Width = 32;
            delayRow.Children.Add(dl);

            autoDelayBox = new TextBox();
            autoDelayBox.Width = 48;
            autoDelayBox.FontSize = 12;
            autoDelayBox.Padding = new Thickness(6, 3, 6, 3);
            autoDelayBox.TextAlignment = TextAlignment.Center;
            autoDelayBox.Text = (settings.autoDueMinutes > 0 ? settings.autoDueMinutes : 30).ToString();
            autoDelayBox.Background = B(CardBg);
            autoDelayBox.BorderBrush = B(LineCol);
            autoDelayBox.BorderThickness = new Thickness(1);
            autoDelayBox.Foreground = B(TextMain);
            delayRow.Children.Add(autoDelayBox);

            var dmin = new TextBlock();
            dmin.Text = "  分钟后提醒";
            dmin.FontSize = 12;
            dmin.Foreground = B(TextSub);
            dmin.VerticalAlignment = VerticalAlignment.Center;
            delayRow.Children.Add(dmin);

            var setDelay = TinyButton("设为默认", AccentDeep);
            setDelay.Margin = new Thickness(10, 0, 0, 0);
            setDelay.Padding = new Thickness(10, 3, 10, 3);
            setDelay.Click += delegate
            {
                int n = 0;
                if (!int.TryParse((autoDelayBox.Text ?? "").Trim(), out n) || n <= 0) return;
                settings.autoDue = "delay";
                settings.autoDueMinutes = n;
                SaveSettings();
                RefreshMain();
            };
            delayRow.Children.Add(setDelay);
            mainContent.Children.Add(delayRow);

            mainContent.Children.Add(SectionHint("当前默认：" + AutoDueText()));

            // ---------------- 主题：先选风格（整体气质），再选这个风格下的颜色
            mainContent.Children.Add(SectionTitle("主题"));
            mainContent.Children.Add(SectionHint("先选风格，再选这个风格里的颜色。"));

            var styleRow = new WrapPanel();
            styleRow.Margin = new Thickness(0, 4, 0, 0);
            for (int i = 0; i < StyleNames.Length; i++)
            {
                int idx = i;
                var b = Pill(StyleNames[i], StyleIndex() == i, delegate
                {
                    settings.style = idx;
                    settings.theme = 0;      // 换风格先落到该风格的第一种颜色
                    SaveSettings();
                    ApplyTheme();
                    RefreshMain();
                });
                styleRow.Children.Add(b);
            }
            mainContent.Children.Add(styleRow);

            var colorRow = new WrapPanel();
            colorRow.Margin = new Thickness(0, 6, 0, 0);
            var palette = Styles[StyleIndex()];
            for (int i = 0; i < palette.Length; i++)
            {
                int idx = i;
                bool on = ColorIndex() == i;
                var cell = new StackPanel();
                cell.Margin = new Thickness(0, 0, 12, 0);
                cell.Cursor = Cursors.Hand;

                var sw = new Border();
                sw.Width = 44;
                sw.Height = 44;
                sw.CornerRadius = new CornerRadius(12);
                sw.Background = B(palette[i][1]);
                sw.BorderThickness = new Thickness(on ? 3 : 1);
                sw.BorderBrush = on ? B(AccentDeep) : B(LineCol);
                sw.Child = Deco.Make(StyleIndex(), i, 26, "#FFFFFF", 0.92);
                sw.ToolTip = palette[i][0];
                cell.Children.Add(sw);

                var nm = new TextBlock();
                nm.Text = (on ? "✓ " : "") + palette[i][0];
                nm.FontSize = 11;
                nm.Foreground = on ? B(AccentDeep) : B(TextMuted);
                nm.HorizontalAlignment = HorizontalAlignment.Center;
                nm.Margin = new Thickness(0, 5, 0, 0);
                cell.Children.Add(nm);

                cell.MouseLeftButtonUp += delegate
                {
                    settings.theme = idx;
                    SaveSettings();
                    ApplyTheme();
                    RefreshMain();
                };
                colorRow.Children.Add(cell);
            }
            mainContent.Children.Add(colorRow);
            mainContent.Children.Add(SectionHint("当前主题：" + CurStyleName() + " · " + ThemeName));

            // ---------------- 智能助手（v0.4 DeepSeek）
            mainContent.Children.Add(SectionTitle("智能助手（DeepSeek）"));
            mainContent.Children.Add(SectionHint("填了 API Key 才会联网；不填也能用，所有 AI 功能自动退回本地规则。"));

            var keyRow = new StackPanel();
            keyRow.Orientation = Orientation.Horizontal;
            keyRow.Margin = new Thickness(2, 6, 0, 0);

            var keyLabel = new TextBlock();
            keyLabel.Text = "Key";
            keyLabel.FontSize = 12;
            keyLabel.Foreground = B(TextSub);
            keyLabel.Width = 32;
            keyLabel.VerticalAlignment = VerticalAlignment.Center;
            keyRow.Children.Add(keyLabel);

            autoKeyBox = new PasswordBox();
            autoKeyBox.Width = 260;
            autoKeyBox.FontSize = 12;
            autoKeyBox.Padding = new Thickness(7, 4, 7, 4);
            autoKeyBox.Background = B(CardBg);
            autoKeyBox.Foreground = B(TextMain);
            autoKeyBox.BorderBrush = B(LineCol);
            autoKeyBox.BorderThickness = new Thickness(1);
            if (!string.IsNullOrEmpty(settings.aiKey)) autoKeyBox.Password = settings.aiKey;
            keyRow.Children.Add(autoKeyBox);
            mainContent.Children.Add(keyRow);

            var modelRow = new StackPanel();
            modelRow.Orientation = Orientation.Horizontal;
            modelRow.Margin = new Thickness(2, 6, 0, 0);

            var mLab = new TextBlock();
            mLab.Text = "模型";
            mLab.FontSize = 12;
            mLab.Foreground = B(TextSub);
            mLab.Width = 32;
            mLab.VerticalAlignment = VerticalAlignment.Center;
            modelRow.Children.Add(mLab);

            autoModelBox = new TextBox();
            autoModelBox.Width = 150;
            autoModelBox.FontSize = 12;
            autoModelBox.Padding = new Thickness(7, 4, 7, 4);
            autoModelBox.Background = B(CardBg);
            autoModelBox.Foreground = B(TextMain);
            autoModelBox.BorderBrush = B(LineCol);
            autoModelBox.BorderThickness = new Thickness(1);
            autoModelBox.Text = string.IsNullOrEmpty(settings.aiModel) ? "deepseek-flash" : settings.aiModel;
            modelRow.Children.Add(autoModelBox);

            var baseLab = new TextBlock();
            baseLab.Text = "  地址";
            baseLab.FontSize = 12;
            baseLab.Foreground = B(TextSub);
            baseLab.VerticalAlignment = VerticalAlignment.Center;
            modelRow.Children.Add(baseLab);

            autoBaseBox = new TextBox();
            autoBaseBox.Width = 210;
            autoBaseBox.FontSize = 12;
            autoBaseBox.Padding = new Thickness(7, 4, 7, 4);
            autoBaseBox.Background = B(CardBg);
            autoBaseBox.Foreground = B(TextMain);
            autoBaseBox.BorderBrush = B(LineCol);
            autoBaseBox.BorderThickness = new Thickness(1);
            autoBaseBox.Text = string.IsNullOrEmpty(settings.aiBase) ? "https://api.deepseek.com" : settings.aiBase;
            modelRow.Children.Add(autoBaseBox);
            mainContent.Children.Add(modelRow);

            var aiBtnRow = new StackPanel();
            aiBtnRow.Orientation = Orientation.Horizontal;
            aiBtnRow.Margin = new Thickness(2, 8, 0, 0);

            var saveKey = TinyButton("保存 Key", AccentDeep);
            saveKey.Padding = new Thickness(10, 3, 10, 3);
            saveKey.Click += delegate
            {
                settings.aiKey = (autoKeyBox.Password ?? "").Trim();
                settings.aiModel = (autoModelBox.Text ?? "").Trim();
                settings.aiBase = (autoBaseBox.Text ?? "").Trim();
                SaveSettings();
                RefreshMain();
            };
            aiBtnRow.Children.Add(saveKey);

            var testKey = TinyButton("测试连接", AccentDeep);
            testKey.Padding = new Thickness(10, 3, 10, 3);
            testKey.Margin = new Thickness(8, 0, 0, 0);
            testKey.Click += delegate
            {
                settings.aiKey = (autoKeyBox.Password ?? "").Trim();
                settings.aiModel = (autoModelBox.Text ?? "").Trim();
                settings.aiBase = (autoBaseBox.Text ?? "").Trim();
                SaveSettings();
                aiStatus.Text = "正在连接…";
                StartAiPing(aiStatus);
            };
            aiBtnRow.Children.Add(testKey);
            mainContent.Children.Add(aiBtnRow);

            aiStatus = new TextBlock();
            aiStatus.FontSize = 11;
            aiStatus.Foreground = B(TextMuted);
            aiStatus.Margin = new Thickness(2, 6, 0, 0);
            aiStatus.TextWrapping = TextWrapping.Wrap;
            aiStatus.Text = AiReady() ? ("已配置 · 模型 " + AiModelName()) : "未配置（AI 功能关闭，走本地规则）";
            mainContent.Children.Add(aiStatus);

            var aiToggles = new WrapPanel();
            aiToggles.Margin = new Thickness(0, 8, 0, 0);
            aiToggles.Children.Add(Pill("录入窗显示「AI 拆解」", AiDecomposeOn(), delegate
            {
                settings.aiDecompose = settings.aiDecompose != 0 ? 0 : 1;
                SaveSettings();
                RefreshMain();
            }));
            aiToggles.Children.Add(Pill("让 AI 决定稍后时长", AiSnoozeOn(), delegate
            {
                settings.aiSnooze = settings.aiSnooze != 0 ? 0 : 1;
                SaveSettings();
                RefreshMain();
            }));
            aiToggles.Children.Add(Pill("清单页用「AI 帮我选」", settings.aiPick != 0, delegate
            {
                settings.aiPick = settings.aiPick != 0 ? 0 : 1;
                SaveSettings();
                RefreshMain();
            }));
            aiToggles.Children.Add(Pill("AI 安排提醒时间", settings.aiPlan != 0, delegate
            {
                settings.aiPlan = settings.aiPlan != 0 ? 0 : 1;
                SaveSettings();
                if (settings.aiPlan != 0) EnsurePlanFresh(true);
                else pushPlan = null;
                RefreshMain();
            }));
            mainContent.Children.Add(aiToggles);

            var habitHint = new TextBlock();
            habitHint.Text = "AI 会参考你的完成时段、实际用时和推迟次数；只在点按钮 / 排提醒时调用，失败自动退回本地规则。";
            habitHint.FontSize = 10.5;
            habitHint.Foreground = B(TextMuted);
            habitHint.TextWrapping = TextWrapping.Wrap;
            habitHint.Margin = new Thickness(2, 4, 0, 0);
            mainContent.Children.Add(habitHint);

            var planHint = new TextBlock();
            planHint.Text = "「AI 安排提醒时间」会在截止前挑时间点提醒（23:00–07:00 默认不打扰，高优先且 3 小时内截止例外），到期后汇总没完成的任务；关掉就回到固定时间提醒。";
            planHint.FontSize = 10.5;
            planHint.Foreground = B(TextMuted);
            planHint.TextWrapping = TextWrapping.Wrap;
            planHint.Margin = new Thickness(2, 3, 0, 0);
            mainContent.Children.Add(planHint);

            var pinTitle = new TextBlock();
            pinTitle.Text = "AI 排提醒优先任务";
            pinTitle.FontSize = 11.5;
            pinTitle.Foreground = B(TextSub);
            pinTitle.Margin = new Thickness(2, 8, 0, 2);
            mainContent.Children.Add(pinTitle);

            var pinBox = new ComboBox();
            pinBox.Width = 320;
            pinBox.HorizontalAlignment = HorizontalAlignment.Left;
            pinBox.FontSize = 11.5;
            pinBox.Margin = new Thickness(2, 0, 0, 1);
            pinBox.Background = B(CardBg);
            pinBox.Foreground = B(TextMain);
            pinBox.BorderBrush = B(LineCol);
            var pinNone = new ComboBoxItem();
            pinNone.Content = "不指定（按 AI / 惯例）";
            pinNone.Tag = "";
            pinBox.Items.Add(pinNone);
            int pinSel = 0;
            foreach (var t in tasks)
            {
                if (t == null || t.done) continue;
                var it = new ComboBoxItem();
                it.Content = t.title;
                it.Tag = t.id;
                pinBox.Items.Add(it);
                if (t.id == settings.pinTaskId) pinSel = pinBox.Items.Count - 1;
            }
            pinBox.SelectedIndex = pinSel;
            pinBox.SelectionChanged += delegate
            {
                var it = pinBox.SelectedItem as ComboBoxItem;
                settings.pinTaskId = (it == null || it.Tag == null) ? "" : it.Tag.ToString();
                SaveSettings();
                EnsurePlanFresh(true);
                RefreshMain();
            };
            mainContent.Children.Add(pinBox);

            var pinHint = new TextBlock();
            pinHint.Text = "只影响 AI 排提醒和「现在做哪件」的先后，不改变任务重要度；这条任务完成后自动跳过。";
            pinHint.FontSize = 10.5;
            pinHint.Foreground = B(TextMuted);
            pinHint.TextWrapping = TextWrapping.Wrap;
            pinHint.Margin = new Thickness(2, 3, 0, 0);
            mainContent.Children.Add(pinHint);

            mainContent.Children.Add(SectionTitle("数据"));

            var dir = new TextBlock();
            dir.Text = dataDir;
            dir.FontSize = 11;
            dir.Foreground = B(TextSub);
            dir.TextWrapping = TextWrapping.Wrap;
            mainContent.Children.Add(dir);

            var openDir = TinyButton("打开数据文件夹", AccentDeep);
            openDir.Margin = new Thickness(0, 8, 0, 0);
            openDir.HorizontalAlignment = HorizontalAlignment.Left;
            openDir.Click += delegate { try { System.Diagnostics.Process.Start("explorer.exe", dataDir); } catch { } };
            mainContent.Children.Add(openDir);

            // ---------------- 手机 / 同步（放在底部，连接信息默认收起）
            mainContent.Children.Add(SectionTitle("手机 / 同步"));
            bool mobileOn = settings.mobileOn != 0;
            var mobileRow = new WrapPanel();
            mobileRow.Margin = new Thickness(0, 4, 0, 0);
            mobileRow.Children.Add(Pill(mobileOn ? "手机同步已开启" : "手机同步已关闭", mobileOn,
                delegate { ToggleMobileSync(); }));
            var connect = TinyButton(mobileDetailsOpen ? "收起连接信息" : "连接手机", AccentDeep);
            connect.Margin = new Thickness(0, 0, 7, 7);
            connect.Click += delegate { mobileDetailsOpen = !mobileDetailsOpen; RefreshMain(); };
            mobileRow.Children.Add(connect);
            mainContent.Children.Add(mobileRow);
            mainContent.Children.Add(SectionHint(mobileOn
                ? "已开启；点「连接手机」查看地址和令牌，手机和电脑用 Tailscale 或同一 Wi-Fi。"
                : "手机同步当前关闭；点左边开关开启，或点「连接手机」查看配置。"));

            if (mobileDetailsOpen)
            {
                var urlLabel = new TextBlock();
                urlLabel.Text = "手机 App 填这个地址（任选一行）：";
                urlLabel.FontSize = 11;
                urlLabel.Foreground = B(TextSub);
                urlLabel.Margin = new Thickness(2, 6, 0, 3);
                mainContent.Children.Add(urlLabel);

                var urlBox = new TextBox();
                urlBox.IsReadOnly = true;
                urlBox.AcceptsReturn = true;
                urlBox.TextWrapping = TextWrapping.Wrap;
                urlBox.Height = 52;
                urlBox.FontSize = 11.5;
                urlBox.Padding = new Thickness(7, 4, 7, 4);
                urlBox.Background = B(CardBg);
                urlBox.Foreground = B(TextMain);
                urlBox.BorderBrush = B(LineCol);
                urlBox.BorderThickness = new Thickness(1);
                urlBox.Text = MobileUrlText();
                mainContent.Children.Add(urlBox);

                var tokenRow = new StackPanel();
                tokenRow.Orientation = Orientation.Horizontal;
                tokenRow.Margin = new Thickness(2, 8, 0, 0);
                var tokenLabel = new TextBlock();
                tokenLabel.Text = "令牌";
                tokenLabel.FontSize = 11.5;
                tokenLabel.Foreground = B(TextSub);
                tokenLabel.VerticalAlignment = VerticalAlignment.Center;
                tokenLabel.Width = 38;
                tokenRow.Children.Add(tokenLabel);

                var tokenBox = new TextBox();
                tokenBox.IsReadOnly = true;
                tokenBox.Width = 210;
                tokenBox.FontSize = 11.5;
                tokenBox.Padding = new Thickness(7, 4, 7, 4);
                tokenBox.Background = B(CardBg);
                tokenBox.Foreground = B(TextMain);
                tokenBox.BorderBrush = B(LineCol);
                tokenBox.BorderThickness = new Thickness(1);
                tokenBox.Text = "••••••••••••";
                tokenRow.Children.Add(tokenBox);

                var showToken = TinyButton("显示令牌", TextSub);
                showToken.Margin = new Thickness(8, 0, 0, 0);
                bool tokenShown = false;
                showToken.Click += delegate
                {
                    tokenShown = !tokenShown;
                    tokenBox.Text = tokenShown ? settings.mobileToken : "••••••••••••";
                    showToken.Content = tokenShown ? "隐藏令牌" : "显示令牌";
                };
                tokenRow.Children.Add(showToken);

                var copy = TinyButton("复制", AccentDeep);
                copy.Margin = new Thickness(8, 0, 0, 0);
                copy.Click += delegate
                {
                    try
                    {
                        Clipboard.SetText(MobileUrlText() + "\n令牌: " + settings.mobileToken);
                        ShowToast("已复制", "发到手机，或直接在手机 App 里填写");
                    }
                    catch (Exception ex) { ShowToast("复制失败", ex.Message); }
                };
                tokenRow.Children.Add(copy);
                mainContent.Children.Add(tokenRow);

                var regen = TinyButton("重新生成令牌", TextSub);
                regen.Margin = new Thickness(2, 8, 0, 0);
                regen.HorizontalAlignment = HorizontalAlignment.Left;
                regen.Click += delegate { RegenerateMobileToken(); };
                mainContent.Children.Add(regen);

                mobileStatus = new TextBlock();
                mobileStatus.FontSize = 10.5;
                mobileStatus.Foreground = B(TextMuted);
                mobileStatus.TextWrapping = TextWrapping.Wrap;
                mobileStatus.Margin = new Thickness(2, 6, 0, 0);
                string statusText;
                if (!mobileOn && !string.IsNullOrEmpty(mobileStartError)) statusText = "上次启动失败：" + mobileStartError;
                else if (!mobileOn) statusText = "当前关闭 · 开启后手机才能连电脑；不会把 API Key 发给手机。";
                else if (mobileRunning) statusText = "已启动 · 端口 " + MobilePort() + " · 手机与电脑需在同一 Wi-Fi 或 Tailscale。";
                else statusText = "启动失败：" + (string.IsNullOrEmpty(mobileStartError) ? "未知错误" : mobileStartError);
                mobileStatus.Text = statusText;
                mainContent.Children.Add(mobileStatus);

                mainContent.Children.Add(SectionHint("首次连接若失败，在 Windows 防火墙里允许「ADHD-lite」的专用网络访问；Tailscale 地址是 100.x。"));
            }

            mainContent.Children.Add(SectionTitle("关于"));
            var about = new TextBlock();
            about.Text = "ADHD-lite · 构建 " + BuildStamp() + " · 热键 " + hotkeyName;
            about.FontSize = 11;
            about.Foreground = B(TextMuted);
            mainContent.Children.Add(about);
        }

        static TextBlock SectionTitle(string text)
        {
            var t = new TextBlock();
            t.Text = text;
            t.FontSize = 12;
            t.FontWeight = FontWeights.SemiBold;
            t.Foreground = B(TextSub);
            t.Margin = new Thickness(2, 18, 0, 8);
            return t;
        }

        static TextBlock SectionHint(string text)
        {
            var t = new TextBlock();
            t.Text = text;
            t.FontSize = 11;
            t.Foreground = B(TextMuted);
            t.Margin = new Thickness(2, 2, 0, 0);
            return t;
        }

        // ---------------- 默认时间 / 主题：预设胶囊按钮
        static TextBox autoDelayBox;
        static PasswordBox autoKeyBox;
        static TextBox autoModelBox, autoBaseBox;
        static TextBlock aiStatus;
        static TextBlock mobileStatus;
        static bool mobileDetailsOpen = false;

        // 选中态用"深主色实心 + ✓ + 加粗"，且鼠标移出不会被抹掉
        // （以前用 TinyButton，它的 MouseLeave 会把 Background 设成透明，选中色一闪就没了）
        static Button Pill(string text, bool on, Action onClick)
        {
            var b = new Button();
            b.Content = (on ? "✓ " : "") + text;
            b.FontSize = 11.5;
            b.Padding = new Thickness(11, 4, 11, 4);
            b.Margin = new Thickness(0, 0, 7, 7);
            b.Cursor = Cursors.Hand;
            b.Template = MakeFlatButtonTemplate();
            b.Background = on ? B(AccentDeep) : B(CardBg);
            b.Foreground = on ? B("#FBFDFC") : B(TextSub);
            b.BorderThickness = new Thickness(1);
            b.BorderBrush = on ? B(AccentDeep) : B(LineCol);
            b.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            b.MouseEnter += delegate { if (!on) b.Background = B(AccentSoft); };
            b.MouseLeave += delegate { if (!on) b.Background = B(CardBg); };
            if (onClick != null) b.Click += delegate { onClick(); };
            return b;
        }

        static void AddAutoChip(Panel host, string text, string kind, int minutes)
        {
            var b = Pill(text, settings.autoDue == kind, delegate
            {
                settings.autoDue = kind;
                if (minutes > 0) settings.autoDueMinutes = minutes;
                SaveSettings();
                RefreshMain();
            });
            host.Children.Add(b);
        }

        // 柔和的竖向滚动条：没有箭头、没有凹槽，只有一条圆角细条（颜色跟主题走）
        static Style MakeSoftScrollBarStyle()
        {
            try
            {
                string thumb = Accent;
                string trackBg = "Transparent";
                string xaml =
                    "<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" "
                    + "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" TargetType=\"ScrollBar\">"
                    + "<Setter Property=\"Width\" Value=\"9\"/>"
                    + "<Setter Property=\"Background\" Value=\"" + trackBg + "\"/>"
                    + "<Setter Property=\"Focusable\" Value=\"False\"/>"
                    + "<Setter Property=\"Template\"><Setter.Value>"
                    + "<ControlTemplate TargetType=\"ScrollBar\">"
                    + "<Grid Background=\"Transparent\">"
                    + "<Track x:Name=\"PART_Track\" IsDirectionReversed=\"True\" Orientation=\"Vertical\">"
                    + "<Track.Thumb><Thumb>"
                    + "<Thumb.Template><ControlTemplate TargetType=\"Thumb\">"
                    + "<Border x:Name=\"bd\" CornerRadius=\"4\" Margin=\"2\" Background=\"" + thumb + "\" Opacity=\"0.45\"/>"
                    + "<ControlTemplate.Triggers>"
                    + "<Trigger Property=\"IsMouseOver\" Value=\"True\">"
                    + "<Setter TargetName=\"" + "bd" + "\" Property=\"Opacity\" Value=\"0.85\"/>"
                    + "</Trigger>"
                    + "</ControlTemplate.Triggers>"
                    + "</ControlTemplate></Thumb.Template>"
                    + "</Thumb></Track.Thumb>"
                    + "</Track>"
                    + "</Grid>"
                    + "</ControlTemplate>"
                    + "</Setter.Value></Setter>"
                    + "</Style>";
                return (Style)System.Windows.Markup.XamlReader.Parse(xaml);
            }
            catch (Exception ex)
            {
                StartupLog("滚动条样式失败: " + ex.Message);
                return null;
            }
        }

        // 换主题时把主界面骨架刷一遍（侧栏、品牌字、加号按钮、标题、叉号、卡片边框）
        static void RepaintMainChrome()
        {
            try
            {
                if (mainCard != null) { mainCard.Background = B(AccentBg); mainCard.BorderBrush = B(LineCol); }
                if (mainSide != null) { mainSide.Background = B(SideBg); mainSide.BorderBrush = B(SideLine); }
                if (mainBrand != null) mainBrand.Foreground = B(SideBrand);
                if (mainSideLine != null) mainSideLine.Background = B(SideLine);
                if (mainAddBtn != null) mainAddBtn.Background = B(Accent);
                if (mainTitle != null) mainTitle.Foreground = B(TextMain);
                if (mainCloseX != null) mainCloseX.Foreground = B(TextMuted);
                if (sideDecoHost != null) sideDecoHost.Child = Deco.Make(StyleIndex(), ColorIndex(), 46, SideBrand, 0.6);
                if (mainTitleDecoHost != null) mainTitleDecoHost.Child = Deco.Make(StyleIndex(), ColorIndex(), 22, Accent, 0.9);
                if (settingsDeco != null) settingsDeco.Child = Deco.MakeScene(StyleIndex(), ColorIndex(), 210, AccentLine, 0.18, 1);
                if (navPanel != null)
                {
                    foreach (var child in navPanel.Children)
                    {
                        var nb = child as Button;
                        if (nb == null || nb.Tag == null) continue;
                        bool on = (string)nb.Tag == mainView;
                        nb.Background = on ? B(Accent) : Brushes.Transparent;
                        nb.Foreground = on ? B("#FBFDFC") : B(SideText);
                    }
                }
                if (mainScroll != null)
                {
                    var sb2 = MakeSoftScrollBarStyle();
                    if (sb2 != null) mainScroll.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = sb2;
                }
            }
            catch (Exception ex) { StartupLog("重刷主界面主题失败: " + ex.Message); }
        }

        // ---------------- 无边框窗口的"拖边缘缩放"
        // AllowsTransparency=True 时 WPF 自带的缩放不生效，只能自己铺 8 个透明热区
        static void EnableResize(Window w, Grid host)
        {
            AddGrip(w, host, -1, -1);
            AddGrip(w, host, 1, -1);
            AddGrip(w, host, -1, 1);
            AddGrip(w, host, 1, 1);
            AddGrip(w, host, -1, 0);
            AddGrip(w, host, 1, 0);
            AddGrip(w, host, 0, -1);
            AddGrip(w, host, 0, 1);
        }

        static void AddGrip(Window w, Grid host, int dx, int dy)
        {
            const double th = 6;
            var g = new Border();
            g.Background = Brushes.Transparent;
            g.Tag = "grip";   // AttachManualDrag 看到这个标记就不抢事件
            if (dx == 0)
            {
                g.Height = th;
                g.HorizontalAlignment = HorizontalAlignment.Stretch;
                g.VerticalAlignment = dy < 0 ? VerticalAlignment.Top : VerticalAlignment.Bottom;
                g.Cursor = Cursors.SizeNS;
            }
            else if (dy == 0)
            {
                g.Width = th;
                g.VerticalAlignment = VerticalAlignment.Stretch;
                g.HorizontalAlignment = dx < 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                g.Cursor = Cursors.SizeWE;
            }
            else
            {
                g.Width = th;
                g.Height = th;
                g.HorizontalAlignment = dx < 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                g.VerticalAlignment = dy < 0 ? VerticalAlignment.Top : VerticalAlignment.Bottom;
                g.Cursor = dx * dy > 0 ? Cursors.SizeNWSE : Cursors.SizeNESW;
            }

            bool dragging = false;
            Point startCursor = new Point();
            double startW = 0, startH = 0, startL = 0, startT = 0;

            g.PreviewMouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (e.ChangedButton != MouseButton.Left) return;
                startCursor = CursorInDip(w);
                startW = w.ActualWidth;
                startH = w.ActualHeight;
                startL = w.Left;
                startT = w.Top;
                dragging = true;
                g.CaptureMouse();
                e.Handled = true;
            };

            g.MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (!dragging) return;
                Point p = CursorInDip(w);
                ApplyResize(w, dx, dy, startL, startT, startW, startH, p.X - startCursor.X, p.Y - startCursor.Y);
            };

            g.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                if (!dragging) return;
                dragging = false;
                g.ReleaseMouseCapture();
            };

            Grid.SetColumnSpan(g, 2);
            host.Children.Add(g);
        }

        // 缩放数学独立出来：热区拖拽和 --ui-selftest 共用同一份逻辑
        static void ApplyResize(Window w, int dx, int dy, double startL, double startT,
                                double startW, double startH, double ddx, double ddy)
        {
            if (dx != 0)
            {
                double nw = Math.Max(w.MinWidth, dx > 0 ? startW + ddx : startW - ddx);
                w.Width = nw;
                if (dx < 0) w.Left = startL + (startW - nw);
            }
            if (dy != 0)
            {
                double nh = Math.Max(w.MinHeight, dy > 0 ? startH + ddy : startH - ddy);
                w.Height = nh;
                if (dy < 0) w.Top = startT + (startH - nh);
            }
        }
    }
}
