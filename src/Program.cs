// ADHD-lite  v0.1  (C# / WPF / .NET Framework 4.8)
// 编译：build.cmd （用系统自带 csc.exe，不需要装 SDK、不需要脚本宿主）
//
// 设计要点：
//  1. Shift+Tab 全局热键 → 无边框悬浮窗（RegisterHotKey + HwndSource 钩子）
//  2. 三个字段：任务名 / 截止（快捷词或自定义）/ 重要度
//  3. Enter 保存并隐藏；输入法上屏时不会误提交
//  4. 提醒分级 + 同一任务一天最多 2 次
//  5. 数据存 %APPDATA%\adhd-lite\tasks.json（可用 ADHD_HOME 改目录）
//
// 注意：本文件用 C# 5 语法（系统自带 csc 只支持到 C# 5），别用 $"..." 和 ?. 操作符。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace AdhdLite
{
    // ------------------------------------------------------------ 数据模型
    public class ReminderSlot
    {
        public long at { get; set; }
        public string label { get; set; }
        public bool fired { get; set; }
    }

    public class TaskItem
    {
        public string id { get; set; }
        public string title { get; set; }
        public string priority { get; set; }
        public long? due { get; set; }
        public bool done { get; set; }
        public long createdAt { get; set; }
        public List<ReminderSlot> reminders { get; set; }
        public string fireDay { get; set; }
        public int fireCount { get; set; }
        public string overdueDay { get; set; }
        public long doneAt { get; set; }

        // v0.5 执行闭环：开始计时 / 累计实际用时 / 预计用时 / 上次做到哪 / 被推迟次数
        public long startedAt { get; set; }      // >0 表示正在计时
        public long spentMs { get; set; }        // 已累计的实际用时
        public int estimateMin { get; set; }     // 0 = 还没估过
        public string lastNote { get; set; }     // 上次做到哪了
        public int snoozeCount { get; set; }
        public int postponeCount { get; set; }
        public string kind { get; set; }        // v0.7：writing / code / comms / errand / study / other
        public string resumeHint { get; set; }  // v0.7：AI 生成的"下一步"
        public long updatedAt { get; set; }     // v0.10：手机同步用

        public TaskItem()
        {
            id = Guid.NewGuid().ToString();
            title = "";
            priority = "mid";
            reminders = new List<ReminderSlot>();
            fireDay = "";
            overdueDay = "";
            lastNote = "";
            kind = "other";
            resumeHint = "";
            createdAt = Now();
        }

        public static long Now()
        {
            return DateTimeOffset.Now.ToUnixTimeMilliseconds();
        }
    }

    public class Store
    {
        public int version { get; set; }
        public List<TaskItem> tasks { get; set; }

        public Store()
        {
            version = 1;
            tasks = new List<TaskItem>();
        }
    }

    public class Settings
    {
        public int ver { get; set; }
        public uint mods { get; set; }
        public uint vk { get; set; }
        public string label { get; set; }
        public int theme { get; set; }
        public int style { get; set; }      // 主题风格：0 柔和 / 1 冷白 / 2 素雅
        // 默认截止时间：每次唤出录入窗时预选它（none / today / tomorrow / in5 / in10 / delay）
        public string autoDue { get; set; }
        public int autoDueMinutes { get; set; }
        // v0.4：DeepSeek
        public string aiKey { get; set; }
        public string aiBase { get; set; }
        public string aiModel { get; set; }
        public int aiDecompose { get; set; }   // 1 = 录入窗显示「拆解」
        public int aiSnooze { get; set; }      // 1 = 让 AI 建议稍后时长

        // v0.5：习惯学习
        public int aiPick { get; set; }        // 1 = 清单页允许「AI 帮我选」
        public int aiPlan { get; set; }        // 1 = 让 AI 排「截止前的间断提醒」
        public string lastInsight { get; set; }  // AI 复盘结论，会带进之后的提示词
        public long insightAt { get; set; }
        public string rules { get; set; }      // v0.7：AI 提炼的个人规则（换行分隔）
        public int autoStart { get; set; }     // v0.9：开机自启（HKCU Run）
        public int mobileOn { get; set; }      // v0.10：手机同步 API
        public int mobilePort { get; set; }
        public string mobileToken { get; set; }
        public string pinTaskId { get; set; }  // v0.11：AI 排提醒优先任务

        public Settings()
        {
            ver = 10;
            mods = 0x0002;   // MOD_CONTROL
            vk = 0x006B;     // VK_ADD —— 小键盘的 +
            label = "Ctrl+Num+";
            theme = 0;
            style = 0;
            autoDue = "none";
            autoDueMinutes = 30;
            aiKey = "";
            aiBase = "https://api.deepseek.com";
            aiModel = "deepseek-flash";
            aiDecompose = 1;
            aiSnooze = 0;
            aiPick = 1;
            aiPlan = 1;
            lastInsight = "";
            insightAt = 0;
            rules = "";
            autoStart = 0;
            mobileOn = 0;
            mobilePort = 17890;
            mobileToken = "";
            pinTaskId = "";
        }
    }

    // ------------------------------------------------------------ 原生 API
    internal static class Native
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int x, int y);
    }

    // ------------------------------------------------------------ 主程序
    public static partial class App
    {
        const uint MOD_ALT = 0x0001;
        const uint MOD_CONTROL = 0x0002;
        const uint MOD_SHIFT = 0x0004;
        const uint VK_TAB = 0x09;
        const uint VK_SPACE = 0x20;
        const int WM_HOTKEY = 0x0312;
        const int HOTKEY_ID = 1;
        const int HOTKEY_ID2 = 2;
        const long GRACE_MS = 10 * 60 * 1000;
        const int DAILY_LIMIT = 2;
        const int LOG_MAX_LINES = 200;   // 日志上限：超过就只留最近 200 行

        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static Window popup;
        static Window reminderWindow;

        static TextBox titleBox;
        static TextBox dateBox;
        static TextBox timeBox;
        static TextBlock placeholder;
        static TextBlock hotkeyText;
        static TextBlock summaryText;
        static TextBlock hintText;
        static List<Button> popupFooterBtns;
        static TextBlock labelText;
        static TextBlock titleText;
        static TextBlock dueText;
        static TextBlock badgeText;
        static Border badgeBox;
        static StackPanel customRow;
        static Button quitBtn;
        static Grid dragBar;

        static List<TaskItem> tasks = new List<TaskItem>();
        static string dataDir;
        static string tasksFile;
        static string settingsFile;
        static string planFile;
        static string priority = "mid";
        static string dueKind = "today";
        static string hotkeyName = "Shift+`";

        static TaskItem current;
        static IntPtr hwnd = IntPtr.Zero;
        static IntPtr hookedHwnd = IntPtr.Zero;
        static IntPtr registeredHwnd = IntPtr.Zero;
        static bool hotkeyRegistered = false;
        static uint hotkeyMods = 0;
        static uint hotkeyVK = 0;
        // 当前注册的是不是"用户自己选的"键：临时降级时为 false，
        // 这样任何一次"保存设置"（换配色等）都不会把降级值写进设置文件
        static bool hotkeyFromUser = true;
        static bool silentStartup = false;   // --startup：登录后静默启动，不弹主界面
        static bool testMode = false;        // --selftest / --ui-selftest / --ai-smoke：不碰真实数据和注册表
        static Settings settings = new Settings();
        static WinForms.NotifyIcon tray;
        static Application application;
        static DispatcherTimer hintTimer;
        static int snoozeMinutes = 60;
        static Window picker;
        static uint pendingMods = 0;
        static uint pendingVK = 0;
        static TextBlock pickerText;
        static bool pickerOpen = false;
        static DispatcherTimer showDelayTimer;
        const int SHOW_DELAY_MS = 1500;

        // -------------------------------------------------------- 入口
        [STAThread]
        public static void Main(string[] args)
        {
            try
            {
                string modeArg = args.Length > 0 ? args[0] : "";
                testMode = IsTestMode(modeArg);
                if (testMode) PrepareTestHome();

                // 先定数据目录（做真实写入测试），否则日志和设置都会写进一个写不进去的目录
                dataDir = PickDataDir();
                tasksFile = Path.Combine(dataDir, "tasks.json");
                settingsFile = Path.Combine(dataDir, "settings.json");
                planFile = Path.Combine(dataDir, "pushplan.json");

                StartupLog("Main enter  build=" + BuildStamp() + "  args=[" + string.Join(" ", args) + "]");
                StartupLog("数据目录 = " + dataDir);
                foreach (var e in dataDirErrors) StartupLog("  不可写: " + e);

                AppDomain.CurrentDomain.ProcessExit += delegate { StartupLog("进程即将结束"); };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s2, UnhandledExceptionEventArgs e2)
                {
                    StartupLog("UNHANDLED " + e2.ExceptionObject);
                };

                // 已经有实例在跑？直接让它弹出录入窗，本次静默退出。
                // 这样"再双击一次 exe"就等于"呼出录入窗"，不依赖全局热键。
                // --startup 是开机自启：发现已有实例就安静退出，不要抢焦点。
                silentStartup = string.Equals(modeArg, "--startup", StringComparison.Ordinal);
                if (silentStartup)
                {
                    if (ExistingInstanceRunning())
                    {
                        StartupLog("开机自启：已有实例在运行，本次静默退出");
                        return;
                    }
                }
                else if (!IsCliMode(modeArg) && SignalExistingInstance())
                {
                    StartupLog("已通知运行中的实例弹出录入窗，本次退出");
                    return;
                }

                // 自检 / 截图 / smoke 是独立进程，不参与单实例锁：
                // 否则主程序开着时 --ai-smoke 会弹“要不要结束旧实例”，根本跑不了。
                if (IsCliMode(modeArg))
                {
                    Run(args);
                    return;
                }

                for (int attempt = 0; attempt < 3; attempt++)
                {
                    bool isNew;
                    using (var mutex = new Mutex(true, "Global\\AdhdLite.SingleInstance", out isNew))
                    {
                        if (isNew)
                        {
                            Run(args);
                            return;
                        }
                    }

                    // 已有实例：多半是旧版本还活着，占着热键和托盘
                    var r = WinForms.MessageBox.Show(
                        "已经有一个 ADHD-lite 在运行（可能是旧版本，它正占着全局快捷键）。\r\n\r\n" +
                        "当前这份的构建时间：" + BuildStamp() + "\r\n\r\n" +
                        "要不要结束旧实例，改用当前这份？\r\n" +
                        "选「是」会关掉旧的那份再启动本版本；选「否」就什么都不做。",
                        "ADHD-lite",
                        WinForms.MessageBoxButtons.YesNo,
                        WinForms.MessageBoxIcon.Question);
                    if (r != WinForms.DialogResult.Yes) return;

                    StartupLog("用户选择结束旧实例");
                    KillOtherInstances();
                    Thread.Sleep(700);
                }
                WinForms.MessageBox.Show("多次尝试仍未拿到单实例锁，请重启电脑后再试。", "ADHD-lite");
            }
            catch (Exception ex)
            {
                StartupLog("FATAL " + ex.ToString());
                try
                {
                    WinForms.MessageBox.Show(
                        "ADHD-lite 启动失败：\r\n\r\n" + ex.Message + "\r\n\r\n日志：" + LogDir() + "\\startup.log",
                        "ADHD-lite 错误", WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Error);
                }
                catch { }
            }
        }

        // -------------------------------------------------------- 实例间通信
        const string SHOW_EVENT_NAME = "Global\\AdhdLite.ShowPopup";
        static EventWaitHandle showEvent;
        static Thread showThread;
        static volatile bool shuttingDown = false;

        static bool SignalExistingInstance()
        {
            try
            {
                using (var ev = EventWaitHandle.OpenExisting(SHOW_EVENT_NAME))
                {
                    ev.Set();
                    return true;
                }
            }
            catch { return false; }
        }

        // 不触发弹窗，只判断有没有活着的实例（开机自启用）
        static bool ExistingInstanceRunning()
        {
            try
            {
                using (var ev = EventWaitHandle.OpenExisting(SHOW_EVENT_NAME)) { return true; }
            }
            catch { return false; }
        }

        // 自检 / 截图 / smoke 这些命令行模式不能因为主程序开着就被"呼出信号"吞掉
        static bool IsCliMode(string arg)
        {
            return !string.IsNullOrEmpty(arg)
                && arg.StartsWith("--", StringComparison.Ordinal)
                && !string.Equals(arg, "--startup", StringComparison.Ordinal);
        }

        static bool IsTestMode(string arg)
        {
            return string.Equals(arg, "--selftest", StringComparison.Ordinal)
                || string.Equals(arg, "--ui-selftest", StringComparison.Ordinal)
                || string.Equals(arg, "--ai-smoke", StringComparison.Ordinal);
        }

        // 自检绝不能再写真实 data\：以前一次 --selftest 把 5 条“自检：…”写进了用户清单。
        // 这里把 ADHD_HOME 指到临时目录，只复制 settings.json（为了拿 Key / 主题），不复制任务和计划。
        static void PrepareTestHome()
        {
            try
            {
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ADHD_HOME"))) return;
                string home = Path.Combine(Path.GetTempPath(), "adhd-selftest-home");
                Directory.CreateDirectory(home);
                string realSettings = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "settings.json");
                string dstSettings = Path.Combine(home, "settings.json");
                try { if (File.Exists(dstSettings)) File.Delete(dstSettings); } catch { }
                if (File.Exists(realSettings))
                {
                    try { File.Copy(realSettings, dstSettings, true); } catch { }
                }
                string[] junk = { "tasks.json", "pushplan.json", "diagnostics.log" };
                foreach (var f in junk)
                {
                    try { File.Delete(Path.Combine(home, f)); } catch { }
                }
                Environment.SetEnvironmentVariable("ADHD_HOME", home);
            }
            catch { }
        }

        static void StartShowListener()
        {
            try
            {
                showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, SHOW_EVENT_NAME);
                showThread = new Thread(delegate()
                {
                    while (!shuttingDown)
                    {
                        try
                        {
                            if (!showEvent.WaitOne(500)) continue;
                            StartupLog("收到呼出信号（来自另一次双击）");
                            popup.Dispatcher.BeginInvoke(new Action(delegate { ShowPopup(); }));
                        }
                        catch { }
                    }
                });
                showThread.IsBackground = true;
                showThread.Start();
                StartupLog("呼出信号监听已启动");
            }
            catch (Exception ex) { StartupLog("创建呼出信号失败 " + ex.Message); }
        }

        static string BuildStamp()        {
            try
            {
                return File.GetLastWriteTime(Assembly.GetExecutingAssembly().Location).ToString("yyyy-MM-dd HH:mm", Inv);
            }
            catch { return "unknown"; }
        }

        static void KillOtherInstances()
        {
            int self = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcessesByName("ADHD-lite"))
            {
                if (p.Id == self) continue;
                try
                {
                    p.Kill();
                    p.WaitForExit(3000);
                    StartupLog("已结束旧实例 PID=" + p.Id);
                }
                catch (Exception ex) { StartupLog("结束旧实例失败 " + ex.Message); }
            }
        }

        // -------------------------------------------------------- 数据目录
        static List<string> dataDirErrors = new List<string>();

        // 依次尝试：ADHD_HOME → exe 同级 data\ → %APPDATA%\adhd-lite → %TEMP%\adhd-lite
        // 每个都做一次真实写入测试，能写才用（%APPDATA% 在你的机器上写不进去）
        static string PickDataDir()
        {
            var candidates = new List<string>();
            string env = Environment.GetEnvironmentVariable("ADHD_HOME");
            if (!string.IsNullOrEmpty(env)) candidates.Add(env);

            string exeDir = "";
            try { exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); } catch { }
            if (!string.IsNullOrEmpty(exeDir)) candidates.Add(Path.Combine(exeDir, "data"));

            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "adhd-lite"));
            candidates.Add(Path.Combine(Path.GetTempPath(), "adhd-lite"));

            foreach (var d in candidates)
            {
                try
                {
                    Directory.CreateDirectory(d);
                    string probe = Path.Combine(d, ".write-test");
                    File.WriteAllText(probe, "ok");
                    File.Delete(probe);
                    return d;
                }
                catch (Exception ex)
                {
                    dataDirErrors.Add(d + "  ->  " + ex.Message);
                }
            }
            dataDirErrors.Add("所有位置都写不进去");
            return Path.GetTempPath();
        }

        static string LogDir()
        {
            if (!string.IsNullOrEmpty(dataDir)) return dataDir;
            return PickDataDir();
        }

        // 日志里绝不落 API Key；设置对象整个序列化时用它脱敏
        static string MaskSecrets(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = System.Text.RegularExpressions.Regex.Replace(
                s, "(\"aiKey\"\\s*:\\s*\")[^\"]*(\")", "$1***$2");
            s = System.Text.RegularExpressions.Regex.Replace(
                s, "(\"mobileToken\"\\s*:\\s*\")[^\"]*(\")", "$1***$2");
            return s;
        }

        static void StartupLog(string s)
        {
            try
            {
                string path = Path.Combine(LogDir(), "startup.log");
                // 日志只保留最近 LOG_MAX_LINES 行，避免无限增长
                if (File.Exists(path))
                {
                    try
                    {
                        string[] old = File.ReadAllLines(path, System.Text.Encoding.UTF8);
                        if (old.Length > LOG_MAX_LINES)
                        {
                            int keep = LOG_MAX_LINES - 200;
                            if (keep < 50) keep = 50;
                            var sb2 = new System.Text.StringBuilder();
                            for (int i = old.Length - keep; i < old.Length; i++) sb2.AppendLine(old[i]);
                            File.WriteAllText(path, sb2.ToString(), new System.Text.UTF8Encoding(false));
                        }
                    }
                    catch { }
                }
                File.AppendAllText(path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv) + "  " + s + Environment.NewLine,
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
        }

        static void TryStep(string name, Action body)
        {
            try
            {
                body();
                StartupLog("OK   " + name);
            }
            catch (Exception ex)
            {
                StartupLog("FAIL " + name + " -> " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        static void Run(string[] args)
        {
            TryStep("确认数据目录", delegate
            {
                if (string.IsNullOrEmpty(dataDir)) dataDir = PickDataDir();
                if (string.IsNullOrEmpty(tasksFile)) tasksFile = Path.Combine(dataDir, "tasks.json");
                if (string.IsNullOrEmpty(settingsFile)) settingsFile = Path.Combine(dataDir, "settings.json");
                if (string.IsNullOrEmpty(planFile)) planFile = Path.Combine(dataDir, "pushplan.json");
                try { Directory.CreateDirectory(dataDir); } catch { }
            });

            application = new Application();
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            application.DispatcherUnhandledException += delegate(object s2, DispatcherUnhandledExceptionEventArgs e2)
            {
                StartupLog("UI 线程异常: " + e2.Exception);
                e2.Handled = true;   // 一个小异常不该把整个程序带走
            };

            TryStep("读取数据", delegate { LoadTasks(); });
            TryStep("恢复进行中的任务", delegate { NormalizeActiveTasks(); });
            TryStep("补齐预计用时", delegate { NormalizeTaskEstimates(); });
            TryStep("读取提醒计划", delegate { LoadPlan(); });
            TryStep("读取设置", delegate { LoadSettings(); });
            TryStep("同步开机自启", delegate { SyncAutoStart(); });
            TryStep("构建界面", delegate { BuildUi(); });
            // 立刻把录入窗句柄建出来并注册热键。
            // 否则启动时只开主界面（录入窗没显示），热键一直没注册：
            // 表现就是"按了没反应"，而且这段时间里保存设置会把 0 写进 settings.json
            TryStep("注册全局热键", delegate
            {
                new WindowInteropHelper(popup).EnsureHandle();
                AttachHotkey();
            });
            TryStep("应用配色", delegate { ApplyTheme(); });
            TryStep("创建托盘", delegate { SetupTray(); });
            TryStep("创建悬浮球", delegate { CreateBall(); });
            // 让"再双击一次 exe"也能呼出录入窗（不依赖全局热键）
            TryStep("启动呼出监听", delegate { StartShowListener(); });
            TryStep("启动手机同步", delegate { if (settings.mobileOn != 0) StartMobileServer(true); });
            // 全局热键不在这里注册：等窗口句柄真正就绪后由 SourceInitialized 触发
            UpdateSummary();
            PaintChips();
            PaintPriority();
            StartupLog("就绪：热键=" + hotkeyName + (hotkeyRegistered ? "（已注册）" : "（尚未注册）") + "，任务数=" + tasks.Count + "，数据目录=" + dataDir);

            dateBox.Text = DateTime.Now.Date.AddDays(1).ToString("yyyy-MM-dd", Inv);

            var tick = new DispatcherTimer();
            tick.Interval = TimeSpan.FromSeconds(30);
            tick.Tick += delegate { Tick(); };
            tick.Start();

            string mode = args.Length > 0 ? args[0] : "";
            string outPath = args.Length > 1 ? args[1] : "";

            if (mode == "--snapshot") { SelfSnapshot(outPath, args.Length > 2 ? args[2] : ""); return; }
            if (mode == "--reminder-shot") { SelfReminderShot(outPath, args.Length > 2 ? args[2] : ""); return; }
            if (mode == "--digest-shot") { SelfDigestShot(outPath, args.Length > 2 ? args[2] : "nudge"); return; }
            if (mode == "--selftest") { SelfTest(); return; }
            if (mode == "--ai-smoke") { SelfAiSmoke(); return; }
            if (mode == "--test-delay") { SelfDelay(); return; }
            if (mode == "--test-hotkey") { SelfHotkey(); return; }
            if (mode == "--probe-hotkeys") { SelfProbeHotkeys(); return; }
            if (mode == "--list-shot") { SelfMainShot(outPath); return; }
            if (mode == "--main-shot") { SelfMainShot(outPath); return; }
            if (mode == "--ui-selftest") { SelfUiTest(); return; }
            if (mode == "--dialog-shot") { SelfDialogShot(outPath, args.Length > 2 ? args[2] : "resched"); return; }

            Tick();

            // 正常启动要"看得见"；--startup 是登录自启，安静待在托盘/悬浮球里
            if (!silentStartup)
            {
                try
                {
                    tray.BalloonTipTitle = "ADHD-lite 已启动";
                    tray.BalloonTipText = "按 " + hotkeyName + " 记一件事；也可以双击托盘图标";
                    tray.BalloonTipIcon = WinForms.ToolTipIcon.Info;
                    tray.ShowBalloonTip(5000);
                    StartupLog("已发送托盘气泡提示");
                }
                catch (Exception ex) { StartupLog("气泡失败 " + ex.Message); }
            }
            else
            {
                StartupLog("开机自启：静默模式，不弹主界面 / 气泡");
            }

            var kick = new DispatcherTimer();
            kick.Interval = TimeSpan.FromMilliseconds(500);
            kick.Tick += delegate
            {
                kick.Stop();
                // 消息泵已经跑起来了：如果托盘还没建好，这里补一次（WinForms 的 NotifyIcon 更稳）
                if (tray == null)
                {
                    TryStep("补建托盘", delegate { SetupTray(); });
                }
                if (tray != null) StartupLog("托盘图标已创建，Visible=" + tray.Visible + "，Text=" + tray.Text);
                if (!silentStartup)
                {
                    ShowMain("list");
                    StartupLog("已打开主界面");
                }
                // 开机后别的软件可能先抢了你的组合键，这里持续尝试恢复
                StartHotkeyRecovery();

                if (tray == null || !hotkeyRegistered)
                    StartupLog("启动异常（仅记录）：" + (tray == null ? "托盘失败 " : "") + (!hotkeyRegistered ? "热键未注册" : ""));
            };
            kick.Start();

            application.Run();
        }

        // -------------------------------------------------------- 存储
        static void LoadTasks()
        {
            tasks = new List<TaskItem>();
            try
            {
                if (!File.Exists(tasksFile)) return;
                var s = Json.Deserialize<Store>(File.ReadAllText(tasksFile, System.Text.Encoding.UTF8));
                if (s != null && s.tasks != null) tasks = s.tasks;
            }
            catch { tasks = new List<TaskItem>(); }
        }

        static void SaveTasks()
        {
            try
            {
                var s = new Store();
                s.tasks = tasks;
                string text = Json.Serialize(s);
                string tmp = tasksFile + ".tmp";
                File.WriteAllText(tmp, text, new System.Text.UTF8Encoding(false));
                if (File.Exists(tasksFile)) File.Delete(tasksFile);
                File.Move(tmp, tasksFile);
            }
            catch (Exception ex) { Trace("[save] " + ex.Message); }
        }

        // -------------------------------------------------------- 时间
        static string DayKey(long ms)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("yyyyMMdd", Inv);
        }

        static long DayStartMs(DateTime d)
        {
            return new DateTimeOffset(d.Date).ToUnixTimeMilliseconds();
        }

        static List<ReminderSlot> BuildReminders(long? due, string pri)
        {
            var list = new List<ReminderSlot>();
            if (!due.HasValue) return list;

            DateTime dueLocal = DateTimeOffset.FromUnixTimeMilliseconds(due.Value).LocalDateTime;
            DateTime day0 = dueLocal.Date;

            Action<DateTime, string> add = delegate(DateTime dt, string label)
            {
                var slot = new ReminderSlot();
                slot.at = new DateTimeOffset(dt).ToUnixTimeMilliseconds();
                slot.label = label;
                list.Add(slot);
            };

            long now = TaskItem.Now();
            DateTime dueDay9 = day0.AddHours(9);
            DateTime before2h = dueLocal.AddHours(-2);

            // 到点提醒：所有任务都有一个"到点了"的提醒点，
            // 否则"5 分钟后"这种短截止会一个提醒都不剩（其它点都已过期）
            add(dueLocal, "到点了");

            if (pri == "high")
            {
                add(day0.AddDays(-1).AddHours(20), "明天就截止了");
                add(dueDay9, "今天截止");
                add(before2h, "还有 2 小时");
            }
            else if (pri == "mid")
            {
                add(dueDay9, "今天截止");
                add(before2h, "还有 2 小时");
            }
            else
            {
                add(dueDay9, "今天截止");
            }

            list.Sort(delegate(ReminderSlot a, ReminderSlot b) { return a.at.CompareTo(b.at); });

            var seen = new HashSet<long>();
            var result = new List<ReminderSlot>();
            foreach (var s in list)
            {
                if (seen.Contains(s.at)) continue;
                seen.Add(s.at);
                if (s.at <= now) s.fired = true;
                result.Add(s);
            }
            return result;
        }

        // -------------------------------------------------------- 界面
        static Window LoadWindow(string xaml)
        {
            return (Window)XamlReader.Parse(xaml);
        }

        static T Find<T>(Window w, string name) where T : class
        {
            return w.FindName(name) as T;
        }

        // 自检用：在可视树里找一段文字（不依赖 x:Name，动态生成的卡片也能测）
        static TextBlock FindTextBlockByText(DependencyObject root, string text)
        {
            if (root == null) return null;
            var tb = root as TextBlock;
            if (tb != null && tb.Text != null && tb.Text.IndexOf(text) >= 0) return tb;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var r = FindTextBlockByText(VisualTreeHelper.GetChild(root, i), text);
                if (r != null) return r;
            }
            return null;
        }

        static Button FindButtonByText(DependencyObject root, string text)
        {
            if (root == null) return null;
            var b = root as Button;
            if (b != null)
            {
                string s = b.Content as string;
                if (s != null && s.IndexOf(text) >= 0) return b;
            }
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var r = FindButtonByText(VisualTreeHelper.GetChild(root, i), text);
                if (r != null) return r;
            }
            return null;
        }

        static void BuildUi()
        {
            popup = LoadWindow(Ui.PopupXaml);
            reminderWindow = LoadWindow(Ui.ReminderXaml);

            titleBox = Find<TextBox>(popup, "TitleBox");
            placeholder = Find<TextBlock>(popup, "Placeholder");
            dragBar = Find<Grid>(popup, "DragBar");
            hotkeyText = Find<TextBlock>(popup, "HotkeyText");
            var popupDeco = Find<Border>(popup, "DecoHost");
            if (popupDeco != null) popupDeco.Child = Deco.MakeScene(StyleIndex(), ColorIndex(), 64, Accent, 0.95, 0);
            var popupScene = Find<Border>(popup, "DecoScene");
            if (popupScene != null) popupScene.Child = Deco.MakeScene(StyleIndex(), ColorIndex(), 150, AccentLine, 0.15, 1);
            summaryText = Find<TextBlock>(popup, "SummaryText");
            hintText = Find<TextBlock>(popup, "HintText");
            quitBtn = Find<Button>(popup, "QuitBtn");
            customRow = Find<StackPanel>(popup, "CustomRow");
            dateBox = Find<TextBox>(popup, "DateBox");
            timeBox = Find<TextBox>(popup, "TimeBox");

            badgeText = Find<TextBlock>(reminderWindow, "BadgeText");
            badgeBox = Find<Border>(reminderWindow, "BadgeBox");
            labelText = Find<TextBlock>(reminderWindow, "LabelText");
            titleText = Find<TextBlock>(reminderWindow, "TitleText");
            dueText = Find<TextBlock>(reminderWindow, "DueText");

            hotkeyText.Text = hotkeyName;

            quitBtn.Visibility = Visibility.Collapsed;
            AttachManualDrag(popup, dragBar);
            if (popup.Content is UIElement) AttachManualDrag(popup, (UIElement)popup.Content);

            // 页脚保留两个入口：清单 / 快捷键（Esc 关闭录入窗）
            var footParent = quitBtn.Parent as StackPanel;
            if (footParent != null)
            {
                int idx = footParent.Children.IndexOf(quitBtn);

                var listBtn = FooterButton("清单");
                listBtn.Click += delegate { ShowList(); };
                if (idx >= 0) footParent.Children.Insert(idx, listBtn);

                popupFooterBtns = new List<Button>();
                popupFooterBtns.Add(listBtn);
                var hkBtn = FooterButton("快捷键");
                popupFooterBtns.Add(hkBtn);
                hkBtn.Click += delegate { OpenHotkeyPicker(); };
                if (idx >= 0) footParent.Children.Insert(idx + 1, hkBtn);

                if (AiDecomposeOn())
                {
                    var aiBtn = FooterButton("AI 拆解");
                    aiBtn.Foreground = B(AccentDeep);
                    popupFooterBtns.Add(aiBtn);
                    aiBtn.Click += delegate { StartDecompose(); };
                    if (idx >= 0) footParent.Children.Insert(idx + 2, aiBtn);
                }
            }

            // 关键：等 WPF 真正创建出窗口句柄后再注册热键
            // （EnsureHandle 之后 Show() 时句柄理论上不会再变，变了 registeredHwnd 判断会兜住）
            popup.SourceInitialized += delegate { AttachHotkey(); };

            titleBox.TextChanged += delegate
            {
                placeholder.Visibility = titleBox.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
            };

            SetupCustomTime();
            titleBox.PreviewKeyDown += OnPopupKeyDown;
            popup.PreviewKeyDown += OnPopupKeyDown;

            popup.Deactivated += delegate
            {
                // 故意什么都不做：点窗口外面不再自动消失，
                // 只有 Enter 保存 / Esc / 关闭按钮 才收起
                if (pickerOpen) return;
            };

            WireChip("ChipToday", "today");
            WireChip("ChipTomorrow", "tomorrow");
            WireChip("ChipIn5", "in5");
            WireChip("ChipIn10", "in10");
            WireChip("ChipCustom", "custom");

            WireSeg("SegHigh", "high");
            WireSeg("SegMid", "mid");
            WireSeg("SegLow", "low");

            WireTrayButton("BtnDone", "done");
            WireTrayButton("BtnStart", "start");
            WireTrayButton("BtnSnooze", "snooze");
            WireTrayButton("BtnIgnore", "ignore");
            var snoozeCustomBtn = Find<Button>(reminderWindow, "BtnSnoozeCustom");
            if (snoozeCustomBtn != null) snoozeCustomBtn.Click += delegate { OpenSnoozePicker(); };
            reminderWindow.PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.ImeProcessed) return;
                if (e.Key == Key.Return) { ResolveCurrent("done"); e.Handled = true; }
                else if (e.Key == Key.Escape) { HideReminder(); e.Handled = true; }
            };
        }

        static void WireChip(string name, string kind)
        {
            var b = Find<Button>(popup, name);
            b.Click += delegate
            {
                dueKind = kind;
                if (kind == "custom" && string.IsNullOrEmpty(dateBox.Text))
                    dateBox.Text = DateTime.Now.Date.AddDays(1).ToString("yyyy-MM-dd", Inv);
                if (mainWin != null && mainWin.Content is Border) ((Border)mainWin.Content).Background = B(AccentBg);
                if (popup != null && popup.Content is Border) ((Border)popup.Content).Background = B(AccentBg);
                if (ballMenu != null && ballMenu.Content is Border) ((Border)ballMenu.Content).Background = B(CardBg);
                PaintChips();
                if (kind == "custom")
                {
                    if (customIsDelay) { if (delayBox != null) delayBox.Focus(); }
                    else dateBox.Focus();
                }
            };
        }

        static void WireSeg(string name, string p)
        {
            var b = Find<Button>(popup, name);
            b.Click += delegate { priority = p; PaintPriority(); };
        }

        static void WireTrayButton(string name, string action)
        {
            var b = Find<Button>(reminderWindow, name);
            b.Click += delegate { ResolveCurrent(action); };
        }

        static Brush B(string hex)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }

        // ---- 自定义时间：延时 / 指定日期 两种方式，二选一（当前用哪种一眼可见）
        static TextBox delayBox;
        static Button delayUnitBtn;
        static Button modeDelayBtn, modeDateBtn;
        static TextBlock delaySuffix, dateFormatHint;
        static bool customIsDelay = true;
        static int delayUnitMs = 60000;

        static void SetupCustomTime()
        {
            try
            {
                delayBox = Find<TextBox>(popup, "DelayBox");
                delayUnitBtn = Find<Button>(popup, "DelayUnit");
                modeDelayBtn = Find<Button>(popup, "ModeDelay");
                modeDateBtn = Find<Button>(popup, "ModeDate");
                delaySuffix = Find<TextBlock>(popup, "DelaySuffix");
                dateFormatHint = Find<TextBlock>(popup, "DateFormatHint");
                if (delayBox == null || delayUnitBtn == null || modeDelayBtn == null || modeDateBtn == null)
                {
                    StartupLog("自定义时间控件缺失，退回仅日期模式");
                    return;
                }

                delayUnitBtn.Template = MakeFlatButtonTemplate();
                delayUnitBtn.Content = "分";
                delayUnitMs = 60000;
                delayUnitBtn.Click += delegate { ShowUnitMenu(delayUnitBtn); };

                modeDelayBtn.Click += delegate { SetCustomMode(true); };
                modeDateBtn.Click += delegate { SetCustomMode(false); };

                // 延时只收数字，别让人填出奇怪的东西
                delayBox.PreviewTextInput += delegate(object s, TextCompositionEventArgs e2)
                {
                    string digits = "0123456789";
                    foreach (char c in e2.Text) if (digits.IndexOf(c) < 0) { e2.Handled = true; return; }
                };
                delayBox.GotFocus += delegate { SetCustomMode(true); };
                dateBox.GotFocus += delegate { SetCustomMode(false); };
                timeBox.GotFocus += delegate { SetCustomMode(false); };

                if (string.IsNullOrEmpty(dateBox.Text))
                    dateBox.Text = DateTime.Now.Date.AddDays(1).ToString("yyyy-MM-dd", Inv);

                SetCustomMode(true);
            }
            catch (Exception ex) { StartupLog("自定义时间控件创建失败: " + ex.Message); }
        }

        static void SetCustomMode(bool delay)
        {
            customIsDelay = delay;
            var vis = delay ? Visibility.Visible : Visibility.Collapsed;
            var hid = delay ? Visibility.Collapsed : Visibility.Visible;
            if (delayBox != null) delayBox.Visibility = vis;
            if (delayUnitBtn != null) delayUnitBtn.Visibility = vis;
            if (delaySuffix != null) delaySuffix.Visibility = vis;
            if (dateBox != null) dateBox.Visibility = hid;
            if (timeBox != null) timeBox.Visibility = hid;
            if (dateFormatHint != null) dateFormatHint.Visibility = hid;
            PaintCustomMode();
        }

        static void PaintCustomMode()
        {
            if (modeDelayBtn == null || modeDateBtn == null) return;
            PaintModeButton(modeDelayBtn, customIsDelay);
            PaintModeButton(modeDateBtn, !customIsDelay);
        }

        static void PaintModeButton(Button b, bool on)
        {
            b.Background = on ? B(AccentSoft) : Brushes.Transparent;
            b.BorderBrush = on ? B(AccentLine) : Brushes.Transparent;
            b.Foreground = on ? B(AccentDeep) : B(TextSub);
            b.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
        static void ShowRescheduleMenu(Button anchor, TaskItem task)
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
                w.WindowStartupLocation = WindowStartupLocation.Manual;

                var card = new Border();
                card.CornerRadius = new CornerRadius(10);
                card.Background = B(CardBg);
                card.BorderBrush = B(LineCol);
                card.BorderThickness = new Thickness(1);
                card.Padding = new Thickness(5);

                var stack = new StackPanel();
                string[] names = { "5 分钟后", "10 分钟后", "1 小时后", "自定义…", "默认 · " + AutoDueText() };
                long nowMs = TaskItem.Now();
                // 只关一次：Deactivated 和"点了菜单项"都会走到关闭，
                // 以前两边各调一次 Close()，第二次会在 OnDeactivated 里抛 InvalidOperationException
                bool closed = false;
                Action close = delegate { if (closed) return; closed = true; try { w.Close(); } catch { } };
                for (int i = 0; i < names.Length; i++)
                {
                    int idx = i;
                    var b = MenuButton(names[i], false);
                    b.Width = 130;
                    b.Click += delegate
                    {
                        if (idx == 3)          // 自定义…：关掉菜单再开一个小窗
                        {
                            close();
                            ShowRescheduleCustom(task);
                            return;
                        }
                        long due = idx == 0 ? TaskItem.Now() + 5 * 60000
                                 : idx == 1 ? TaskItem.Now() + 10 * 60000
                                 : idx == 2 ? TaskItem.Now() + 3600000
                                 : DefaultDueStamp();
                        ApplyPostpone(task, due);
                        close();
                    };
                    stack.Children.Add(b);
                }
                card.Child = stack;
                w.Content = card;

                Point pt = anchor.PointToScreen(new Point(0, anchor.ActualHeight + 2));
                var src = PresentationSource.FromVisual(anchor);
                double sc = 1.0;
                if (src != null && src.CompositionTarget != null) sc = src.CompositionTarget.TransformFromDevice.M11;
                w.Left = pt.X * sc - 40;
                w.Top = pt.Y * sc;
                w.Deactivated += delegate { close(); };
                w.Show();
                w.Activate();
            }
            catch (Exception ex) { StartupLog("改期菜单失败: " + ex.Message); }
        }

        static void ApplyPostpone(TaskItem task, long dueMs)
        {
            if (task == null) return;
            task.due = dueMs;
            task.reminders = BuildReminders(task.due, task.priority);
            task.overdueDay = "";
            // 用户主动改了时间，就当"重新开始计时"：不然今天额度用完后改期也弹不出来
            task.fireDay = "";
            task.fireCount = 0;
            task.postponeCount = task.postponeCount + 1;
            SaveTasks();
            UpdateSummary();
            SchedulePreciseReminder(task);
            RefreshMain();
        }

        // 稍后提醒：清零当日计数（用户主动要求再提醒，不能被 DAILY_LIMIT 吃掉）+ 挂精确定时
        static void SnoozeTask(TaskItem t, long ms)
        {
            if (t == null) return;
            var slot = new ReminderSlot();
            slot.at = TaskItem.Now() + (ms < 200 ? 200 : ms);
            slot.label = "稍后提醒";
            slot.fired = false;
            if (t.reminders == null) t.reminders = new List<ReminderSlot>();
            t.reminders.Add(slot);
            t.fireDay = "";
            t.fireCount = 0;
            // 关键：截止时间还在过去，"逾期"这条今天已经算提醒过了，
            // 否则下一个 30 秒心跳会把提醒窗又顶回来（看着像"点了确定没反应"）
            t.overdueDay = DayKey(TaskItem.Now());
            t.snoozeCount = t.snoozeCount + 1;
            SaveTasks();
            SchedulePreciseReminder(t);
            RefreshMain();
        }

        static bool dialogStartWithDate = false;   // 只给 --dialog-shot 用

        // 改期 → 自定义：先选「延时」或「指定日期」，只有选中的那一组输入，最后按「确定」
        // 编辑任务：改标题 + 改重要度（月历下方的任务行用它）
        static Window ShowEditTaskDialog(TaskItem task)
        {
            try
            {
                if (task == null) return null;
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
                head.Text = "编辑任务";
                head.FontSize = 13;
                head.FontWeight = FontWeights.SemiBold;
                head.Foreground = B(TextMain);
                head.Margin = new Thickness(0, 0, 0, 10);
                root.Children.Add(head);

                var nameBox = new TextBox();
                nameBox.Text = task.title;
                nameBox.FontSize = 13;
                nameBox.Padding = new Thickness(8, 5, 8, 5);
                nameBox.MinWidth = 320;
                nameBox.Background = B(CardBg);
                nameBox.Foreground = B(TextMain);
                nameBox.BorderBrush = B(LineCol);
                nameBox.BorderThickness = new Thickness(1);
                root.Children.Add(nameBox);

                var priRow = new StackPanel();
                priRow.Orientation = Orientation.Horizontal;
                priRow.Margin = new Thickness(0, 10, 0, 0);
                var priLabel = SmallLabel("重要", 34);
                priRow.Children.Add(priLabel);

                string[] keys = { "high", "mid", "low" };
                string[] names = { "高", "中", "低" };
                string[] sel = { task.priority };
                var chips = new List<Button>();
                Action repaint = delegate
                {
                    for (int i = 0; i < chips.Count; i++) RepaintSegPill(chips[i], names[i], keys[i] == sel[0]);
                };
                for (int i = 0; i < keys.Length; i++)
                {
                    int idx = i;
                    var b = SegPill(names[i], keys[i] == sel[0]);
                    b.Click += delegate { sel[0] = keys[idx]; repaint(); };
                    chips.Add(b);
                    priRow.Children.Add(b);
                }
                root.Children.Add(priRow);

                var kindRow = new WrapPanel();
                kindRow.Margin = new Thickness(0, 7, 0, 0);
                kindRow.MaxWidth = 360;
                var kindLabel = SmallLabel("类型", 34);
                kindRow.Children.Add(kindLabel);

                string[] kindSel = { GuessKindOf(task) };
                var kindChips = new List<Button>();
                Action repaintKind = delegate
                {
                    for (int i = 0; i < kindChips.Count; i++)
                        RepaintSegPill(kindChips[i], TaskKind.Names[i], TaskKind.Keys[i] == kindSel[0]);
                };
                for (int i = 0; i < TaskKind.Keys.Length; i++)
                {
                    int idx = i;
                    var b = SegPill(TaskKind.Names[i], TaskKind.Keys[i] == kindSel[0]);
                    b.FontSize = 10.5;
                    b.Padding = new Thickness(7, 3, 7, 3);
                    b.Margin = new Thickness(4, 0, 0, 4);
                    b.Click += delegate { kindSel[0] = TaskKind.Keys[idx]; repaintKind(); };
                    kindChips.Add(b);
                    kindRow.Children.Add(b);
                }
                root.Children.Add(kindRow);

                var estRow = new StackPanel();
                estRow.Orientation = Orientation.Horizontal;
                estRow.Margin = new Thickness(0, 8, 0, 0);
                var estLabel = SmallLabel("预计", 34);
                estRow.Children.Add(estLabel);

                var estBox = new TextBox();
                estBox.Width = 52;
                estBox.FontSize = 12;
                estBox.Padding = new Thickness(6, 3, 6, 3);
                estBox.TextAlignment = TextAlignment.Center;
                estBox.Background = B(CardBg);
                estBox.Foreground = B(TextMain);
                estBox.BorderBrush = B(LineCol);
                estBox.BorderThickness = new Thickness(1);
                int suggested = task.estimateMin > 0 ? task.estimateMin
                              : HabitStats.Compute(tasks, DateTime.Now).SuggestEstimate(task.priority, kindSel[0]);
                estBox.Text = suggested.ToString();
                estRow.Children.Add(estBox);

                var estSuffix = new TextBlock();
                estSuffix.Text = "  分钟（复盘时对比实际用时）";
                estSuffix.FontSize = 11;
                estSuffix.Foreground = B(TextMuted);
                estSuffix.VerticalAlignment = VerticalAlignment.Center;
                estRow.Children.Add(estSuffix);
                root.Children.Add(estRow);

                var bar = new StackPanel();
                bar.Orientation = Orientation.Horizontal;
                bar.HorizontalAlignment = HorizontalAlignment.Right;
                bar.Margin = new Thickness(0, 12, 0, 0);

                var cancel = TinyButton("取消", TextSub);
                cancel.VerticalAlignment = VerticalAlignment.Center;
                cancel.Click += delegate { try { w.Close(); } catch { } };
                bar.Children.Add(cancel);

                var ok = FilledButton("保存");
                ok.Click += delegate
                {
                    string n = (nameBox.Text ?? "").Trim();
                    if (n.Length == 0) { nameBox.Focus(); return; }
                    task.title = n;
                    task.kind = kindSel[0];
                    task.resumeHint = "";
                    int est = 0;
                    int.TryParse((estBox.Text ?? "").Trim(), out est);
                    if (est > 0 && est <= 600) task.estimateMin = est;
                    if (task.priority != sel[0])
                    {
                        task.priority = sel[0];
                        if (task.due.HasValue) task.reminders = BuildReminders(task.due, task.priority);
                    }
                    SaveTasks();
                    UpdateSummary();
                    RefreshMain();
                    try { w.Close(); } catch { }
                };
                bar.Children.Add(ok);
                root.Children.Add(bar);

                w.Content = card;
                w.Show();
                w.Activate();
                nameBox.Focus();
                nameBox.SelectAll();
                return w;
            }
            catch (Exception ex) { StartupLog("编辑任务窗失败: " + ex.Message); }
            return null;
        }

        static Window ShowRescheduleCustom(TaskItem task)
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

                var root = new StackPanel();
                card.Child = root;

                var head = new TextBlock();
                head.Text = "改期：" + task.title;
                head.FontSize = 13;
                head.FontWeight = FontWeights.SemiBold;
                head.Foreground = B(TextMain);
                head.TextTrimming = TextTrimming.CharacterEllipsis;
                head.MaxWidth = 360;
                head.Margin = new Thickness(0, 0, 0, 12);
                root.Children.Add(head);

                // ---- 方式选择（二选一，只显示一组输入）
                bool[] isDelay = { !dialogStartWithDate };
                var modeRow = new StackPanel();
                modeRow.Orientation = Orientation.Horizontal;
                modeRow.Margin = new Thickness(0, 0, 0, 12);

                var delayRow = new StackPanel();
                delayRow.Orientation = Orientation.Horizontal;
                delayRow.Margin = new Thickness(0, 0, 0, 8);
                delayRow.Children.Add(SmallLabel("延时", 34));
                var num = MakeSmallInput("30", 52);
                delayRow.Children.Add(num);

                string[] unitNames = { "秒", "分", "时" };
                int[] unitVals = { 1000, 60000, 3600000 };
                int[] unit = { 60000 };
                var unitChips = new List<Button>();
                Action repaintUnits = delegate
                {
                    for (int i = 0; i < unitChips.Count; i++)
                        RepaintSegPill(unitChips[i], unitNames[i], unitVals[i] == unit[0]);
                };
                for (int i = 0; i < unitNames.Length; i++)
                {
                    int idx = i;
                    var b = SegPill(unitNames[i], unitVals[i] == unit[0]);
                    b.Click += delegate { unit[0] = unitVals[idx]; repaintUnits(); };
                    unitChips.Add(b);
                    delayRow.Children.Add(b);
                }

                var dateRow = new StackPanel();
                dateRow.Orientation = Orientation.Horizontal;
                dateRow.Margin = new Thickness(0, 0, 0, 8);
                dateRow.Children.Add(SmallLabel("指定", 34));
                var dBox = MakeSmallInput(DateTime.Now.Date.AddDays(1).ToString("yyyy-MM-dd", Inv), 100);
                dBox.TextAlignment = TextAlignment.Left;
                dateRow.Children.Add(dBox);
                var tBox = MakeSmallInput("21:00", 66);
                tBox.Margin = new Thickness(6, 0, 0, 0);
                dateRow.Children.Add(tBox);

                var hint = new TextBlock();
                hint.FontSize = 11;
                hint.Foreground = B("#C47B6D");
                hint.Margin = new Thickness(0, 4, 0, 0);

                var modeDelay = SegPill("延时", true);
                var modeDate = SegPill("指定日期", false);
                Action paintMode = delegate
                {
                    RepaintSegPill(modeDelay, "延时", isDelay[0]);
                    RepaintSegPill(modeDate, "指定日期", !isDelay[0]);
                    delayRow.Visibility = isDelay[0] ? Visibility.Visible : Visibility.Collapsed;
                    dateRow.Visibility = isDelay[0] ? Visibility.Collapsed : Visibility.Visible;
                    hint.Text = "";
                };
                modeDelay.Click += delegate { isDelay[0] = true; paintMode(); };
                modeDate.Click += delegate { isDelay[0] = false; paintMode(); };
                modeRow.Children.Add(modeDelay);
                modeRow.Children.Add(modeDate);
                root.Children.Add(modeRow);
                root.Children.Add(delayRow);
                root.Children.Add(dateRow);
                root.Children.Add(hint);

                var bar = new StackPanel();
                bar.Orientation = Orientation.Horizontal;
                bar.HorizontalAlignment = HorizontalAlignment.Right;
                bar.Margin = new Thickness(0, 12, 0, 0);

                var cancel = TinyButton("取消", TextSub);
                cancel.VerticalAlignment = VerticalAlignment.Center;
                cancel.Click += delegate { try { w.Close(); } catch { } };
                bar.Children.Add(cancel);

                var ok = FilledButton("确定");
                ok.Click += delegate
                {
                    if (isDelay[0])
                    {
                        int n = 0;
                        if (!int.TryParse((num.Text ?? "").Trim(), out n) || n <= 0)
                        {
                            hint.Text = "延时要填一个大于 0 的数字";
                            return;
                        }
                        ApplyPostpone(task, TaskItem.Now() + (long)n * unit[0]);
                    }
                    else
                    {
                        long? due = ParseDateTimeText(dBox.Text, tBox.Text);
                        if (!due.HasValue) { hint.Text = "日期格式不对，例：2026-10-25"; return; }
                        ApplyPostpone(task, due.Value);
                    }
                    try { w.Close(); } catch { }
                };
                bar.Children.Add(ok);
                root.Children.Add(bar);

                paintMode();
                w.Content = card;
                w.Show();
                w.Activate();
                return w;
            }
            catch (Exception ex) { StartupLog("自定义改期失败: " + ex.Message); }
            return null;
        }

        static Button FilledButton(string text)
        {
            var b = new Button();
            b.Content = text;
            b.FontSize = 12;
            b.Foreground = B("#FBFDFC");
            b.Background = B(Accent);
            b.BorderThickness = new Thickness(0);
            b.Padding = new Thickness(13, 6, 13, 6);
            b.Margin = new Thickness(8, 0, 0, 0);
            b.Cursor = Cursors.Hand;
            b.Template = MakeCenteredButtonTemplate();
            b.MouseEnter += delegate { b.Opacity = 0.88; };
            b.MouseLeave += delegate { b.Opacity = 1.0; };
            return b;
        }

        // 把中文全角/中文标点统一成英文半角，日期时间里"："和":"都能用
        static string NormalizeDateText(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder();
            foreach (char c in s)
            {
                char x = c;
                if (c == '：') x = ':';
                else if (c == '－' || c == '—' || c == '–' || c == '−') x = '-';
                else if (c == '／') x = '/';
                else if (c == '　') x = ' ';
                else if (c == '．') x = '.';
                else if (c == '，' || c == '、') x = ' ';
                else if (c >= '０' && c <= '９') x = (char)('0' + (c - '０'));
                sb.Append(x);
            }
            return sb.ToString().Trim();
        }

        static long? ParseDateTimeText(string d, string t)
        {
            d = NormalizeDateText(d);
            t = NormalizeDateText(t);
            if (t.Length == 0) t = "21:00";
            DateTime parsed;
            string[] formats = { "yyyy-MM-dd H:mm", "yyyy-MM-dd HH:mm", "yyyy/M/d H:mm", "yyyy/M/d HH:mm" };
            if (DateTime.TryParseExact(d + " " + t, formats, Inv, DateTimeStyles.None, out parsed))
                return new DateTimeOffset(parsed).ToUnixTimeMilliseconds();
            return null;
        }

        // ---- 分段胶囊（秒/分/时、延时/指定日期）：选中态用主色实心 + ✓，鼠标移出不会被抹掉
        static Button SegPill(string text, bool on)
        {
            var b = new Button();
            b.Content = (on ? "✓ " : "") + text;
            b.FontSize = 12;
            b.Padding = new Thickness(11, 4, 11, 4);
            b.Margin = new Thickness(6, 0, 0, 0);
            b.Cursor = Cursors.Hand;
            b.Template = MakeFlatButtonTemplate();
            b.Background = on ? B(AccentDeep) : B(CardBg);
            b.Foreground = on ? B("#FBFDFC") : B(TextSub);
            b.BorderThickness = new Thickness(1);
            b.BorderBrush = on ? B(AccentDeep) : B(LineCol);
            b.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            return b;
        }

        static void RepaintSegPill(Button b, string text, bool on)
        {
            b.Content = (on ? "✓ " : "") + text;
            b.Background = on ? B(AccentDeep) : B(CardBg);
            b.Foreground = on ? B("#FBFDFC") : B(TextSub);
            b.BorderBrush = on ? B(AccentDeep) : B(LineCol);
            b.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }

        static TextBox MakeSmallInput(string text, int width)
        {
            var tb = new TextBox();
            tb.Width = width;
            tb.FontSize = 12;
            tb.Padding = new Thickness(6, 3, 6, 3);
            tb.TextAlignment = TextAlignment.Center;
            tb.VerticalContentAlignment = VerticalAlignment.Center;
            tb.Background = B(CardBg);
            tb.Foreground = B(TextMain);
            tb.BorderBrush = B(LineCol);
            tb.BorderThickness = new Thickness(1);
            tb.Text = text == null ? "" : text;
            return tb;
        }

        static TextBlock SmallLabel(string text, int width)
        {
            var t = new TextBlock();
            t.Text = text;
            t.FontSize = 12;
            t.Foreground = B(TextSub);
            t.Width = width;
            t.VerticalAlignment = VerticalAlignment.Center;
            return t;
        }

        // 通用"延时"小窗：数字 + 秒/分/时 + 确定（提醒窗的"自定义稍后"用它）
        static Window ShowDelayDialog(string title, int defaultNum, int defaultUnitMs, Action<long> onOk)
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

                var root = new StackPanel();
                card.Child = root;

                var head = new TextBlock();
                head.Text = title;
                head.FontSize = 13;
                head.FontWeight = FontWeights.SemiBold;
                head.Foreground = B(TextMain);
                head.Margin = new Thickness(0, 0, 0, 12);
                root.Children.Add(head);

                var row = new StackPanel();
                row.Orientation = Orientation.Horizontal;
                row.Children.Add(SmallLabel("延时", 34));

                var num = MakeSmallInput(defaultNum.ToString(), 52);
                row.Children.Add(num);

                string[] unitNames = { "秒", "分", "时" };
                int[] unitVals = { 1000, 60000, 3600000 };
                int[] unit = { defaultUnitMs <= 0 ? 60000 : defaultUnitMs };
                var chips = new List<Button>();
                Action repaint = delegate
                {
                    for (int i = 0; i < chips.Count; i++) RepaintSegPill(chips[i], unitNames[i], unitVals[i] == unit[0]);
                };
                for (int i = 0; i < unitNames.Length; i++)
                {
                    int idx = i;
                    var b = SegPill(unitNames[i], unitVals[i] == unit[0]);
                    b.Click += delegate { unit[0] = unitVals[idx]; repaint(); };
                    chips.Add(b);
                    row.Children.Add(b);
                }
                root.Children.Add(row);

                var hint = new TextBlock();
                hint.FontSize = 11;
                hint.Foreground = B("#C47B6D");
                hint.Margin = new Thickness(0, 4, 0, 0);
                root.Children.Add(hint);

                var bar = new StackPanel();
                bar.Orientation = Orientation.Horizontal;
                bar.HorizontalAlignment = HorizontalAlignment.Right;
                bar.Margin = new Thickness(0, 12, 0, 0);

                var cancel = TinyButton("取消", TextSub);
                cancel.VerticalAlignment = VerticalAlignment.Center;
                cancel.Click += delegate { try { w.Close(); } catch { } };
                bar.Children.Add(cancel);

                var ok = FilledButton("确定");
                ok.Click += delegate
                {
                    int n = 0;
                    if (!int.TryParse((num.Text ?? "").Trim(), out n) || n <= 0)
                    {
                        hint.Text = "请填一个大于 0 的数字";
                        return;
                    }
                    long ms = (long)n * unit[0];
                    try { w.Close(); } catch { }
                    if (onOk != null) onOk(ms);
                };
                bar.Children.Add(ok);

                root.Children.Add(bar);
                w.Content = card;

                // 数字一开始就处于"全选"状态：窗口激活时、以及点进输入框时都重选一次
                Action selectNum = delegate { try { num.Focus(); num.SelectAll(); } catch { } };
                w.Activated += delegate { selectNum(); };
                num.GotKeyboardFocus += delegate { try { num.SelectAll(); } catch { } };
                num.PreviewMouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
                {
                    if (!num.IsKeyboardFocusWithin) { num.Focus(); num.SelectAll(); e.Handled = true; }
                };

                w.Show();
                w.Activate();
                var hDlg = new WindowInteropHelper(w).Handle;
                try { if (hDlg != IntPtr.Zero) Native.SetForegroundWindow(hDlg); } catch { }
                selectNum();
                var focusOnce = new DispatcherTimer();
                focusOnce.Interval = TimeSpan.FromMilliseconds(80);
                focusOnce.Tick += delegate { focusOnce.Stop(); selectNum(); };
                focusOnce.Start();
                return w;
            }
            catch (Exception ex) { StartupLog("延时选择窗失败: " + ex.Message); }
            return null;
        }

        static void ShowUnitMenu(Button anchor)
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
                w.WindowStartupLocation = WindowStartupLocation.Manual;

                var card = new Border();
                card.CornerRadius = new CornerRadius(10);
                card.Background = B(CardBg);
                card.BorderBrush = B(LineCol);
                card.BorderThickness = new Thickness(1);
                card.Padding = new Thickness(5);

                var stack = new StackPanel();
                string[] names = { "秒", "分", "时" };
                int[] vals = { 1000, 60000, 3600000 };
                bool closed = false;
                Action close = delegate { if (closed) return; closed = true; try { w.Close(); } catch { } };
                for (int i = 0; i < names.Length; i++)
                {
                    int idx = i;
                    var b = MenuButton(names[i], false);
                    b.Width = 78;
                    b.Click += delegate
                    {
                        delayUnitMs = vals[idx];
                        SetCustomMode(true);
                        anchor.Content = names[idx];
                        close();
                    };
                    stack.Children.Add(b);
                }
                card.Child = stack;
                w.Content = card;

                Point pt = anchor.PointToScreen(new Point(0, anchor.ActualHeight + 2));
                var src = PresentationSource.FromVisual(anchor);
                double sc = 1.0;
                if (src != null && src.CompositionTarget != null) sc = src.CompositionTarget.TransformFromDevice.M11;
                w.Left = pt.X * sc;
                w.Top = pt.Y * sc;

                w.Deactivated += delegate { close(); };
                w.Show();
                w.Activate();
            }
            catch (Exception ex) { StartupLog("unit menu failed: " + ex.Message); }
        }
        static void PaintChips()
        {
            string[] kinds = { "today", "tomorrow", "in5", "in10", "custom" };
            string[] names = { "ChipToday", "ChipTomorrow", "ChipIn5", "ChipIn10", "ChipCustom" };
            for (int i = 0; i < kinds.Length; i++)
            {
                var b = Find<Button>(popup, names[i]);
                if (kinds[i] == dueKind)
                {
                    b.Background = B(AccentSoft);
                    b.BorderBrush = B(AccentLine);
                    b.Foreground = B(AccentDeep);
                }
                else
                {
                    b.Background = B(CardBg);
                    b.BorderBrush = B(LineCol);
                    b.Foreground = B(TextSub);
                }
            }
            customRow.Visibility = dueKind == "custom" ? Visibility.Visible : Visibility.Collapsed;
            if (dueKind == "custom") PaintCustomMode();
        }

        static void PaintPriority()
        {
            string[] keys = { "high", "mid", "low" };
            string[] names = { "SegHigh", "SegMid", "SegLow" };
            // 选中态要"实心 + 有边框 + 深色字"，否则浅色底和窗口底色几乎一样，
            // 点完看不出变化，就会被当成"改不了重要程度"
            string[] bg = { "#F6E0DA", "#F7EDD7", "#E2EEE9" };
            string[] bd = { "#DFB4AB", "#E2CBA2", "#BCD5CC" };
            string[] fg = { "#8E4438", "#836526", "#3F6459" };

            for (int i = 0; i < keys.Length; i++)
            {
                var b = Find<Button>(popup, names[i]);
                if (b == null) continue;
                bool on = keys[i] == priority;
                b.Background = on ? B(bg[i]) : Brushes.Transparent;
                b.BorderBrush = on ? B(bd[i]) : Brushes.Transparent;
                b.BorderThickness = new Thickness(1);
                b.Foreground = on ? B(fg[i]) : B(TextSub);
                b.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        static void UpdateSummary()
        {
            long now = TaskItem.Now();
            long endOfToday = DayStartMs(DateTime.Now.AddDays(1));
            int open = 0, today = 0, overdue = 0;
            foreach (var t in tasks)
            {
                if (t.done) continue;
                open++;
                if (t.due.HasValue)
                {
                    if (t.due.Value < endOfToday) today++;
                    if (t.due.Value < now) overdue++;
                }
            }
            summaryText.Text = string.Format("今天 {0} · 逾期 {1} · 未完成 {2}", today, overdue, open);
        }

        static void ShowHint(string text)
        {
            hintText.Text = text;
            if (hintTimer != null) hintTimer.Stop();
            hintTimer = new DispatcherTimer();
            hintTimer.Interval = TimeSpan.FromMilliseconds(1800);
            hintTimer.Tick += delegate
            {
                hintText.Text = "";
                hintTimer.Stop();
            };
            hintTimer.Start();
        }

        // -------------------------------------------------------- 截止时间
        // 设置页里配的"默认截止时间"：每次唤出录入窗时预选它
        static void ApplyDefaultDue()
        {
            try
            {
                string k = settings.autoDue;
                if (string.IsNullOrEmpty(k) || k == "none") return;

                if (k == "delay")
                {
                    int mins = settings.autoDueMinutes > 0 ? settings.autoDueMinutes : 30;
                    dueKind = "custom";
                    if (delayBox != null && delayUnitBtn != null)
                    {
                        delayUnitMs = 60000;
                        delayBox.Text = mins.ToString();
                        delayUnitBtn.Content = "分";
                        SetCustomMode(true);
                    }
                    return;
                }
                dueKind = k;   // today / tomorrow / in5 / in10
            }
            catch (Exception ex) { StartupLog("应用默认时间失败: " + ex.Message); }
        }

        static string AutoDueText()
        {
            string k = settings.autoDue;
            if (string.IsNullOrEmpty(k) || k == "none") return "不预设";
            if (k == "today") return "今天 23:59";
            if (k == "tomorrow") return "明天 21:00";
            if (k == "in5") return "5 分钟后";
            if (k == "in10") return "10 分钟后";
            if (k == "delay") return (settings.autoDueMinutes > 0 ? settings.autoDueMinutes : 30) + " 分钟后";
            return "不预设";
        }

        // 把"默认截止时间"换算成一个具体时刻（改期菜单里的「默认」用它）
        static long DefaultDueStamp()
        {
            DateTime now = DateTime.Now;
            string k = settings.autoDue;
            if (k == "tomorrow")
                return new DateTimeOffset(now.Date.AddDays(1).AddHours(21)).ToUnixTimeMilliseconds();
            if (k == "in5") return new DateTimeOffset(now.AddMinutes(5)).ToUnixTimeMilliseconds();
            if (k == "in10") return new DateTimeOffset(now.AddMinutes(10)).ToUnixTimeMilliseconds();
            if (k == "delay")
            {
                int mins = settings.autoDueMinutes > 0 ? settings.autoDueMinutes : 30;
                return new DateTimeOffset(now.AddMinutes(mins)).ToUnixTimeMilliseconds();
            }
            // none / today → 今天 23:59
            return new DateTimeOffset(now.Date.AddHours(23).AddMinutes(59)).ToUnixTimeMilliseconds();
        }

        static long? SelectedDue()
        {
            DateTime now = DateTime.Now;
            switch (dueKind)
            {
                case "today":
                    return new DateTimeOffset(now.Date.AddHours(23).AddMinutes(59)).ToUnixTimeMilliseconds();
                case "tomorrow":
                    return new DateTimeOffset(now.Date.AddDays(1).AddHours(21)).ToUnixTimeMilliseconds();
                case "in5":
                    return new DateTimeOffset(now.AddMinutes(5)).ToUnixTimeMilliseconds();
                case "in10":
                    return new DateTimeOffset(now.AddMinutes(10)).ToUnixTimeMilliseconds();
                case "friday":
                    return NextWeekday(5);
                case "monday":
                    return NextWeekday(1);
                case "custom":
                    // 按当前选中的方式解析，不再"看焦点在哪"
                    if (customIsDelay)
                    {
                        double n2 = 0;
                        string s2 = delayBox == null ? "" : (delayBox.Text ?? "").Trim();
                        if (!double.TryParse(s2, out n2) || n2 <= 0) return null;
                        return new DateTimeOffset(now.AddMilliseconds(n2 * delayUnitMs)).ToUnixTimeMilliseconds();
                    }
                    return ParseDateTimeText(dateBox.Text, timeBox.Text);
            }
            return null;
        }

        static long NextWeekday(int target)
        {
            DateTime now = DateTime.Now;
            int diff = ((target - (int)now.DayOfWeek) + 7) % 7;
            DateTime d = now.Date.AddDays(diff).AddHours(21);
            if (d <= now) d = d.AddDays(7);
            return new DateTimeOffset(d).ToUnixTimeMilliseconds();
        }

        // -------------------------------------------------------- 动作
        static void Submit()
        {
            string title = (titleBox.Text ?? "").Trim();
            if (title.Length == 0)
            {
                ShowHint("先说清楚要做什么");
                titleBox.Focus();
                return;
            }

            string dupHint;
            var probe = new TaskItem();
            probe.title = title;
            if (CaptureGuard(probe, out dupHint))
            {
                ShowHint(dupHint);
                return;
            }
            bool vague = TaskKind.IsVague(title);

            long? due = SelectedDue();
            if (dueKind == "custom" && !due.HasValue)
            {
                if (customIsDelay)
                {
                    ShowHint("延时请填一个大于 0 的数字");
                    if (delayBox != null) delayBox.Focus();
                }
                else
                {
                    ShowHint("日期格式不对，例：2026-10-25");
                    dateBox.Focus();
                }
                return;
            }

            var t = new TaskItem();
            t.title = title;
            t.priority = priority;
            t.kind = TaskKind.Guess(title);
            t.due = due;
            t.reminders = BuildReminders(due, priority);
            // 预计用时按习惯 + 同类任务给一个起点（之后可在「编辑」里改）
            t.estimateMin = HabitStats.Compute(tasks, DateTime.Now).SuggestEstimate(priority, t.kind);

            tasks.Add(t);
            SaveTasks();
            SchedulePreciseReminder(t);
            UpdateSummary();
            titleBox.Text = "";
            popup.Hide();
            if (vague) ShowToast("已记下", "如果它太大，补一句「下一步」会更容易开始");
            try { if (mainWin != null && mainWin.IsVisible) RefreshMain(); } catch { }
        }

        static void ResolveCurrent(string action)
        {
            if (current == null) { HideReminder(); return; }
            var t = current;
            if (action == "done") { HideReminder(); FinishTask(t); return; }
            if (action == "start") { HideReminder(); StartTask(t); return; }
            if (action == "snooze")
            {
                SnoozeTask(t, snoozeMinutes * 60L * 1000);
            }
            HideReminder();
        }

        // -------------------------------------------------------- 提醒
        static void ShowReminder(TaskItem t, string label)
        {
            current = t;
            try { HideDigest(); } catch { }
            StartupLog("显示提醒: " + t.title + " / " + label + " / " + t.priority);
            ApplyReminderVisual(t, label);

            labelText.Text = t.priority == "high" ? "现在最该处理"
                           : (t.priority == "low" ? "不急，顺手做掉" : label);
            titleText.Text = t.title;
            if (t.due.HasValue)
            {
                string s = DateTimeOffset.FromUnixTimeMilliseconds(t.due.Value).LocalDateTime.ToString("M月d日 HH:mm", Inv);
                dueText.Text = "截止 " + s;

                }
            else
            {
                dueText.Text = "未设截止";
            }

            // 稍后时长：本地规则（重要度 + 任务跨度 + 时段）；开了 AI 决策就再异步问一次
            snoozeMinutes = LocalSnoozeMinutes(t);

            var snoozeBtn = reminderWindow.FindName("BtnSnooze") as Button;
            if (snoozeBtn != null)
                snoozeBtn.Content = "稍后 " + snoozeMinutes + " 分钟";
            AskAiSnoozeSuggestion(t);

            // 提醒窗固定弹在屏幕正中（窗口本身还能拖动）
            reminderWindow.Topmost = true;
            PlaceAtScreenCenter(reminderWindow);
            if (reminderWindow.Content is UIElement) AttachManualDrag(reminderWindow, (UIElement)reminderWindow.Content);
            reminderWindow.Show();
            var zTop = new DispatcherTimer();
            zTop.Interval = TimeSpan.FromMilliseconds(600);
            int zCount = 0;
            zTop.Tick += delegate
            {
                zCount++;
                if (zCount > 12 || !reminderWindow.IsVisible) { zTop.Stop(); return; }
                reminderWindow.Topmost = false;
                reminderWindow.Topmost = true;
            };
            zTop.Start();
        }

        // 高 / 中 / 低三套提醒窗：尺寸、描边、徽章、按钮、文案都不同
        static void ApplyReminderVisual(TaskItem t, string label)
        {
            int lvl = HabitStats.Pi(t.priority);
            bool high = lvl == 0, low = lvl == 2;

            reminderWindow.Width = high ? 520 : (low ? 440 : 480);
            var card = reminderWindow.Content as Border;
            if (card != null)
            {
                card.BorderThickness = new Thickness(high ? 2 : 1);
                card.BorderBrush = B(high ? PriorityColor("high") : (low ? LineCol : AccentLine));
            }

            badgeText.Text = high ? "重要" : (low ? "顺手" : "提醒");
            badgeText.FontSize = high ? 10.5 : 11;
            badgeText.Foreground = B(high ? "#A4574A" : (low ? "#557268" : "#9C7A35"));
            badgeBox.Background = B(high ? "#F8E9E6" : (low ? "#EAF1EE" : "#F8F1E2"));
            badgeBox.BorderBrush = B(high ? "#ECD3CE" : (low ? "#CFE0D9" : "#ECDFC6"));
            titleText.FontSize = high ? 20 : (low ? 17 : 19);

            var tipTb = reminderWindow.FindName("TipText") as TextBlock;
            if (tipTb != null)
            {
                if (t.due.HasValue && t.due.Value < TaskItem.Now())
                    tipTb.Text = high ? "已经过期了：先做 5 分钟，或者现在就改期"
                              : (low ? "已经过期了：顺手清掉，或者改期" : "已经过期了：现在做掉，或者直接改期");
                else
                    tipTb.Text = high ? "这条很重要：先做 5 分钟，开了头就不难"
                              : (low ? "不急，顺手做掉，别让它继续占脑子"
                                     : "有空档就往前推一点，不用一次做完");
            }

            var startBtn = reminderWindow.FindName("BtnStart") as Button;
            if (startBtn != null)
            {
                startBtn.Visibility = low ? Visibility.Collapsed : Visibility.Visible;
                startBtn.Content = "开始做";
                startBtn.Background = B(high ? PriorityColor("high") : CardBg);
                startBtn.Foreground = B(high ? "#FBFDFC" : AccentDeep);
                startBtn.BorderBrush = B(high ? PriorityColor("high") : AccentLine);
            }

            var doneBtn = reminderWindow.FindName("BtnDone") as Button;
            if (doneBtn != null) doneBtn.Background = B(high ? AccentDeep : Accent);

            var custom = reminderWindow.FindName("BtnSnoozeCustom") as Button;
            if (custom != null) custom.Visibility = low ? Visibility.Collapsed : Visibility.Visible;
        }

        // 提醒窗 → 自定义稍后（延时数字 + 秒/分/时）
        static void OpenSnoozePicker()
        {
            var t = current;
            // 打开小窗时数字框里直接选中，敲数字即可覆盖
            ShowDelayDialog("稍后提醒", snoozeMinutes > 0 ? snoozeMinutes : 5, 60000, delegate(long ms)
            {
                if (t == null) return;
                SnoozeTask(t, ms);
                // 提醒窗可能已经因为别的原因关掉了，这里只是兜底
                if (current == t) HideReminder();
            });
        }

        static void HideReminder()
        {
            current = null;
            if (reminderWindow != null) reminderWindow.Hide();
        }

        static bool HasDailyBudget(TaskItem t, long now)
        {
            if (t.fireDay != DayKey(now)) return true;
            return t.fireCount < DAILY_LIMIT;
        }

        static void BumpFireCount(TaskItem t, long now)
        {
            string key = DayKey(now);
            if (t.fireDay != key) { t.fireDay = key; t.fireCount = 1; }
            else t.fireCount = t.fireCount + 1;
        }

        static void Tick()
        {
            long now = TaskItem.Now();
            bool dirty = false;

            MaintainPlan(now);
            bool planOn = PlanActive();
            var overdueNow = new List<TaskItem>();

            foreach (var t in tasks)
            {
                if (t == null || t.done) continue;

                if (t.reminders != null)
                {
                    foreach (var r in t.reminders)
                    {
                        if (r.fired || r.at > now) continue;
                        r.fired = true;
                        dirty = true;
                        // AI 计划生效时，截止前的旧固定提醒不再弹；但「到点了」和用户手动
                        // 「稍后提醒」必须保留，否则 AI 计划没覆盖截止时刻时任务会完全没提示。
                        if (planOn && r.label != "稍后提醒" && r.label != "到点了") continue;
                        if (now - r.at > GRACE_MS) continue;
                        if (!HasDailyBudget(t, now)) continue;
                        BumpFireCount(t, now);
                        ShowReminder(t, r.label);
                        break;
                    }
                }

                if (t.due.HasValue && now > t.due.Value + GRACE_MS && t.overdueDay != DayKey(now))
                {
                    if (QuietHour(now) || AnyReminderVisible()) continue;
                    t.overdueDay = DayKey(now);
                    dirty = true;
                    overdueNow.Add(t);
                }
            }

            if (dirty) SaveTasks();
            if (overdueNow.Count > 0) ShowOverdueDigest(overdueNow);
        }

        // -------------------------------------------------------- 窗口
        // 短任务（到期在 2 分钟内）用一次性定时器，避免 30 秒心跳导致延迟
        // 精确定时：两分钟内要触发的"最近一个提醒点"（提醒槽 or 截止时刻）都单独挂定时器，
        // 否则只能等 30 秒心跳 —— 这就是"设 3 秒却 10 秒才提醒"的原因
        static long precisePlanAt = 0;   // 只用于自检观察

        static void SchedulePreciseReminder(TaskItem t)
        {
            if (t == null || t.done) return;
            long now = TaskItem.Now();
            long best = long.MaxValue;
            if (t.reminders != null)
            {
                foreach (var r in t.reminders)
                {
                    if (r == null || r.fired) continue;
                    if (r.at > now && r.at < best) best = r.at;
                }
            }
            if (t.due.HasValue && t.due.Value > now && t.due.Value < best) best = t.due.Value;
            if (best == long.MaxValue) return;

            long diff = best - now;
            if (diff > 120000) return;
            var tm = new DispatcherTimer();
            tm.Interval = TimeSpan.FromMilliseconds(diff + 60);
            tm.Tick += delegate
            {
                tm.Stop();
                InvokeTickSafe();
            };
            tm.Start();
            precisePlanAt = best;
            StartupLog("已安排精确提醒，约 " + (diff / 1000) + " 秒后");
        }

        static void InvokeTickSafe()
        {
            try { Tick(); } catch (Exception ex) { StartupLog("Tick 异常: " + ex.Message); }
        }
        static void ShowPopup()
        {
            // 每次唤出都回到"默认截止时间"（设置页里配的），写完标题直接回车即可
            ApplyDefaultDue();
            if (popup.IsVisible)
            {
                popup.Topmost = true;
                popup.Activate();
                try { Native.SetForegroundWindow(hwnd); } catch { }
                titleBox.Focus();
                return;
            }
            UpdateSummary();
            PaintChips();
            PaintPriority();
            PlaceAtCursor(popup);
            popup.Topmost = true;
            popup.Show();
            popup.Activate();
            try { Native.SetForegroundWindow(hwnd); } catch { }
            titleBox.Focus();
            titleBox.SelectAll();
            var drop = new DispatcherTimer();
            drop.Interval = TimeSpan.FromMilliseconds(500);
            drop.Tick += delegate { drop.Stop(); popup.Topmost = false; };
            drop.Start();
        }

        // 从月历点某天 → 直接记在这天（打开录入窗并预填日期）
        static void ShowPopupForDate(DateTime day)
        {
            try
            {
                ShowPopup();
                dueKind = "custom";
                SetCustomMode(false);
                if (dateBox != null) dateBox.Text = day.ToString("yyyy-MM-dd", Inv);
                if (timeBox != null) timeBox.Text = "21:00";
                if (modeDateBtn != null) PaintCustomMode();
                PaintChips();
                titleBox.Focus();
            }
            catch (Exception ex) { StartupLog("按日期唤起录入窗失败: " + ex.Message); }
        }

        static void OnPopupKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.ImeProcessed) return;

            if (e.Key == Key.Escape) { popup.Hide(); e.Handled = true; return; }
            if (e.Key == Key.Return) { Submit(); e.Handled = true; return; }

            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (e.Key == Key.D1 || e.Key == Key.NumPad1) { priority = "high"; PaintPriority(); e.Handled = true; }
                else if (e.Key == Key.D2 || e.Key == Key.NumPad2) { priority = "mid"; PaintPriority(); e.Handled = true; }
                else if (e.Key == Key.D3 || e.Key == Key.NumPad3) { priority = "low"; PaintPriority(); e.Handled = true; }
            }
        }

        // -------------------------------------------------------- 托盘 / 热键
        static System.Drawing.Icon trayIconRef;

        static System.Drawing.Icon MakeIcon()
        {
            try
            {
                // 托盘图标必须"实心 + 高对比"
                using (var bmp = new System.Drawing.Bitmap(32, 32))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        g.Clear(System.Drawing.Color.Transparent);
                        using (var bg = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 110, 143, 133)))
                            g.FillEllipse(bg, 1, 1, 30, 30);
                        using (var ring = new System.Drawing.Pen(System.Drawing.Color.FromArgb(255, 246, 243, 238), 3f))
                            g.DrawEllipse(ring, 8, 8, 16, 16);
                        using (var dot = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 250, 248, 244)))
                            g.FillEllipse(dot, 13, 13, 6, 6);
                    }
                    IntPtr h = bmp.GetHicon();
                    // 关键：不要 Clone + Dispose。HICON 一旦被释放，托盘图标就是"死句柄"，
                    // Explorer 会直接忽略它——这很可能就是"一直没有托盘图标"的原因。
                    trayIconRef = System.Drawing.Icon.FromHandle(h);
                    StartupLog("托盘图标: 自绘 " + trayIconRef.Width + "x" + trayIconRef.Height);
                    return trayIconRef;
                }
            }
            catch (Exception ex)
            {
                StartupLog("自绘图标失败，改用系统图标: " + ex.Message);
                trayIconRef = System.Drawing.SystemIcons.Application;
                return trayIconRef;
            }
        }

        static void SetupTray()
        {
            // WinForms 的 NotifyIcon 放到独立 STA 线程 + 自己的消息泵里最稳，
            // 不依赖 WPF 的 Dispatcher
            var th = new Thread(delegate()
            {
                try
                {
                    var menu = new WinForms.ContextMenuStrip();
                    var itemShow = menu.Items.Add("打开录入窗");
                    itemShow.Click += delegate { popup.Dispatcher.BeginInvoke(new Action(delegate { ShowPopup(); })); };
                    menu.Items.Add(new WinForms.ToolStripSeparator());
                    var itemQuit = menu.Items.Add("退出");
                    itemQuit.Click += delegate { popup.Dispatcher.BeginInvoke(new Action(delegate { Quit(); })); };

                    tray = new WinForms.NotifyIcon();
                    tray.Icon = MakeIcon();
                    tray.Text = "ADHD-lite";
                    tray.ContextMenuStrip = menu;
                    tray.DoubleClick += delegate { popup.Dispatcher.BeginInvoke(new Action(delegate { ShowPopup(); })); };
                    tray.Visible = true;

                    StartupLog("托盘线程：图标已进入通知区域");
                    WinForms.Application.Run();   // 该线程自己的消息泵，图标靠它存活
                }
                catch (Exception ex)
                {
                    StartupLog("托盘线程失败: " + ex.Message);
                }
            });
            th.SetApartmentState(ApartmentState.STA);
            th.IsBackground = true;
            th.Start();
        }

        // -------------------------------------------------------- 热键设置
        static string KeyName(uint vk)
        {
            switch (vk)
            {
                case 0x08: return "Back";
                case 0x09: return "Tab";
                case 0x0D: return "Enter";
                case 0x20: return "Space";
                case 0x21: return "PgUp";
                case 0x22: return "PgDn";
                case 0x23: return "End";
                case 0x24: return "Home";
                case 0x25: return "Left";
                case 0x26: return "Up";
                case 0x27: return "Right";
                case 0x28: return "Down";
                case 0x2D: return "Ins";
                case 0x2E: return "Del";
                case 0xBA: return ";";
                case 0xBB: return "=";
                case 0xBC: return ",";
                case 0xBD: return "-";
                case 0xBE: return ".";
                case 0xBF: return "/";
                case 0xC0: return "`";
                case 0xDB: return "[";
                case 0xDC: return "\\";
                case 0xDD: return "]";
                case 0xDE: return "'";
            }
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
            if (vk >= 0x60 && vk <= 0x69) return "Num" + (vk - 0x60);
            if (vk >= 0x70 && vk <= 0x7B) return "F" + (vk - 0x6F);
            switch (vk)
            {
                case 0x6A: return "Num*";
                case 0x6B: return "Num+";
                case 0x6D: return "Num-";
                case 0x6E: return "Num.";
                case 0x6F: return "Num/";
            }
            return "VK" + vk.ToString("X2");
        }

        static string ModName(uint mods)
        {
            var parts = new List<string>();
            if ((mods & MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((mods & MOD_SHIFT) != 0) parts.Add("Shift");
            if ((mods & MOD_ALT) != 0) parts.Add("Alt");
            return string.Join("+", parts.ToArray());
        }

        static string Describe(uint mods, uint vk)
        {
            string m = ModName(mods);
            if (m.Length == 0) return KeyName(vk);
            return m + "+" + KeyName(vk);
        }

        // -------------------------------------------------------- 开机自启
        // 只写 HKCU，不需要管理员；绿色版移动目录后，下次启动会把路径改成新位置。
        const string RUN_KEY_PATH = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RUN_VALUE_NAME = "ADHD-lite";

        static string AutoStartExePath()
        {
            try { return Assembly.GetExecutingAssembly().Location; }
            catch { return ""; }
        }

        static string AutoStartCommand()
        {
            string p = AutoStartExePath();
            if (string.IsNullOrEmpty(p)) return "";
            return "\"" + p + "\" --startup";
        }

        static bool IsAutoStartOn()
        {
            try
            {
                string exe = AutoStartExePath();
                if (string.IsNullOrEmpty(exe)) return false;
                using (var k = Registry.CurrentUser.OpenSubKey(RUN_KEY_PATH, false))
                {
                    if (k == null) return false;
                    string v = k.GetValue(RUN_VALUE_NAME) as string;
                    return !string.IsNullOrEmpty(v)
                        && v.IndexOf(exe, StringComparison.OrdinalIgnoreCase) >= 0
                        && v.IndexOf("--startup", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        static bool SetAutoStart(bool on)
        {
            try
            {
                if (!on)
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(RUN_KEY_PATH, true))
                    {
                        if (k != null) k.DeleteValue(RUN_VALUE_NAME, false);
                    }
                    return true;
                }
                string cmd = AutoStartCommand();
                if (cmd.Length == 0) return false;
                using (var k = Registry.CurrentUser.CreateSubKey(RUN_KEY_PATH))
                {
                    if (k == null) return false;
                    k.SetValue(RUN_VALUE_NAME, cmd, RegistryValueKind.String);
                }
                return true;
            }
            catch (Exception ex)
            {
                StartupLog("开机自启写入失败: " + ex.Message);
                return false;
            }
        }

        // 让设置和注册表保持一致：开了就刷新为当前 exe 路径；用户手动删了就把开关关掉。
        static void SyncAutoStart()
        {
            bool on = IsAutoStartOn();
            if (testMode)
            {
                settings.autoStart = on ? 1 : 0;
                StartupLog("测试模式：跳过开机自启写入（当前=" + (on ? "开" : "关") + "）");
                return;
            }
            if (settings.autoStart != 0)
            {
                if (SetAutoStart(true)) on = true;
                else on = false;
            }
            int want = on ? 1 : 0;
            bool changed = settings.autoStart != want;
            settings.autoStart = want;
            if (changed) SaveSettingsRaw();
            StartupLog("开机自启 = " + (on ? "开" : "关") + (on ? "（" + AutoStartCommand() + "）" : ""));
        }

        static void LoadSettings()
        {
            settingsFile = Path.Combine(dataDir, "settings.json");
            try
            {
                if (File.Exists(settingsFile))
                {
                    string raw = File.ReadAllText(settingsFile, System.Text.Encoding.UTF8);
                    StartupLog("settings.json 内容: " + MaskSecrets(raw));
                    settings = Json.Deserialize<Settings>(raw);
                }
                else
                {
                    StartupLog("settings.json 不存在，使用默认设置");
                }
            }
            catch { }
            if (settings == null) settings = new Settings();

            // 老版本的设置（Shift+` / Ctrl+=）作废，换成新版默认的 Ctrl+Num+
            if (settings.ver < 5)
            {
                int keepTheme = settings.theme;
                settings = new Settings();
                if (keepTheme >= 0 && keepTheme < Styles[0].Length) settings.theme = keepTheme;
                SaveSettingsRaw();
                StartupLog("旧版快捷键设置已迁移为 " + settings.label);
            }

            // ver 6：配色改成"风格 + 颜色"，老的四套颜色归到「柔和」风格里，颜色序号不变
            if (settings.ver < 6)
            {
                int keepTheme = settings.theme;
                int keepAuto = settings.autoDueMinutes;
                string keepKind = settings.autoDue;
                settings.ver = 6;
                settings.style = 0;
                settings.theme = (keepTheme >= 0 && keepTheme < Styles[0].Length) ? keepTheme : 0;
                settings.autoDueMinutes = keepAuto > 0 ? keepAuto : 30;
                settings.autoDue = keepKind == null ? "none" : keepKind;
                SaveSettingsRaw();
                StartupLog("主题已升级为「风格 + 颜色」，当前 = " + CurStyleName() + " · " + ThemeName);
            }

            // ver 7：习惯学习（AI 选任务 / 复盘结论 / 计时字段的默认值）
            if (settings.ver < 7)
            {
                settings.ver = 7;
                SaveSettingsRaw();
                StartupLog("设置已升级到 v7（习惯学习）");
            }

            // ver 8：AI 排提醒计划（截止前间断提醒 / 到期汇总）
            if (settings.ver < 8)
            {
                settings.ver = 8;
                settings.aiPlan = 1;   // 新功能默认开，可在设置里关
                SaveSettingsRaw();
                StartupLog("设置已升级到 v8（AI 提醒计划）");
            }

            // ver 9：开机自启（HKCU Run）；以注册表实际状态为准
            if (settings.ver < 9)
            {
                settings.ver = 9;
                settings.autoStart = IsAutoStartOn() ? 1 : 0;
                SaveSettingsRaw();
                StartupLog("设置已升级到 v9（开机自启）");
            }

            // ver 10：手机同步 API
            if (settings.ver < 10)
            {
                settings.ver = 10;
                if (settings.mobilePort <= 0) settings.mobilePort = 17890;
                if (string.IsNullOrEmpty(settings.mobileToken)) settings.mobileToken = NewMobileToken();
                SaveSettingsRaw();
                StartupLog("设置已升级到 v10（手机同步）");
            }

            // 热键是 0 的设置一定是被写坏的（以前"保存设置"会把尚未注册的 0 写进去），
            // 直接重置成默认，否则每次启动都会 拒绝注册 VK00 → 降级成 Ctrl+=
            // 注意：只修快捷键，别把主题等其它设置一起清掉
            if (settings.mods == 0 || settings.vk == 0)
            {
                settings.mods = 0x0002;
                settings.vk = 0x006B;
                settings.label = "Ctrl+Num+";
                settings.style = StyleIndex();
                settings.theme = ColorIndex();
                SaveSettingsRaw();
                StartupLog("设置里的热键无效（VK00），已重置为默认 " + settings.label);
            }

            hotkeyName = Describe(settings.mods, settings.vk);
            StartupLog("读取快捷键设置: " + hotkeyName);
        }

        // 只落盘"设置对象里已有的值"，不掺当前注册状态（避免把 0 或降级值写进去）
        static void SaveSettingsRaw()
        {
            try
            {
                settings.label = Describe(settings.mods, settings.vk);
                if (settings.ver < 10) settings.ver = 10;
                File.WriteAllText(settingsFile, Json.Serialize(settings), new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex) { StartupLog("保存设置失败 " + ex.Message); }
        }

        static void SaveSettings()
        {
            try
            {
                // 只有"当前确实注册着用户自己选的键"时才回写快捷键；
                // 降级态 / 尚未注册（值为 0）时只保存主题等其它设置
                if (hotkeyFromUser && hotkeyRegistered && hotkeyMods != 0 && hotkeyVK != 0)
                {
                    settings.mods = hotkeyMods;
                    settings.vk = hotkeyVK;
                    settings.label = hotkeyName;
                }
                if (settings.ver < 10) settings.ver = 10;
                string json = Json.Serialize(settings);
                File.WriteAllText(settingsFile, json, new System.Text.UTF8Encoding(false));
                StartupLog("已保存设置 -> " + settingsFile + "  " + MaskSecrets(json));
            }
            catch (Exception ex) { StartupLog("保存设置失败 " + ex.Message); }
        }

        static void UpdateHotkeyUi()
        {
            hotkeyText.Text = hotkeyName;
            if (tray != null) tray.Text = "ADHD-lite（" + hotkeyName + "）";
            if (ball != null) ball.ToolTip = "点一下记一件事（" + hotkeyName + "）· 右键有菜单";
        }

        // -------------------------------------------------------- 主题
        // 两级：风格（整体气质）→ 颜色（同一风格下的主色）
        // 每个颜色 = { 名称, 主色, 浅底, 描边, 深色, 窗口底, 卡片底, 主文字, 次文字, 弱文字, 分隔线, 侧栏底 }
        static readonly string[][][] Styles = new string[][][]
        {
            new string[][]   // 0 柔和（暖米白）
            {
                new string[] { "青瓷", "#4F8A78", "#DFEDE7", "#A9CBBE", "#2F5F51", "#F0F4F1", "#FFFFFF", "#2C3A34", "#5D6B65", "#8A968F", "#DDE7E1", "#DFEDE7" },
                new string[] { "雾蓝", "#4E7CA6", "#E0EAF4", "#A9C5DE", "#2F577A", "#EEF3F8", "#FFFFFF", "#2A3540", "#5A6775", "#8794A1", "#DCE5EE", "#E0EAF4" },
                new string[] { "暖砂", "#B5764A", "#F4E7D9", "#E0C4A6", "#7D4C2B", "#F8F2EA", "#FFFDF8", "#3B332B", "#6B5F52", "#968A7C", "#EADFCF", "#F4E7D9" },
                new string[] { "梅子", "#9A6A88", "#F2E4EC", "#DCC0D0", "#6B3F5B", "#F7EFF4", "#FFFCFD", "#382B33", "#6A5A63", "#94838D", "#E9DCE4", "#F2E4EC" },
            },
            new string[][]   // 1 冷白（偏冷、更亮）
            {
                new string[] { "雪松", "#2F7D5E", "#DDEDE5", "#A9CDBC", "#1E5A42", "#F1F6F3", "#FFFFFF", "#1C2A24", "#4C5F56", "#899690", "#D9E5DF", "#DDEDE5" },
                new string[] { "冰蓝", "#3E7FB8", "#E1EDF8", "#B0CBEA", "#255A87", "#F2F6FB", "#FFFFFF", "#1E2A36", "#4C5A69", "#8492A0", "#DAE3EE", "#E1EDF8" },
                new string[] { "石板", "#525C6B", "#DCE3EA", "#AFBAC7", "#2F3742", "#E8ECF1", "#FFFFFF", "#1A1F26", "#48525E", "#7C8794", "#C9D2DC", "#DCE3EA" },
                new string[] { "雾灰", "#71848B", "#E5EDEF", "#BFCED2", "#44575D", "#F2F5F6", "#FFFFFF", "#1F292C", "#4E5D62", "#879499", "#DBE4E6", "#E5EDEF" },
            },
            new string[][]   // 2 素雅（莫兰迪、低饱和偏灰）
            {
                new string[] { "豆沙", "#A8695F", "#F1E3DF", "#DBBDB7", "#7A453D", "#F5EDEA", "#FBF7F4", "#382F2C", "#6A5D58", "#948681", "#E6D9D4", "#F1E3DF" },
                new string[] { "苔绿", "#7A8A5E", "#EBEFE2", "#CBD6B6", "#54633B", "#F2F4EC", "#FAFAF4", "#34372B", "#63695A", "#8C9284", "#E0E3D5", "#EBEFE2" },
                new string[] { "灰紫", "#7A6B92", "#EDE8F2", "#CFC5DE", "#524668", "#F3F0F7", "#FBFAFC", "#322D3A", "#5F5870", "#8A8497", "#E2DDE9", "#EDE8F2" },
                new string[] { "松烟", "#566A6E", "#E6EDEE", "#C2D1D3", "#37494C", "#F1F5F5", "#FAFCFC", "#26302F", "#566363", "#879493", "#DCE5E5", "#E6EDEE" },
            },
            new string[][]   // 3 科幻（深色侧栏 + 冷光主色）
            {
                new string[] { "星舰", "#12707E", "#DDF1F3", "#9DCFD6", "#0B4A54", "#F0F6F7", "#FFFFFF", "#12262B", "#456168", "#7A959B", "#CDE1E5", "#0E2430" },
                new string[] { "深空", "#5B63C8", "#E4E6F8", "#B8BDE8", "#3A419C", "#F2F3FB", "#FFFFFF", "#1B1F3A", "#4E5478", "#8489A6", "#D9DCF0", "#141A33" },
                new string[] { "极光", "#2E8B6F", "#DFF0EA", "#A9D2C4", "#1B604B", "#F0F7F4", "#FFFFFF", "#152A23", "#48645A", "#7F9A90", "#D3E6DF", "#102A2A" },
                new string[] { "矩阵", "#5F6B1E", "#EEF0DA", "#C9D0A0", "#454E12", "#F6F7EE", "#FFFFFF", "#242814", "#5C6240", "#8C9273", "#E0E4C9", "#1B2417" },
            },
            new string[][]   // 4 简约（蓝白 / 黑白 / 灰蓝 / 米白）
            {
                new string[] { "蓝白", "#2F6FEB", "#E7EFFC", "#B7CBF2", "#1D4FBF", "#FBFCFE", "#FFFFFF", "#162033", "#4B586E", "#8791A3", "#E2E8F1", "#F1F5FC" },
                new string[] { "黑白", "#262A30", "#EEF0F2", "#C9CED3", "#111316", "#FAFAFA", "#FFFFFF", "#17191C", "#555A60", "#8A8F95", "#E4E6E8", "#F3F4F5" },
                new string[] { "橙白", "#D95B22", "#FBE8DE", "#F0C0A6", "#9E3A12", "#FFF9F5", "#FFFFFF", "#2B1D16", "#6B5145", "#9B8578", "#F0E1D7", "#FDF1EA" },
                new string[] { "绿白", "#0B8A5F", "#DDF4EA", "#A3DCC6", "#075C40", "#F6FCF9", "#FFFFFF", "#142A22", "#46685B", "#7E9A8E", "#D8EDE4", "#F0FAF5" },
            },
        };

        static int StyleIndex()
        {
            int i = settings.style;
            if (i < 0 || i >= Styles.Length) i = 0;
            return i;
        }

        static int ColorIndex()
        {
            var arr = Styles[StyleIndex()];
            int i = settings.theme;
            if (i < 0 || i >= arr.Length) i = 0;
            return i;
        }

        static string[] Cur { get { return Styles[StyleIndex()][ColorIndex()]; } }

        static string Accent { get { return Cur[1]; } }
        static string AccentSoft { get { return Cur[2]; } }
        static string AccentLine { get { return Cur[3]; } }
        static string AccentDeep { get { return Cur[4]; } }
        static string AccentBg { get { return Cur[5]; } }
        static string CardBg { get { return Cur[6]; } }
        static string TextMain { get { return Cur[7]; } }
        static string TextSub { get { return Cur[8]; } }
        static string TextMuted { get { return Cur[9]; } }
        static string LineCol { get { return Cur[10]; } }
        static string SideBg { get { return Cur.Length > 11 ? Cur[11] : AccentSoft; } }
        static string ThemeName { get { return Cur[0]; } }
        static string StyleName { get; set; }   // 运行时由 设置页/悬浮球 维护
        static readonly string[] StyleNames = { "柔和", "冷白", "素雅", "科幻", "简约" };

        static bool SideDark()
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(SideBg);
                double l = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
                return l < 0.48;
            }
            catch { return false; }
        }

        // 侧栏文字/悬停/分隔线：深色侧栏自动切成浅色字，浅色侧栏沿用正文色
        static string SideText { get { return SideDark() ? "#E9F3F6" : TextSub; } }
        static string SideTextSub { get { return SideDark() ? "#A8BDC6" : TextMuted; } }
        static string SideHover { get { return SideDark() ? "#1B3544" : "#EFF4F9"; } }
        static string SideLine { get { return SideDark() ? "#27434F" : LineCol; } }
        static string SideBrand { get { return SideDark() ? AccentLine : AccentDeep; } }

        static string CurStyleName()
        {
            int i = StyleIndex();
            return i < StyleNames.Length ? StyleNames[i] : "柔和";
        }

        // 把主题刷到所有窗口上（主界面由 RefreshMain 重建，这里只处理常驻控件）
        static void ApplyTheme()
        {
            try
            {
                if (ball != null && ball.Content is Grid)
                {
                    var g = (Grid)ball.Content;
                    if (g.Children.Count >= 3)
                    {
                        ((System.Windows.Shapes.Ellipse)g.Children[0]).Fill = B(AccentLine);
                        ((System.Windows.Shapes.Ellipse)g.Children[1]).Fill = B(Accent);
                    }
                }
                if (mainWin != null && mainWin.Content is Border) ((Border)mainWin.Content).Background = B(AccentBg);

                // ---- 录入窗
                if (popup != null)
                {
                    var pc = popup.Content as Border;
                    if (pc != null) { pc.Background = B(AccentBg); pc.BorderBrush = B(LineCol); }
                    PaintFg(popup, "TitleBox", TextMain);
                    PaintFg(popup, "Placeholder", TextMuted);
                    PaintFg(popup, "HotkeyText", TextMuted);
                    var pd = Find<Border>(popup, "DecoHost");
                    if (pd != null) pd.Child = Deco.MakeScene(StyleIndex(), ColorIndex(), 64, Accent, 0.95, 0);
                    var ps = Find<Border>(popup, "DecoScene");
                    if (ps != null) ps.Child = Deco.MakeScene(StyleIndex(), ColorIndex(), 150, AccentLine, 0.15, 1);
                    PaintFg(popup, "SummaryText", TextMuted);
                    PaintFg(popup, "HintText", "#C47B6D");
                    PaintInput(popup, "DateBox");
                    PaintInput(popup, "TimeBox");
                    PaintInput(popup, "DelayBox");
                    var du = popup.FindName("DelayUnit") as Button;
                    if (du != null) { du.Background = B(CardBg); du.Foreground = B(TextMain); du.BorderBrush = B(LineCol); }
                    PaintFg(popup, "DelaySuffix", TextMuted);
                    PaintFg(popup, "DateFormatHint", TextMuted);
                    if (popupFooterBtns != null)
                        foreach (var fb in popupFooterBtns) { fb.Foreground = B(TextSub); }
                }

                // ---- 提醒窗
                if (reminderWindow != null)
                {
                    var rc = reminderWindow.Content as Border;
                    if (rc != null) { rc.Background = B(AccentBg); rc.BorderBrush = B(LineCol); }
                    PaintFg(reminderWindow, "LabelText", TextSub);
                    PaintFg(reminderWindow, "DueText", TextMuted);
                    PaintFg(reminderWindow, "TitleText", TextMain);
                    PaintFg(reminderWindow, "TipText", TextSub);
                    var doneBtn = reminderWindow.FindName("BtnDone") as Button;
                    if (doneBtn != null) doneBtn.Background = B(Accent);
                    var startBtn = reminderWindow.FindName("BtnStart") as Button;
                    if (startBtn != null)
                    {
                        startBtn.Background = B(CardBg);
                        startBtn.Foreground = B(AccentDeep);
                        startBtn.BorderBrush = B(AccentLine);
                    }
                    var snooze = reminderWindow.FindName("BtnSnooze") as Button;
                    if (snooze != null) PaintSoftButton(snooze);
                    var snoozeCustom = reminderWindow.FindName("BtnSnoozeCustom") as Button;
                    if (snoozeCustom != null) PaintSoftButton(snoozeCustom);
                    var ignore = reminderWindow.FindName("BtnIgnore") as Button;
                    if (ignore != null) PaintSoftButton(ignore);
                }

                if (ballMenu != null && ballMenu.Content is Border) ((Border)ballMenu.Content).Background = B(CardBg);

                PaintChips();
                PaintPriority();
                PaintCustomMode();
                RepaintMainChrome();
                StartupLog("主题已应用: " + CurStyleName() + " · " + ThemeName + " " + Accent);
            }
            catch (Exception ex) { StartupLog("应用主题失败: " + ex.Message); }
        }

        static void PaintFg(Window w, string name, string color)
        {
            var t = w.FindName(name) as TextBlock;
            if (t != null) t.Foreground = B(color);
        }

        static void PaintInput(Window w, string name)
        {
            var tb = w.FindName(name) as TextBox;
            if (tb == null) return;
            tb.Background = B(CardBg);
            tb.Foreground = B(TextMain);
            tb.BorderBrush = B(LineCol);
        }

        // 提醒窗里的次要按钮：浅底 + 细边 + 次文字色
        static void PaintSoftButton(Button b)
        {
            b.Background = B(AccentSoft);
            b.Foreground = B(TextSub);
            b.BorderBrush = B(LineCol);
        }

        // persist=true 只有"用户亲自录制的"才写盘，自动降级不覆盖用户选择
        static bool TryRegister(uint mods, uint vk, bool persist)
        {
            if (vk == 0) { StartupLog("拒绝注册 VK00"); return false; }
            if (hwnd == IntPtr.Zero) { StartupLog("hwnd 还没就绪，暂不注册"); return false; }
            if (hotkeyRegistered)
            {
                Native.UnregisterHotKey(hwnd, HOTKEY_ID);
                hotkeyRegistered = false;
            }
            bool ok = Native.RegisterHotKey(hwnd, HOTKEY_ID, mods, vk);
            StartupLog("注册 " + Describe(mods, vk) + " -> " + ok);
            if (ok)
            {
                hotkeyRegistered = true;
                registeredHwnd = hwnd;
                hotkeyMods = mods;
                hotkeyVK = vk;
                hotkeyName = Describe(mods, vk);
                if (persist)
                {
                    hotkeyFromUser = true;
                    SaveSettings();   // 先落盘，再刷新界面（否则会被误标成临时降级）
                }
                UpdateHotkeyUi();

                // 顺手再注册一个别名：Ctrl+= 与 Ctrl+Shift+= 都认（+ 通常要按住 Shift）
                if (mods == MOD_CONTROL && vk == 0xBB)
                {
                    bool alias = Native.RegisterHotKey(hwnd, HOTKEY_ID2, MOD_CONTROL | MOD_SHIFT, 0xBB);
                    StartupLog("别名 Ctrl+Shift+= -> " + alias);
                }
            }
            return ok;
        }

        static void RegisterFromSettings()
        {
            // 先用用户自己设的（注册成功就绝不自作主张改掉）
            if (TryRegister(settings.mods, settings.vk, false)) { hotkeyFromUser = true; return; }

            // 用户设的这次没注册上：临时降级，但**不写盘**，下次启动还会再试用户的设置
            var pairs = new List<uint[]>();
            pairs.Add(new uint[] { MOD_CONTROL, 0xBB });              // Ctrl+=
            pairs.Add(new uint[] { MOD_CONTROL | MOD_SHIFT, 0xBB });  // Ctrl+Shift+=
            pairs.Add(new uint[] { MOD_ALT, 0xBB });                  // Alt+=
            pairs.Add(new uint[] { MOD_SHIFT, 0x09 });                // Shift+Tab
            pairs.Add(new uint[] { MOD_ALT, 0x20 });                  // Alt+Space

            var seen = new HashSet<string>();
            foreach (var p in pairs)
            {
                string key = p[0] + ":" + p[1];
                if (seen.Contains(key)) continue;
                seen.Add(key);
                if (p[0] == settings.mods && p[1] == settings.vk) continue;
                if (TryRegister(p[0], p[1], false)) { hotkeyFromUser = false; StartupLog("（临时降级，未写入设置）"); return; }
            }
            hotkeyText.Text = "快捷键注册失败";
            StartupLog("所有候选热键都被占用");
        }

        static void AttachHotkey()
        {
            var h = new WindowInteropHelper(popup).Handle;
            if (h == IntPtr.Zero) { StartupLog("AttachHotkey: 句柄为空"); return; }
            hwnd = h;

            // 句柄变了（WPF 有时会重建），旧注册就作废了，必须重新注册
            if (registeredHwnd != h) hotkeyRegistered = false;

            if (hookedHwnd != h)
            {
                hookedHwnd = h;
                var src = HwndSource.FromHwnd(h);
                if (src != null) src.AddHook(HotkeyHook);
                else StartupLog("AttachHotkey: HwndSource 为空");
                StartupLog("已挂载热键钩子 hwnd=" + h);
            }
            if (!hotkeyRegistered) RegisterFromSettings();
        }

        // -------------------------------------------------------- 悬浮小球
        static Window ball;

        static void CreateBall()
        {
            ball = new Window();
            ball.WindowStyle = WindowStyle.None;
            ball.AllowsTransparency = true;
            ball.Background = Brushes.Transparent;
            ball.Topmost = true;
            ball.ShowInTaskbar = false;
            ball.Width = 44;
            ball.Height = 44;
            ball.WindowStartupLocation = WindowStartupLocation.Manual;

            var root = new Grid();
            var glow = new System.Windows.Shapes.Ellipse();
            glow.Width = 38; glow.Height = 38;
            glow.Fill = B("#B9CEC6");
            glow.Opacity = 0.55;
            var dot = new System.Windows.Shapes.Ellipse();
            dot.Width = 30; dot.Height = 30;
            dot.Fill = B("#6E8F85");
            var inner = new System.Windows.Shapes.Ellipse();
            inner.Width = 10; inner.Height = 10;
            inner.Fill = B("#FAF8F4");
            root.Children.Add(glow);
            root.Children.Add(dot);
            root.Children.Add(inner);
            ball.Content = root;
            ball.ToolTip = "点一下记一件事（" + hotkeyName + "）· 右键有菜单";

            Rect wa = SystemParameters.WorkArea;
            ball.Left = wa.Right - 56;
            ball.Top = wa.Top + wa.Height * 0.40;

            double offX = 0, offY = 0;
            bool moved = false;

            // 鼠标坐标是物理像素，窗口坐标是 DIP（受显示缩放影响），
            // 必须换算，否则球会越拖离鼠标越远
            Func<Point> cursorInDip = delegate
            {
                var p = WinForms.Cursor.Position;
                var src = PresentationSource.FromVisual(ball);
                if (src != null && src.CompositionTarget != null)
                {
                    Matrix m = src.CompositionTarget.TransformFromDevice;
                    return m.Transform(new Point(p.X, p.Y));
                }
                return new Point(p.X, p.Y);
            };

            ball.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                Point p = cursorInDip();
                offX = p.X - ball.Left;
                offY = p.Y - ball.Top;
                moved = false;
                ball.CaptureMouse();
                e.Handled = true;
            };

            ball.MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (!ball.IsMouseCaptured) return;
                Point p = cursorInDip();
                double nx = p.X - offX;
                double ny = p.Y - offY;
                if (Math.Abs(nx - ball.Left) > 3 || Math.Abs(ny - ball.Top) > 3) moved = true;
                if (moved)
                {
                    ball.Left = nx;
                    ball.Top = ny;
                }
            };

            ball.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                ball.ReleaseMouseCapture();
                e.Handled = true;
                if (!moved) ShowMain("list");
            };

            ball.MouseRightButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                ShowBallMenu();
            };

            ball.Show();
            StartupLog("悬浮球已创建，位置 " + (int)ball.Left + "," + (int)ball.Top);
        }

        // -------------------------------------------------------- 悬浮球菜单（自绘，和主界面同风格）
        static Window ballMenu;

        static Button MenuButton(string text, bool danger)
        {
            var b = new Button();
            b.Content = text;
            b.FontSize = 12.5;
            b.HorizontalContentAlignment = HorizontalAlignment.Left;
            b.Padding = new Thickness(14, 9, 14, 9);
            b.Margin = new Thickness(0, 1, 0, 1);
            b.BorderThickness = new Thickness(0);
            b.Background = Brushes.Transparent;
            b.Foreground = B(danger ? "#C47B6D" : TextMain);
            b.Cursor = Cursors.Hand;
            b.Template = MakeFlatButtonTemplate();
            b.MouseEnter += delegate { b.Background = B(AccentSoft); };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
            return b;
        }

        static ControlTemplate MakeFlatButtonTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(cp);
            var tpl = new ControlTemplate(typeof(Button));
            tpl.VisualTree = border;
            return tpl;
        }

        static ControlTemplate MakeCenteredButtonTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(cp);
            var tpl = new ControlTemplate(typeof(Button));
            tpl.VisualTree = border;
            return tpl;
        }
        static void ShowBallMenu()
        {
            if (ballMenu != null) { ballMenu.Close(); ballMenu = null; return; }

            var w = new Window();
            w.WindowStyle = WindowStyle.None;
            w.AllowsTransparency = true;
            w.Background = Brushes.Transparent;
            w.Topmost = true;
            w.ShowInTaskbar = false;
            w.SizeToContent = SizeToContent.WidthAndHeight;
            w.WindowStartupLocation = WindowStartupLocation.Manual;

            var border = new Border();
            border.CornerRadius = new CornerRadius(12);
            border.Background = B(CardBg);
            border.BorderBrush = B(LineCol);
            border.BorderThickness = new Thickness(1);
            border.Padding = new Thickness(6);
            border.MinWidth = 150;

            var stack = new StackPanel();

            var b1 = MenuButton("打开录入窗", false);
            b1.Click += delegate { CloseBallMenu(); ShowPopup(); };
            stack.Children.Add(b1);

            var bList = MenuButton("任务清单", false);
            bList.Click += delegate { CloseBallMenu(); ShowList(); };
            stack.Children.Add(bList);

            var b2 = MenuButton("诊断信息", false);
            b2.Click += delegate { CloseBallMenu(); OpenDiagnostics(); };
            stack.Children.Add(b2);

            var b3 = MenuButton("换配色：" + ThemeName, false);
            b3.Click += delegate
            {
                settings.style = (StyleIndex() + 1) % Styles.Length;
                SaveSettings();
                ApplyTheme();
                CloseBallMenu();
            };
            stack.Children.Add(b3);

            var sep = new Border();
            sep.Height = 1;
            sep.Margin = new Thickness(8, 5, 8, 5);
            sep.Background = B(LineCol);
            stack.Children.Add(sep);

            var b4 = MenuButton("退出", true);
            b4.Click += delegate { CloseBallMenu(); Quit("悬浮球菜单退出"); };
            stack.Children.Add(b4);

            border.Child = stack;
            w.Content = border;

            w.Left = ball.Left - 150;
            w.Top = ball.Top + 50;
            if (w.Left < 0) w.Left = 8;

            w.Deactivated += delegate { CloseBallMenu(); };
            ballMenu = w;
            w.Show();
            w.Activate();
        }

        static void CloseBallMenu()
        {
            if (ballMenu == null) return;
            try { ballMenu.Close(); } catch { }
            ballMenu = null;
        }

        // -------------------------------------------------------- 轻提示（与主界面同风格）
        static void ShowToast(string title, string sub)
        {
            try
            {
                var w = new Window();
                w.WindowStyle = WindowStyle.None;
                w.AllowsTransparency = true;
                w.Background = Brushes.Transparent;
                w.Topmost = true;
                w.ShowInTaskbar = false;
                w.ShowActivated = false;
                w.SizeToContent = SizeToContent.WidthAndHeight;
                w.WindowStartupLocation = WindowStartupLocation.CenterScreen;

                var border = new Border();
                border.CornerRadius = new CornerRadius(14);
                border.Background = B(AccentBg);
                border.BorderBrush = B(LineCol);
                border.BorderThickness = new Thickness(1);
                border.Padding = new Thickness(24, 16, 24, 16);

                var stack = new StackPanel();
                var t1 = new TextBlock();
                t1.Text = title;
                t1.FontSize = 14;
                t1.FontWeight = FontWeights.SemiBold;
                t1.Foreground = B(TextMain);
                var t2 = new TextBlock();
                t2.Text = sub;
                t2.FontSize = 11;
                t2.Foreground = B(TextMuted);
                t2.Margin = new Thickness(0, 5, 0, 0);
                stack.Children.Add(t1);
                stack.Children.Add(t2);
                border.Child = stack;
                w.Content = border;

                var t = new DispatcherTimer();
                t.Interval = TimeSpan.FromMilliseconds(2600);
                t.Tick += delegate
                {
                    t.Stop();
                    try { w.Close(); } catch { }
                };
                t.Start();

                w.MouseDown += delegate { try { w.Close(); } catch { } };
                w.Show();
            }
            catch { }
        }

        // -------------------------------------------------------- 快捷键恢复
        static DispatcherTimer recoverTimer;
        static int recoverTries = 0;

        static void StartHotkeyRecovery()
        {
            recoverTimer = new DispatcherTimer();
            recoverTimer.Interval = TimeSpan.FromSeconds(8);
            recoverTimer.Tick += delegate
            {
                recoverTries++;
                if (recoverTries > 22) { recoverTimer.Stop(); return; }
                if (hotkeyRegistered && hotkeyMods == settings.mods && hotkeyVK == settings.vk)
                {
                    recoverTimer.Stop();
                    return;
                }
                uint oldM = hotkeyMods, oldV = hotkeyVK;
                if (TryRegister(settings.mods, settings.vk, false))
                {
                    hotkeyFromUser = true;
                    StartupLog("已恢复用户设置的快捷键 " + hotkeyName);
                    recoverTimer.Stop();
                    ShowToast("快捷键已恢复", "现在可以用 " + hotkeyName);
                }
                else if (oldM != 0 || oldV != 0)
                {
                    if (TryRegister(oldM, oldV, false)) hotkeyFromUser = false;   // 失败就把临时降级的那个注册回去
                }
            };
            recoverTimer.Start();
        }

        // -------------------------------------------------------- 手动拖动（比 DragMove 可靠）
        static Point CursorInDip(Visual v)
        {
            var p = WinForms.Cursor.Position;
            var src = PresentationSource.FromVisual(v);
            if (src != null && src.CompositionTarget != null)
            {
                Matrix m = src.CompositionTarget.TransformFromDevice;
                return m.Transform(new Point(p.X, p.Y));
            }
            return new Point(p.X, p.Y);
        }

        // Run / FlowDocument 这些 ContentElement 不是 Visual，用 VisualTreeHelper.GetParent 会抛
        // InvalidOperationException（以前点"高/中/低"里的文字就崩在这里，导致按钮 Click 根本不触发）
        static DependencyObject SafeParent(DependencyObject d)
        {
            if (d is Visual || d is System.Windows.Media.Media3D.Visual3D)
                return VisualTreeHelper.GetParent(d);
            var ce = d as ContentElement;
            if (ce != null)
            {
                var p = ContentOperations.GetParent(ce);
                if (p != null) return p;
                var fce = ce as FrameworkContentElement;
                if (fce != null && fce.Parent != null) return fce.Parent;
            }
            return LogicalTreeHelper.GetParent(d);
        }

        static void AttachManualDrag(Window w, UIElement handle)
        {
            bool dragging = false;
            Point startCursor = new Point();
            double startLeft = 0, startTop = 0;

            handle.PreviewMouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                var dep = e.OriginalSource as DependencyObject;
                while (dep != null && dep != handle)
                {
                    if (dep is Button || dep is TextBox) return;
                    // 缩放热区自己处理，别被整窗拖动抢走
                    var fe = dep as FrameworkElement;
                    if (fe != null && (fe.Tag as string) == "grip") return;
                    dep = SafeParent(dep);
                }
                startCursor = CursorInDip(w);
                startLeft = w.Left;
                startTop = w.Top;
                dragging = true;
                handle.CaptureMouse();
                e.Handled = true;
            };

            handle.PreviewMouseMove += delegate(object s, MouseEventArgs e)
            {
                if (!dragging) return;
                Point p = CursorInDip(w);
                w.Left = startLeft + (p.X - startCursor.X);
                w.Top = startTop + (p.Y - startCursor.Y);
            };

            handle.PreviewMouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                if (!dragging) return;
                dragging = false;
                handle.ReleaseMouseCapture();
            };
        }
        // 弹在屏幕（工作区）正中间
        static void PlaceAtScreenCenter(Window w)
        {
            try
            {
                Rect wa = SystemParameters.WorkArea;
                double ww = w.Width > 0 ? w.Width : 440;
                double wh = w.Height > 0 ? w.Height : 150;
                w.Left = wa.Left + (wa.Width - ww) / 2;
                w.Top = wa.Top + (wa.Height - wh) / 2;
            }
            catch { }
        }

        // -------------------------------------------------------- 定位到鼠标附近
        static void PlaceAtCursor(Window w)
        {
            try
            {
                var cursor = WinForms.Cursor.Position;
                var src = PresentationSource.FromVisual(w);
                double scale = 1.0;
                if (src != null && src.CompositionTarget != null)
                    scale = src.CompositionTarget.TransformFromDevice.M11;

                double x = cursor.X * scale - w.Width / 2;
                double y = cursor.Y * scale + 22;

                Rect wa = SystemParameters.WorkArea;
                if (x < wa.Left + 8) x = wa.Left + 8;
                if (x + w.Width > wa.Right - 8) x = wa.Right - w.Width - 8;
                if (y + w.Height > wa.Bottom - 8)
                    y = cursor.Y * scale - w.Height - 22;   // 下方放不下就翻到上方
                if (y < wa.Top + 8) y = wa.Top + 8;

                w.Left = x;
                w.Top = y;
            }
            catch { }
        }

        // -------------------------------------------------------- v0.2 清单页
        static Window listWin;
        static StackPanel listPanel;
        static bool listShowDone = false;
        static bool listToday = false;
        static TextBlock listCount;

        static int PriorityWeight(string p)
        {
            if (p == "high") return 3;
            if (p == "low") return 1;
            return 2;
        }

        static string PriorityName(string p)
        {
            if (p == "high") return "高";
            if (p == "low") return "低";
            return "中";
        }

        static string PriorityColor(string p)
        {
            if (p == "high") return "#C47B6D";
            if (p == "low") return "#8FA8A0";
            return "#C9A15F";
        }

        static int Rank(TaskItem t, long now)
        {
            if (!t.due.HasValue) return 4;
            if (t.due.Value < now) return 0;
            if (t.due.Value < DayStartMs(DateTime.Now.AddDays(1))) return 1;
            if (t.due.Value < DayStartMs(DateTime.Now.AddDays(3))) return 2;
            return 3;
        }

        static string DueText(TaskItem t, long now)
        {
            if (!t.due.HasValue) return "未设截止";
            var d = DateTimeOffset.FromUnixTimeMilliseconds(t.due.Value).LocalDateTime;
            string when = d.ToString("M月d日 HH:mm", Inv);
            long diff = t.due.Value - now;
            if (diff < 0)
            {
                long mins = (-diff) / 60000;
                if (mins < 60) return "截止 " + when + " · 已逾期 " + mins + " 分钟";
                if (mins < 60 * 24) return "截止 " + when + " · 已逾期 " + (mins / 60) + " 小时";
                return "截止 " + when + " · 已逾期 " + (mins / 60 / 24) + " 天";
            }
            if (diff < 60 * 60 * 1000) return "截止 " + when + " · 还有 " + Math.Max(1, diff / 60000) + " 分钟";
            if (diff < 60 * 60 * 1000 * 24) return "截止 " + when + " · 还有 " + (diff / 3600000) + " 小时";
            return "截止 " + when;
        }

        static void ShowList()
        {
            ShowMain("list");
        }

        static Button TinyButton(string text, string color)
        {
            var b = new Button();
            b.Content = text;
            b.FontSize = 11.5;
            b.Foreground = B(color);
            b.Background = Brushes.Transparent;
            b.BorderThickness = new Thickness(0);
            b.Padding = new Thickness(8, 4, 8, 4);
            b.Cursor = Cursors.Hand;
            b.Margin = new Thickness(4, 0, 0, 0);
            b.Template = MakeFlatButtonTemplate();
            b.MouseEnter += delegate { b.Background = B(AccentSoft); };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
            return b;
        }

        // 录入窗页脚的小文字按钮（清单 / 快捷键）
        static Button FooterButton(string text)
        {
            var b = new Button();
            b.Content = text;
            b.FontSize = 11;
            b.Foreground = B(TextSub);
            b.Background = Brushes.Transparent;
            b.BorderThickness = new Thickness(0);
            b.Padding = new Thickness(2, 1, 2, 1);
            b.Margin = new Thickness(10, 0, 0, 0);
            b.Cursor = Cursors.Hand;
            b.Template = MakeFlatButtonTemplate();
            b.MouseEnter += delegate { b.Foreground = B(AccentDeep); };
            b.MouseLeave += delegate { b.Foreground = B(TextSub); };
            return b;
        }

        static void BuildListWindow()
        {
            listWin = new Window();
            listWin.WindowStyle = WindowStyle.None;
            listWin.AllowsTransparency = true;
            listWin.Background = Brushes.Transparent;
            listWin.Width = 540;
            listWin.Height = 640;
            listWin.ShowInTaskbar = true;
            listWin.Topmost = false;
            listWin.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var card = new Border();
            card.CornerRadius = new CornerRadius(16);
            card.Background = B(AccentBg);
            card.BorderBrush = B(LineCol);
            card.BorderThickness = new Thickness(1);
            card.Padding = new Thickness(16, 12, 16, 12);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());

            // 标题栏
            var head = new Grid();
            head.Margin = new Thickness(0, 0, 0, 10);
            AttachManualDrag(listWin, head);

            var titleStack = new StackPanel();
            titleStack.Orientation = Orientation.Horizontal;
            var dot = new System.Windows.Shapes.Ellipse();
            dot.Width = 8; dot.Height = 8;
            dot.Fill = B(Accent);
            dot.Margin = new Thickness(0, 0, 8, 0);
            dot.VerticalAlignment = VerticalAlignment.Center;
            var title = new TextBlock();
            title.Text = "我的清单";
            title.FontSize = 15;
            title.FontWeight = FontWeights.SemiBold;
            title.Foreground = B(TextMain);
            titleStack.Children.Add(dot);
            titleStack.Children.Add(title);
            head.Children.Add(titleStack);

            listCount = new TextBlock();
            listCount.FontSize = 11;
            listCount.Foreground = B(TextMuted);
            listCount.VerticalAlignment = VerticalAlignment.Center;
            listCount.HorizontalAlignment = HorizontalAlignment.Right;
            listCount.Margin = new Thickness(0, 0, 46, 0);
            head.Children.Add(listCount);

            var closeX = new Button();
            closeX.Content = "✕";
            closeX.FontSize = 13;
            closeX.Foreground = B(TextMuted);
            closeX.Background = Brushes.Transparent;
            closeX.BorderThickness = new Thickness(0);
            closeX.Width = 28; closeX.Height = 28;
            closeX.Cursor = Cursors.Hand;
            closeX.HorizontalAlignment = HorizontalAlignment.Right;
            closeX.Template = MakeFlatButtonTemplate();
            closeX.MouseEnter += delegate { closeX.Foreground = B("#C47B6D"); closeX.Background = B(AccentSoft); };
            closeX.MouseLeave += delegate { closeX.Foreground = B(TextMuted); closeX.Background = Brushes.Transparent; };
            closeX.Click += delegate { listWin.Hide(); };
            head.Children.Add(closeX);
            Grid.SetRow(head, 0);
            root.Children.Add(head);

            // 过滤
            var tabs = new StackPanel();
            tabs.Orientation = Orientation.Horizontal;
            tabs.Margin = new Thickness(0, 0, 0, 10);
            var tabUndone = TinyButton("待办", AccentDeep);
            var tabDone = TinyButton("已完成", TextSub);
            tabUndone.Click += delegate { listShowDone = false; RefreshList(); };
            tabDone.Click += delegate { listShowDone = true; RefreshList(); };
            tabs.Children.Add(tabUndone);
            tabs.Children.Add(tabDone);
            Grid.SetRow(tabs, 1);
            root.Children.Add(tabs);

            // 列表
            listPanel = new StackPanel();
            var sv = new ScrollViewer();
            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            sv.Content = listPanel;
            Grid.SetRow(sv, 2);
            root.Children.Add(sv);

            card.Child = root;
            listWin.Content = card;
        }

        static void RefreshList()
        {
            if (listPanel == null) return;
            listPanel.Children.Clear();

            long now = TaskItem.Now();
            var list = new List<TaskItem>();
            foreach (var t in tasks)
            {
                if (listShowDone) { if (t.done) list.Add(t); }
                else { if (!t.done) list.Add(t); }
            }

            list.Sort(delegate(TaskItem a, TaskItem b)
            {
                int ra = Rank(a, now), rb = Rank(b, now);
                if (ra != rb) return ra.CompareTo(rb);
                int pa = PriorityWeight(a.priority), pb = PriorityWeight(b.priority);
                if (pa != pb) return pb.CompareTo(pa);
                long da = a.due.HasValue ? a.due.Value : long.MaxValue;
                long db = b.due.HasValue ? b.due.Value : long.MaxValue;
                return da.CompareTo(db);
            });

            if (listCount != null)
                listCount.Text = listShowDone ? ("已完成 " + list.Count) : ("待办 " + list.Count);

            if (list.Count == 0)
            {
                var empty = new TextBlock();
                empty.Text = listShowDone ? "还没有完成的任务" : "清单是空的，按 Ctrl+= 记一件";
                empty.FontSize = 12;
                empty.Foreground = B(TextMuted);
                empty.Margin = new Thickness(6, 24, 0, 0);
                listPanel.Children.Add(empty);
                return;
            }

            foreach (var t in list) listPanel.Children.Add(MakeRow(t, now));
        }

        static Border MakeRow(TaskItem t, long now, bool doneSection = false)
        {
            var row = new Border();
            row.CornerRadius = new CornerRadius(11);
            row.Background = B(CardBg);
            row.BorderBrush = B(LineCol);
            row.BorderThickness = new Thickness(1);
            row.Padding = new Thickness(12, 9, 8, 9);
            row.Margin = new Thickness(0, 0, 0, 6);

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var bar = new Border();
            bar.CornerRadius = new CornerRadius(2);
            bar.Background = B(PriorityColor(t.priority));
            bar.Margin = new Thickness(0, 0, 10, 0);
            Grid.SetColumn(bar, 0);
            g.Children.Add(bar);

            var stack = new StackPanel();
            var title = new TextBlock();
            title.Text = t.title;
            title.FontSize = 13.5;
            title.Foreground = B(t.done ? TextMuted : TextMain);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            if (t.done) title.TextDecorations = TextDecorations.Strikethrough;
            stack.Children.Add(title);

            var sub = new TextBlock();
            sub.Text = TaskKind.Name(GuessKindOf(t)) + " · " + PriorityName(t.priority) + " · " + DueText(t, now)
                     + (t.estimateMin > 0 ? " · 预计 " + t.estimateMin + " 分" : "")
                     + (t.done && t.spentMs > 0 ? " · 实际 " + FmtMinutes(t.spentMs) : "");
            sub.FontSize = 11;
            sub.Foreground = B(TextMuted);
            sub.Margin = new Thickness(0, 3, 0, 0);
            stack.Children.Add(sub);

            if (!t.done && !string.IsNullOrEmpty(t.lastNote))
            {
                var note = new TextBlock();
                note.Text = "上次：" + t.lastNote;
                note.FontSize = 10.5;
                note.Foreground = B(TextMuted);
                note.TextTrimming = TextTrimming.CharacterEllipsis;
                note.Margin = new Thickness(0, 2, 0, 0);
                stack.Children.Add(note);
            }

            Grid.SetColumn(stack, 1);
            g.Children.Add(stack);

            var btns = new StackPanel();
            btns.Orientation = Orientation.Horizontal;
            btns.VerticalAlignment = VerticalAlignment.Center;

            if (!t.done)
            {
                var startBtn = TinyButton(t.startedAt > 0 ? "计时中" : (t.spentMs > 0 ? "继续" : "开始"), AccentDeep);
                startBtn.IsEnabled = t.startedAt <= 0;
                var taskRefStart = t;
                startBtn.Click += delegate { StartTask(taskRefStart); };
                btns.Children.Add(startBtn);

                var doneBtn = TinyButton("完成", AccentDeep);
                doneBtn.Click += delegate { FinishTask(t); };
                btns.Children.Add(doneBtn);

                var postpone = TinyButton("改期", TextSub);
                var taskRef = t;
                postpone.Click += delegate { ShowRescheduleMenu(postpone, taskRef); };
                btns.Children.Add(postpone);

                if (IsStubborn(t) && AiReady())
                {
                    var split = TinyButton("拆小", AccentDeep);
                    var splitRef = t;
                    split.Click += delegate { AiSplitStubborn(splitRef); };
                    btns.Children.Add(split);
                }
            }
            else
            {
                var undo = TinyButton("撤销", TextSub);
                undo.Click += delegate
                {
                    t.done = false;
                    t.doneAt = 0;
                    SaveTasks();
                    UpdateSummary();
                    RefreshMain();
                };
                btns.Children.Add(undo);
            }

            var del = TinyButton("删除", "#C47B6D");
            del.Click += delegate
            {
                if (t.startedAt > 0) t.startedAt = 0;
                tasks.Remove(t);
                SaveTasks();
                UpdateSummary();
                RefreshMain();
            };
            btns.Children.Add(del);

            Grid.SetColumn(btns, 2);
            g.Children.Add(btns);

            row.Child = g;
            row.MouseEnter += delegate { row.Background = B(AccentSoft); };
            row.MouseLeave += delegate { row.Background = B(CardBg); };
            return row;
        }

        // -------------------------------------------------------- 诊断面板
        static string TailLog(int lines)
        {
            try
            {
                string path = Path.Combine(dataDir, "startup.log");
                if (!File.Exists(path)) return "（日志文件还不存在：" + path + "）";
                string[] all = File.ReadAllLines(path, System.Text.Encoding.UTF8);
                int start = Math.Max(0, all.Length - lines);
                var sb = new System.Text.StringBuilder();
                for (int i = start; i < all.Length; i++) sb.AppendLine(all[i]);
                return sb.ToString();
            }
            catch (Exception ex) { return "读取日志失败: " + ex.Message; }
        }

        static void OpenDiagnostics()
        {
            try
            {
                string info = "构建时间: " + BuildStamp() + Environment.NewLine
                    + "数据目录: " + dataDir + Environment.NewLine
                    + "日志文件: " + Path.Combine(dataDir, "startup.log") + Environment.NewLine
                    + "目录写入检查: " + (dataDirErrors.Count == 0 ? "第一个候选就可用" : string.Join(" | ", dataDirErrors.ToArray())) + Environment.NewLine
                    + "当前热键: " + hotkeyName + (hotkeyRegistered ? "（已注册）" : "（未注册/注册失败）") + Environment.NewLine
                    + "托盘图标: " + (tray == null ? "未创建" : (tray.Visible ? "已创建并设为可见" : "已创建但不可见"))
                    + "  " + (trayIconRef == null ? "" : (trayIconRef.Width + "x" + trayIconRef.Height)) + Environment.NewLine
                    + "任务数: " + tasks.Count + Environment.NewLine
                    + "进程 PID: " + Process.GetCurrentProcess().Id + Environment.NewLine
                    + new string('-', 46) + Environment.NewLine
                    + "最近日志（最后 20 行）：" + Environment.NewLine
                    + TailLog(20);

                var w = new Window();
                w.Title = "ADHD-lite 诊断";
                w.Width = 720;
                w.Height = 520;
                w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                w.Background = B(AccentBg);
                w.Topmost = true;

                var grid = new Grid();
                grid.Margin = new Thickness(14);
                grid.RowDefinitions.Add(new RowDefinition());
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var tb = new TextBox();
                tb.Text = info;
                tb.IsReadOnly = true;
                tb.FontFamily = new FontFamily("Consolas, Microsoft YaHei UI");
                tb.FontSize = 12;
                tb.Background = B(CardBg);
                tb.Foreground = B(TextMain);
                tb.BorderBrush = B(LineCol);
                tb.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                tb.AcceptsReturn = true;
                tb.TextWrapping = TextWrapping.NoWrap;
                Grid.SetRow(tb, 0);
                grid.Children.Add(tb);

                var bar = new StackPanel();
                bar.Orientation = Orientation.Horizontal;
                bar.HorizontalAlignment = HorizontalAlignment.Right;
                bar.Margin = new Thickness(0, 10, 0, 0);

                var openBtn = new Button();
                openBtn.Content = "打开数据文件夹";
                openBtn.FontSize = 12;
                openBtn.Padding = new Thickness(12, 6, 12, 6);
                openBtn.Margin = new Thickness(0, 0, 8, 0);
                openBtn.Cursor = Cursors.Hand;
                openBtn.Click += delegate
                {
                    try { Process.Start("explorer.exe", dataDir); } catch { }
                };
                bar.Children.Add(openBtn);

                var copyBtn = new Button();
                copyBtn.Content = "复制全部";
                copyBtn.FontSize = 12;
                copyBtn.Padding = new Thickness(12, 6, 12, 6);
                copyBtn.Margin = new Thickness(0, 0, 8, 0);
                copyBtn.Cursor = Cursors.Hand;
                copyBtn.Click += delegate
                {
                    try { Clipboard.SetText(info); copyBtn.Content = "已复制"; } catch { }
                };
                bar.Children.Add(copyBtn);

                var okBtn = new Button();
                okBtn.Content = "关闭";
                okBtn.FontSize = 12;
                okBtn.Padding = new Thickness(12, 6, 12, 6);
                okBtn.Cursor = Cursors.Hand;
                okBtn.Click += delegate { w.Close(); };
                bar.Children.Add(okBtn);

                Grid.SetRow(bar, 1);
                grid.Children.Add(bar);

                w.Content = grid;
                w.Show();
                w.Activate();
            }
            catch (Exception ex)
            {
                WinForms.MessageBox.Show("打开诊断面板失败: " + ex.Message);
            }
        }

        // -------------------------------------------------------- 快捷键录制窗
        static void OpenHotkeyPicker()
        {
            if (picker != null) { picker.Activate(); return; }
            pendingMods = 0; pendingVK = 0;

            var w = new Window();
            w.WindowStyle = WindowStyle.None;
            w.AllowsTransparency = true;
            w.Background = Brushes.Transparent;
            w.Topmost = true;
            w.ShowInTaskbar = false;
            w.Width = 380;
            w.Height = 180;
            w.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var border = new Border();
            border.CornerRadius = new CornerRadius(16);
            border.Background = B(AccentBg);
            border.BorderBrush = B(LineCol);
            border.BorderThickness = new Thickness(1);
            border.Padding = new Thickness(22, 18, 22, 18);

            var stack = new StackPanel();
            var t1 = new TextBlock();
            t1.Text = "请直接按下你想要的快捷键";
            t1.FontSize = 13;
            t1.Foreground = B(TextSub);

            pickerText = new TextBlock();
            pickerText.Text = "（等待按键…）";
            pickerText.FontSize = 24;
            pickerText.FontWeight = FontWeights.SemiBold;
            pickerText.Foreground = B(TextMain);
            pickerText.Margin = new Thickness(0, 12, 0, 0);

            var t3 = new TextBlock();
            t3.Text = "回车保存 · Esc 取消";
            t3.FontSize = 11;
            t3.Foreground = B(TextMuted);
            t3.Margin = new Thickness(0, 14, 0, 0);

            stack.Children.Add(t1);
            stack.Children.Add(pickerText);
            stack.Children.Add(t3);
            border.Child = stack;
            w.Content = border;

            w.PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                e.Handled = true;
                if (e.Key == Key.ImeProcessed) return;

                if (e.Key == Key.Escape) { w.Close(); return; }

                if (e.Key == Key.Return)
                {
                    if (pendingVK != 0)
                    {
                        if (TryRegister(pendingMods, pendingVK, true))
                        {
                            string savedName = hotkeyName;
                            StartupLog("用户设置新快捷键: " + savedName);
                            w.Close();
                        }
                        else
                        {
                            pickerText.Text = "被占用了，换一个";
                        }
                    }
                    return;
                }

                // 只按下修饰键时先不记录，等真正的主键
                if (e.Key == Key.LeftShift || e.Key == Key.RightShift ||
                    e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl ||
                    e.Key == Key.LeftAlt || e.Key == Key.RightAlt ||
                    e.Key == Key.LWin || e.Key == Key.RWin) return;

                uint mods = 0;
                var m = Keyboard.Modifiers;
                if ((m & ModifierKeys.Control) != 0) mods |= MOD_CONTROL;
                if ((m & ModifierKeys.Shift) != 0) mods |= MOD_SHIFT;
                if ((m & ModifierKeys.Alt) != 0) mods |= MOD_ALT;

                Key k = e.Key == Key.System ? e.SystemKey : e.Key;
                uint vk = (uint)KeyInterop.VirtualKeyFromKey(k);
                if (vk == 0) return;

                pendingMods = mods;
                pendingVK = vk;
                pickerText.Text = Describe(mods, vk) + "   （回车保存）";
            };

            picker = w;
            w.Closed += delegate
            {
                picker = null;
                pickerOpen = false;
                // 录制期间先注销热键，避免按键被自己的热键抢走
                if (!hotkeyRegistered) RegisterFromSettings();
                // 回到录入窗（只退一步，而不是两层一起关）
                if (!popup.IsVisible) ShowPopup();
                else { popup.Activate(); titleBox.Focus(); }
            };
            // 录制期间先注销热键
            if (hotkeyRegistered && hwnd != IntPtr.Zero)
            {
                Native.UnregisterHotKey(hwnd, HOTKEY_ID);
                hotkeyRegistered = false;
                StartupLog("录制快捷键：已临时注销旧热键");
            }
            pickerOpen = true;
            w.Show();
            w.Activate();
        }

        static void RegisterHotkeys()
        {
            hwnd = new WindowInteropHelper(popup).EnsureHandle();
            StartupLog("窗口句柄 hwnd=" + hwnd);
            var source = HwndSource.FromHwnd(hwnd);
            if (source == null) { StartupLog("HwndSource 为空，热键钩子无法挂载"); return; }
            source.AddHook(HotkeyHook);

            string[] names = { "Shift+Tab", "Ctrl+Shift+Space", "Alt+Space" };
            uint[] mods = { MOD_SHIFT, MOD_CONTROL | MOD_SHIFT, MOD_ALT };
            uint[] vks = { VK_TAB, VK_SPACE, VK_SPACE };

            for (int i = 0; i < names.Length; i++)
            {
                bool ok = Native.RegisterHotKey(hwnd, HOTKEY_ID, mods[i], vks[i]);
                StartupLog("尝试注册 " + names[i] + " -> " + ok);
                if (ok)
                {
                    hotkeyName = names[i];
                    hotkeyText.Text = names[i];
                    if (tray != null) tray.Text = "ADHD-lite（" + names[i] + "）";
                    return;
                }
            }

            hotkeyText.Text = "快捷键注册失败";
            StartupLog("所有候选热键都被占用");
            WinForms.MessageBox.Show(
                "Shift+Tab、Ctrl+Shift+Space、Alt+Space 三个快捷键都被别的软件占用了。\r\n\r\n" +
                "程序仍然在托盘里运行：双击托盘图标也能打开录入窗。\r\n" +
                "想换快捷键请告诉我，或改 src\\Program.cs 后运行 build.cmd。",
                "ADHD-lite 提示", WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Warning);
        }

        static IntPtr HotkeyHook(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                if (probeMode) { probeGot = true; handled = true; return IntPtr.Zero; }
                StartupLog("收到热键 WM_HOTKEY");
                OnHotkeyTriggered();
                handled = true;
            }
            return IntPtr.Zero;
        }

        // ---- 长按 1.5 秒才弹出 ----
        const int HOLD_MS = 1500;
        static DispatcherTimer holdTimer;
        static int holdStartTick;
        static bool holdSawDown = false;

        static void OnHotkeyTriggered()
        {
            ShowPopup();
        }

        // 按下快捷键后延迟 1.5 秒再弹出（防手滑），期间再按一次则取消
        static void ScheduleShowPopup()
        {
            if (showDelayTimer != null)
            {
                showDelayTimer.Stop();
                showDelayTimer = null;
                StartupLog("1.5 秒内又按了一次，取消本次弹出");
                return;
            }

            StartupLog("已安排 1.5 秒后弹出");
            showDelayTimer = new DispatcherTimer();
            showDelayTimer.Interval = TimeSpan.FromMilliseconds(SHOW_DELAY_MS);
            showDelayTimer.Tick += delegate
            {
                showDelayTimer.Stop();
                showDelayTimer = null;
                if (popup.IsVisible) { popup.Activate(); return; }
                StartupLog("延迟结束，弹出录入窗");
                ShowPopup();
            };
            showDelayTimer.Start();
        }

        // 自检：把"改期自定义窗"/"延时小窗"渲染成 PNG（这两个窗平时一闪而过，不好肉眼核对）
        static void SelfDialogShot(string outPath, string which)
        {
            if (string.IsNullOrEmpty(outPath)) outPath = Path.Combine(Path.GetTempPath(), "adhd-dialog.png");
            var fake = new TaskItem();
            fake.title = "把方案发给张三，顺便确认明天的会";
            fake.priority = "high";
            fake.due = TaskItem.Now() + 25 * 60 * 1000;
            fake.estimateMin = 30;
            fake.kind = "writing";
            dialogStartWithDate = which == "resched-date";
            var win = which == "edit" ? ShowEditTaskDialog(fake)
                    : (which == "delay" ? ShowDelayDialog("稍后提醒", 5, 60000, null) : ShowRescheduleCustom(fake));
            var tm = new DispatcherTimer();
            tm.Interval = TimeSpan.FromMilliseconds(700);
            tm.Tick += delegate
            {
                tm.Stop();
                try
                {
                    if (win == null) Trace("DIALOG FAIL 窗口没建起来");
                    else { Trace("DIALOG OK " + which); SaveWindowPng(win, outPath); }
                }
                catch (Exception ex) { Trace("DIALOG FAIL " + ex.Message); }
                Quit();
            };
            tm.Start();
            application.Run();
        }

        // 自检：只能用鼠标验证的交互（拖边框缩放 / 点重要度），用真实路由事件在进程内驱动
        static void SelfUiTest()
        {
            selftesting = true;
            ShowMain("list");
            var t = new DispatcherTimer();
            t.Interval = TimeSpan.FromMilliseconds(800);
            t.Tick += delegate
            {
                t.Stop();
                try
                {
                    mainWin.UpdateLayout();

                    // ---- 1) 拖右边缘缩放
                    var card = mainWin.Content as Border;
                    var root = card == null ? null : card.Child as Grid;
                    Border rightGrip = null;
                    if (root != null)
                    {
                        foreach (var child in root.Children)
                        {
                            var b = child as Border;
                            if (b == null) continue;
                            if ((b.Tag as string) == "grip" && b.Cursor == Cursors.SizeWE && b.HorizontalAlignment == HorizontalAlignment.Right)
                                rightGrip = b;
                        }
                    }
                    if (rightGrip == null) { Trace("RESIZE-DRAG FAIL 没找到右侧热区"); }
                    else
                    {
                        // 先看光标能不能被程序移动（有时会被别的程序/系统限制）
                        var before = WinForms.Cursor.Position;
                        Native.SetCursorPos(before.X + 40, before.Y + 40);
                        bool movable = WinForms.Cursor.Position != before;
                        Native.SetCursorPos(before.X, before.Y);

                        if (!movable) { Trace("RESIZE-DRAG SKIP 光标被占用，无法模拟拖动（缩放数学见 RESIZE-LOGIC）"); }
                        else
                        {
                            double w0 = mainWin.Width;
                            var screen = rightGrip.PointToScreen(new Point(3, Math.Max(4, rightGrip.ActualHeight / 2)));
                            Native.SetCursorPos((int)screen.X, (int)screen.Y);
                            var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left);
                            down.RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent;
                            rightGrip.RaiseEvent(down);
                            Native.SetCursorPos((int)screen.X + 160, (int)screen.Y);
                            var mv = new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount);
                            mv.RoutedEvent = UIElement.MouseMoveEvent;
                            rightGrip.RaiseEvent(mv);
                            var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left);
                            up.RoutedEvent = UIElement.MouseLeftButtonUpEvent;
                            rightGrip.RaiseEvent(up);
                            double w1 = mainWin.Width;
                            // 屏幕上拖了 160 物理像素；窗口宽是 DIP，所以按缩放比换算期望值
                            double scale = 1.0;
                            var src2 = PresentationSource.FromVisual(mainWin);
                            if (src2 != null && src2.CompositionTarget != null) scale = src2.CompositionTarget.TransformToDevice.M11;
                            double expect = 160.0 / (scale <= 0 ? 1.0 : scale);
                            bool okSize = (w1 - w0) > expect * 0.6;
                            Trace("RESIZE-DRAG " + (okSize ? "OK" : "FAIL") + "  " + (int)w0 + " → " + (int)w1 + " DIP（DPI " + (int)(scale * 100) + "%，期望 +" + (int)expect + "）");
                        }
                    }

                    // ---- 1b) 缩放数学（不依赖鼠标能不能动）
                    double lw = mainWin.Width, lh = mainWin.Height;
                    ApplyResize(mainWin, 1, 1, mainWin.Left, mainWin.Top, lw, lh, 120, 80);
                    bool okMath = mainWin.Width > lw + 100 && mainWin.Height > lh + 60;
                    Trace("RESIZE-LOGIC " + (okMath ? "OK" : "FAIL") + "  " + (int)lw + "x" + (int)lh + " → " + (int)mainWin.Width + "x" + (int)mainWin.Height);

                    // ---- 2) 触发"高"按钮的 Click（走真实的 Click 路由事件 → WireSeg 处理器）
                    var segHigh = Find<Button>(popup, "SegHigh");
                    if (segHigh == null) { Trace("PRIORITY FAIL 找不到 SegHigh"); }
                    else
                    {
                        priority = "mid";
                        PaintPriority();
                        segHigh.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, segHigh));
                        Trace("PRIORITY " + (priority == "high" ? "OK" : "FAIL") + "  priority=" + priority);
                    }

                    // ---- 3) 点按钮里的文字，拖动处理不能再抛异常
                    if (segHigh != null && segHigh.Content is StackPanel)
                    {
                        var inner = (StackPanel)segHigh.Content;
                        var tb = inner.Children.Count > 0 ? inner.Children[0] as TextBlock : null;
                        if (tb != null)
                        {
                            try
                            {
                                var md = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left);
                                md.RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent;
                                tb.RaiseEvent(md);
                                Trace("DRAG-WALK OK 点到按钮文字不抛异常");
                            }
                            catch (Exception ex) { Trace("DRAG-WALK FAIL " + ex.GetType().Name + " " + ex.Message); }
                        }
                    }

                    // ---- 3b) v0.5：现在做哪件卡片 / 正在做计时条
                    var nowCard = FindTextBlockByText(mainWin, "现在做哪件");
                    Trace("HABIT-CARD " + (nowCard != null ? "OK" : "FAIL"));

                    // 分页选中态不能被 MouseLeave 抹掉（以前 TinyButton 会）
                    var tabBtn = FindButtonByText(mainWin, "待办");
                    bool tabOk = false;
                    if (tabBtn != null)
                    {
                        var beforeBrush = tabBtn.Background as SolidColorBrush;
                        var en = new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount);
                        en.RoutedEvent = Mouse.MouseEnterEvent;
                        tabBtn.RaiseEvent(en);
                        var lv = new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount);
                        lv.RoutedEvent = Mouse.MouseLeaveEvent;
                        tabBtn.RaiseEvent(lv);
                        var afterBrush = tabBtn.Background as SolidColorBrush;
                        tabOk = beforeBrush != null && afterBrush != null
                             && beforeBrush.Color == afterBrush.Color
                             && beforeBrush.Color != Colors.Transparent;
                    }
                    Trace("TAB-HIGHLIGHT " + (tabOk ? "OK" : "FAIL"));

                    var uiTask = new TaskItem();
                    uiTask.title = "自检：正在做";
                    uiTask.priority = "mid";
                    uiTask.estimateMin = 20;
                    tasks.Add(uiTask);
                    StartTask(uiTask);
                    RefreshMain();
                    mainWin.UpdateLayout();
                    bool hasBar = FindTextBlockByText(mainWin, "正在做 · 中") != null;
                    bool hasClock = FindTextBlockByText(mainWin, "预计 20 分") != null;
                    Trace("ACTIVE-BAR " + (hasBar && hasClock ? "OK" : "FAIL") + " bar=" + hasBar + " clock=" + hasClock);
                    PauseTask(uiTask);
                    tasks.Remove(uiTask);
                    RefreshMain();

                    // ---- 3c) v0.6：汇总弹窗（还有哪些没做完）
                    var dTask = new TaskItem();
                    dTask.title = "自检：汇总里的任务";
                    dTask.priority = "mid";
                    dTask.due = TaskItem.Now() + 30 * 60 * 1000;
                    tasks.Add(dTask);
                    var dItem = new PushItem();
                    dItem.at = TaskItem.Now();
                    dItem.kind = "nudge";
                    dItem.taskId = dTask.id;
                    dItem.level = 1;
                    dItem.message = "先做 10 分钟";
                    ShowDigest(dItem, "nudge");
                    bool digestOk = digestWindow != null && FindTextBlockByText(digestWindow, "没做完") != null;
                    Trace("DIGEST-UI " + (digestOk ? "OK" : "FAIL"));
                    HideDigest();
                    tasks.Remove(dTask);
                    RefreshMain();

                    // ---- 3d) 计划到点 → 汇总弹窗（QUIET 时间在自检里也允许，方便白天/半夜都测）
                    var planTask = new TaskItem();
                    planTask.title = "自检：计划到点";
                    planTask.priority = "high";
                    planTask.due = TaskItem.Now() + 30 * 60 * 1000;
                    tasks.Add(planTask);
                    pushPlan = new PushPlan();
                    pushPlan.generatedAt = TaskItem.Now();
                    pushPlan.signature = PlanSignature();
                    pushPlan.source = "local";
                    pushPlan.summary = "自检计划";
                    var planIt = new PushItem();
                    planIt.at = TaskItem.Now() - 1000;
                    planIt.kind = "nudge";
                    planIt.taskId = planTask.id;
                    planIt.level = 0;
                    planIt.message = "先做 5 分钟";
                    pushPlan.items.Add(planIt);
                    FirePlan(TaskItem.Now());
                    bool fireOk = digestWindow != null && FindTextBlockByText(digestWindow, "没做完") != null;
                    Trace("PLAN-FIRE " + (fireOk ? "OK" : "FAIL"));
                    HideDigest();
                    pushPlan = null;
                    tasks.Remove(planTask);
                    RefreshMain();

                    // ---- 3e) 到期汇总：列出没完成的任务
                    var odTask = new TaskItem();
                    odTask.title = "自检：到期没完成";
                    odTask.priority = "mid";
                    odTask.due = TaskItem.Now() - 2 * 3600 * 1000;
                    tasks.Add(odTask);
                    var odList = new List<TaskItem>();
                    odList.Add(odTask);
                    ShowOverdueDigest(odList);
                    bool odOk = digestWindow != null && FindTextBlockByText(digestWindow, "已经到期") != null;
                    Trace("DIGEST-OVERDUE " + (odOk ? "OK" : "FAIL"));
                    HideDigest();
                    tasks.Remove(odTask);
                    RefreshMain();
                }
                catch (Exception ex) { Trace("UITEST FAIL " + ex.Message); Quit(); return; }

                // ---- 4) 稍后提醒：今日额度用光 + 已经逾期的情况下，0.7 秒后必须真的再弹
                var snoozeTask = new TaskItem();
                snoozeTask.title = "自检：稍后提醒";
                snoozeTask.priority = "mid";
                snoozeTask.due = TaskItem.Now() - 5 * 60 * 1000;
                snoozeTask.reminders = new List<ReminderSlot>();
                snoozeTask.fireDay = DayKey(TaskItem.Now());
                snoozeTask.fireCount = DAILY_LIMIT;      // 今天的 2 次额度已用光
                tasks.Add(snoozeTask);
                HideReminder();
                SnoozeTask(snoozeTask, 700);
                Trace("SNOOZE-PLAN " + (precisePlanAt > 0 ? "OK" : "FAIL") + " 已挂精确定时");

                var step1 = new DispatcherTimer();
                step1.Interval = TimeSpan.FromMilliseconds(1400);
                step1.Tick += delegate
                {
                    step1.Stop();
                    bool fired1 = reminderWindow.IsVisible;
                    Trace("SNOOZE-FIRE " + (fired1 ? "OK" : "FAIL") + " 额度用光时稍后 0.7 秒仍应弹出");

                    // 提醒窗应该弹在屏幕正中
                    if (fired1)
                    {
                        Rect wa1 = SystemParameters.WorkArea;
                        double cx = reminderWindow.Left + reminderWindow.ActualWidth / 2;
                        double cy = reminderWindow.Top + reminderWindow.ActualHeight / 2;
                        bool centered = Math.Abs(cx - (wa1.Left + wa1.Width / 2)) < 40
                                     && Math.Abs(cy - (wa1.Top + wa1.Height / 2)) < 60;
                        Trace("REMINDER-CENTER " + (centered ? "OK" : "FAIL") + " 窗口中心=(" + (int)cx + "," + (int)cy + ")");
                    }
                    HideReminder();

                    // 关掉之后，紧接着的心跳不能因为"任务已逾期"又把窗顶回来（以前点确定后还会再弹）
                    InvokeTickSafe();
                    bool quiet = !reminderWindow.IsVisible;
                    Trace("SNOOZE-QUIET " + (quiet ? "OK" : "FAIL") + " 稍后关掉后心跳不应再弹逾期");
                    HideReminder();

                    // ---- 5) 改期：同样要能弹
                    var postTask = new TaskItem();
                    postTask.title = "自检：改期";
                    postTask.priority = "mid";
                    postTask.due = TaskItem.Now() - 30 * 60 * 1000;
                    postTask.reminders = new List<ReminderSlot>();
                    postTask.fireDay = DayKey(TaskItem.Now());
                    postTask.fireCount = DAILY_LIMIT;
                    tasks.Add(postTask);
                    ApplyPostpone(postTask, TaskItem.Now() + 700);
                    Trace("POSTPONE-PLAN " + (precisePlanAt > 0 ? "OK" : "FAIL") + " 已挂精确定时");

                    var step2 = new DispatcherTimer();
                    step2.Interval = TimeSpan.FromMilliseconds(1400);
                    step2.Tick += delegate
                    {
                        step2.Stop();
                        bool fired2 = reminderWindow.IsVisible;
                        Trace("POSTPONE-FIRE " + (fired2 ? "OK" : "FAIL") + " 额度用光时改期后仍应弹出");
                        HideReminder();
                        Quit();
                    };
                    step2.Start();
                };
                step1.Start();
            };
            t.Start();
            application.Run();
        }

        static void Quit()
        {
            Quit("用户退出");
        }

        static void Quit(string reason)
        {
            StartupLog("=== 退出： " + reason + " ===");
            try { StopMobileServer(); } catch { }
            try { PauseActiveForExit(); } catch { }
            try { HideDigest(); } catch { }
            shuttingDown = true;
            try
            {
                if (hwnd != IntPtr.Zero) Native.UnregisterHotKey(hwnd, HOTKEY_ID);
                if (hwnd != IntPtr.Zero) Native.UnregisterHotKey(hwnd, HOTKEY_ID2);
                if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
            }
            catch { }
            application.Shutdown();
        }

        // -------------------------------------------------------- 自检
        static void Trace(string s)
        {
            System.Console.WriteLine(s);
            try
            {
                File.AppendAllText(Path.Combine(dataDir, "diagnostics.log"), s + Environment.NewLine, System.Text.Encoding.UTF8);
            }
            catch { }
        }

        static void SaveWindowPng(Window w, string path)
        {
            w.UpdateLayout();
            int width = (int)Math.Ceiling(w.ActualWidth);
            int height = (int)Math.Ceiling(w.ActualHeight);
            if (width <= 0 || height <= 0) throw new Exception(string.Format("窗口尺寸无效 {0}x{1}", width, height));

            var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(w);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(path)) enc.Save(fs);
            Trace(string.Format("{0} {1}x{2}", path, width, height));
        }

        // variant: "" 默认 / "custom" 自定义延时模式 / "custom-date" 指定日期模式
        static void SelfSnapshot(string outPath, string variant)
        {
            if (string.IsNullOrEmpty(outPath)) outPath = Path.Combine(Path.GetTempPath(), "adhd-shot.png");
            ShowPopup();
            if (variant == "custom" || variant == "custom-date")
            {
                dueKind = "custom";
                SetCustomMode(variant != "custom-date");
                PaintChips();
            }
            var t = new DispatcherTimer();
            t.Interval = TimeSpan.FromMilliseconds(900);
            t.Tick += delegate
            {
                t.Stop();
                try { Trace("SNAPSHOT_OK"); SaveWindowPng(popup, outPath); }
                catch (Exception ex) { Trace("SNAPSHOT_FAIL " + ex.Message); }
                Quit();
            };
            t.Start();
            application.Run();
        }

        static void SelfReminderShot(string outPath, string level)
        {
            selftesting = true;
            bool oldSnooze = settings.aiSnooze != 0;
            settings.aiSnooze = 0;   // 截图不联网
            if (string.IsNullOrEmpty(outPath)) outPath = Path.Combine(Path.GetTempPath(), "adhd-reminder.png");
            var fake = new TaskItem();
            fake.title = "把方案发给张三，顺便确认明天的会";
            fake.priority = level == "low" ? "low" : (level == "mid" ? "mid" : "high");
            fake.due = TaskItem.Now() + 2 * 60 * 60 * 1000;
            fake.estimateMin = fake.priority == "high" ? 30 : (fake.priority == "low" ? 10 : 20);

            ShowReminder(fake, fake.priority == "low" ? "今天截止" : "还有 2 小时");
            var t = new DispatcherTimer();
            t.Interval = TimeSpan.FromMilliseconds(900);
            t.Tick += delegate
            {
                t.Stop();
                try { Trace("REMINDER_OK " + fake.priority); SaveWindowPng(reminderWindow, outPath); }
                catch (Exception ex) { Trace("REMINDER_FAIL " + ex.Message); }
                settings.aiSnooze = oldSnooze ? 1 : 0;
                Quit();
            };
            t.Start();
            application.Run();
        }

        // 渲染「汇总提醒」：--digest-shot out.png nudge|overdue|mid|low
        static void SelfDigestShot(string outPath, string which)
        {
            selftesting = true;
            if (string.IsNullOrEmpty(outPath)) outPath = Path.Combine(Path.GetTempPath(), "adhd-digest.png");
            var a = new TaskItem(); a.title = "把方案发给张三，顺便确认明天的会"; a.priority = "high"; a.due = TaskItem.Now() + 25 * 60 * 1000; a.estimateMin = 30;
            var b = new TaskItem(); b.title = "交房租"; b.priority = "mid"; b.due = TaskItem.Now() - 3 * 60 * 60 * 1000; b.estimateMin = 45;
            var c = new TaskItem(); c.title = "看完那篇关于注意力的论文"; c.priority = "low"; c.estimateMin = 15;
            tasks.Add(a); tasks.Add(b); tasks.Add(c);
            if (which == "overdue") { a.due = TaskItem.Now() - 2 * 3600 * 1000; b.due = TaskItem.Now() - 4 * 3600 * 1000; }

            var it = new PushItem();
            it.at = TaskItem.Now();
            it.kind = which == "overdue" ? "overdue" : "nudge";
            it.taskId = which == "low" ? c.id : (which == "mid" ? b.id : a.id);
            it.level = which == "low" ? 2 : (which == "mid" ? 1 : 0);
            it.message = which == "overdue" ? "这些已经过了截止时间，挑一件先做 5 分钟"
                       : (which == "low" ? "顺手做掉一件" : "先花 10 分钟把方案开头写了");
            ShowDigest(it, it.kind);

            var t = new DispatcherTimer();
            t.Interval = TimeSpan.FromMilliseconds(900);
            t.Tick += delegate
            {
                t.Stop();
                try { Trace("DIGEST_OK " + which); SaveWindowPng(digestWindow, outPath); }
                catch (Exception ex) { Trace("DIGEST_FAIL " + ex.Message); }
                HideDigest();
                Quit();
            };
            t.Start();
            application.Run();
        }

        // 逐个探测哪些组合键真的能被系统投递（用来判断是不是被输入法/其他软件吞了）
        static bool probeMode = false;
        static bool probeGot = false;
        static int probeIdx = 0;
        static int probeState = 0;
        static List<uint[]> probeCombos;

        static void SelfProbeHotkeys()
        {
            probeMode = true;
            probeCombos = new List<uint[]>();
            probeCombos.Add(new uint[] { MOD_SHIFT, 0xC0 });                       // Shift+`
            probeCombos.Add(new uint[] { MOD_SHIFT, 0x09 });                       // Shift+Tab
            probeCombos.Add(new uint[] { MOD_CONTROL | MOD_SHIFT, 0x20 });         // Ctrl+Shift+Space
            probeCombos.Add(new uint[] { MOD_ALT, 0x20 });                        // Alt+Space
            probeCombos.Add(new uint[] { 0, 0x78 });                              // F9
            probeCombos.Add(new uint[] { MOD_CONTROL | MOD_ALT, 0x41 });          // Ctrl+Alt+A
            probeCombos.Add(new uint[] { MOD_CONTROL | MOD_SHIFT, 0x41 });        // Ctrl+Shift+A

            ShowPopup();                 // 逼出真实句柄
            AttachHotkey();
            if (hotkeyRegistered) { Native.UnregisterHotKey(hwnd, HOTKEY_ID); hotkeyRegistered = false; }
            if (popup.IsVisible) popup.Hide();

            Trace("=== 组合键探测开始（每项注入一次，看系统是否回调）===");

            var t = new DispatcherTimer();
            t.Interval = TimeSpan.FromMilliseconds(700);
            t.Tick += delegate { ProbeStep(); };
            t.Start();
            application.Run();
        }

        static void ProbeStep()
        {
            if (probeState == 1)
            {
                var prev = probeCombos[probeIdx];
                Trace(string.Format("  {0,-20} {1}", Describe(prev[0], prev[1]), probeGot ? "OK 收到回调" : "无响应"));
                Native.UnregisterHotKey(hwnd, HOTKEY_ID);
                hotkeyRegistered = false;
                probeIdx++;
                probeState = 0;
            }

            if (probeIdx >= probeCombos.Count)
            {
                Trace("=== 探测结束 ===");
                Quit();
                return;
            }

            var c = probeCombos[probeIdx];
            if (!Native.RegisterHotKey(hwnd, HOTKEY_ID, c[0], c[1]))
            {
                Trace(string.Format("  {0,-20} 注册失败（已被占用）", Describe(c[0], c[1])));
                probeIdx++;
                return;
            }
            hotkeyRegistered = true;
            probeGot = false;

            uint mods = c[0], vk = c[1];
            if ((mods & MOD_SHIFT) != 0) Native.keybd_event(0x10, 0, 0, UIntPtr.Zero);
            if ((mods & MOD_CONTROL) != 0) Native.keybd_event(0x11, 0, 0, UIntPtr.Zero);
            if ((mods & MOD_ALT) != 0) Native.keybd_event(0x12, 0, 0, UIntPtr.Zero);
            Native.keybd_event((byte)vk, 0, 0, UIntPtr.Zero);
            Native.keybd_event((byte)vk, 0, 2, UIntPtr.Zero);
            if ((mods & MOD_ALT) != 0) Native.keybd_event(0x12, 0, 2, UIntPtr.Zero);
            if ((mods & MOD_CONTROL) != 0) Native.keybd_event(0x11, 0, 2, UIntPtr.Zero);
            if ((mods & MOD_SHIFT) != 0) Native.keybd_event(0x10, 0, 2, UIntPtr.Zero);

            probeState = 1;
        }

        // 端到端验证：注入 Shift+`，看系统是否真的把 WM_HOTKEY 投递给程序
        static void SelfHotkey()
        {
            StartupLog("=== 热键注入测试开始 ===");

            var t0 = new DispatcherTimer();
            t0.Interval = TimeSpan.FromMilliseconds(800);
            t0.Tick += delegate
            {
                t0.Stop();
                try
                {
                    ShowPopup();          // 先显示一次，逼出真实句柄并完成注册
                    AttachHotkey();
                    StartupLog("测试前置：hotkeyRegistered=" + hotkeyRegistered + " hwnd=" + hwnd);
                    if (popup.IsVisible) popup.Hide();

                    Native.keybd_event(0x10, 0, 0, UIntPtr.Zero);        // Shift down
                    Native.keybd_event(0xC0, 0, 0, UIntPtr.Zero);        // ` down
                    Native.keybd_event(0xC0, 0, 2, UIntPtr.Zero);        // ` up
                    Native.keybd_event(0x10, 0, 2, UIntPtr.Zero);        // Shift up
                    StartupLog("已注入 Shift+`");
                }
                catch (Exception ex) { Trace("注入失败 " + ex.Message); }

                var t1 = new DispatcherTimer();
                t1.Interval = TimeSpan.FromMilliseconds(2200);
                t1.Tick += delegate
                {
                    t1.Stop();
                    Trace("RESULT popupVisible=" + popup.IsVisible);
                    Quit();
                };
                t1.Start();
            };
            t0.Start();
            application.Run();
        }

        // 验证「按下快捷键后 1.5 秒才弹出」
        static void SelfDelay()
        {
            popup.Hide();
            StartupLog("=== 延迟弹出测试开始 ===");
            ScheduleShowPopup();

            var t1 = new DispatcherTimer();
            t1.Interval = TimeSpan.FromMilliseconds(900);
            t1.Tick += delegate
            {
                t1.Stop();
                Trace("900ms  窗口可见=" + popup.IsVisible + "（应为 False）");

                var t2 = new DispatcherTimer();
                t2.Interval = TimeSpan.FromMilliseconds(900);
                t2.Tick += delegate
                {
                    t2.Stop();
                    Trace("1800ms 窗口可见=" + popup.IsVisible + "（应为 True）");
                    Quit();
                };
                t2.Start();
            };
            t1.Start();
            application.Run();
        }

        // 渲染主界面（自检）
        static void SelfMainShot(string outPath)
        {
            selftesting = true;
            if (string.IsNullOrEmpty(outPath)) outPath = Path.Combine(Path.GetTempPath(), "adhd-main.png");
            var a = new TaskItem(); a.title = "把方案发给张三，顺便确认明天的会"; a.priority = "high"; a.due = TaskItem.Now() + 25 * 60 * 1000; a.estimateMin = 30; a.kind = "writing";
            var b = new TaskItem(); b.title = "交房租"; b.priority = "mid"; b.due = TaskItem.Now() - 3 * 60 * 60 * 1000; b.estimateMin = 45; b.kind = "errand";
            var c = new TaskItem(); c.title = "看完那篇关于注意力的论文"; c.priority = "low"; c.estimateMin = 15; c.kind = "study";
            var d = new TaskItem(); d.title = "整理本周复盘"; d.priority = "mid"; d.due = TaskItem.Now() + 2 * 24 * 60 * 60 * 1000; d.estimateMin = 30; d.kind = "other";
            tasks.Add(a); tasks.Add(b); tasks.Add(c); tasks.Add(d);

            // 截图里给一行"AI 计划"，避免演示图显示"还没排提醒"
            pushPlan = new PushPlan();
            pushPlan.generatedAt = TaskItem.Now();
            pushPlan.signature = PlanSignature();
            pushPlan.source = "ai";
            pushPlan.summary = "先做逾期的，两小时后再看一次";
            var demoPlanItem = new PushItem();
            demoPlanItem.at = TaskItem.Now() + 2 * 60 * 60 * 1000;
            demoPlanItem.kind = "nudge";
            demoPlanItem.taskId = b.id;
            demoPlanItem.level = 1;
            demoPlanItem.message = "先花 10 分钟把交房租处理掉";
            pushPlan.items.Add(demoPlanItem);

            string view = "list";
            if (outPath.IndexOf("calendar") >= 0 || outPath.IndexOf("月历") >= 0) view = "calendar";
            else if (outPath.IndexOf("settings") >= 0 || outPath.IndexOf("设置") >= 0) view = "settings";
            else if (outPath.IndexOf("review") >= 0 || outPath.IndexOf("复盘") >= 0) view = "review";

            // 复盘页 / 正在做条：塞一点演示历史（只存在内存里，不会写回 tasks.json）
            if (view == "review")
            {
                Action<string, string, int, int, int, int> addDone = delegate(string title, string pri, int est, int actual, int daysAgo, int hour)
                {
                    var x = new TaskItem();
                    x.title = title;
                    x.priority = pri;
                    x.estimateMin = est;
                    x.spentMs = actual * 60000L;
                    x.done = true;
                    DateTime when = DateTime.Today.AddDays(-daysAgo).AddHours(hour).AddMinutes(10);
                    x.doneAt = new DateTimeOffset(when).ToUnixTimeMilliseconds();
                    x.due = x.doneAt + 20 * 60000L;
                    x.createdAt = x.doneAt - 86400000L;
                    x.kind = TaskKind.Guess(title);
                    tasks.Add(x);
                };
                addDone("把接口那版改完", "high", 30, 42, 0, 11);
                addDone("回张三邮件", "high", 20, 28, 1, 15);
                addDone("整理会议纪要", "mid", 20, 22, 2, 10);
                addDone("看两篇论文", "mid", 30, 26, 3, 16);
                addDone("交水电费", "low", 10, 8, 4, 21);
            }
            if (outPath.IndexOf("active") >= 0 || outPath.IndexOf("正在做") >= 0)
            {
                a.startedAt = TaskItem.Now() - 12 * 60 * 1000;
                a.lastNote = "接口那段改完了，下一步补测试";
            }

            ShowMain(view);
            if (outPath.IndexOf("tall") >= 0) { mainWin.Height = 960; mainWin.Width = 820; mainWin.UpdateLayout(); }
            var tm = new DispatcherTimer();
            tm.Interval = TimeSpan.FromMilliseconds(900);
            tm.Tick += delegate
            {
                tm.Stop();
                try { Trace("MAIN_OK " + view); SaveWindowPng(mainWin, outPath); }
                catch (Exception ex) { Trace("MAIN_FAIL " + ex.Message); }
                Quit();
            };
            tm.Start();
            application.Run();
        }

        // 渲染清单页（自检）
        static void SelfListShot(string outPath)
        {
            selftesting = true;
            if (string.IsNullOrEmpty(outPath)) outPath = Path.Combine(Path.GetTempPath(), "adhd-list.png");

            var t1 = new TaskItem();
            t1.title = "把方案发给张三，顺便确认明天的会";
            t1.priority = "high";
            t1.due = TaskItem.Now() + 25 * 60 * 1000;

            var t2 = new TaskItem();
            t2.title = "交房租";
            t2.priority = "mid";
            t2.due = TaskItem.Now() - 3 * 60 * 60 * 1000;

            var t3 = new TaskItem();
            t3.title = "看完那篇关于注意力的论文";
            t3.priority = "low";

            var t4 = new TaskItem();
            t4.title = "整理本周复盘";
            t4.priority = "mid";
            t4.due = TaskItem.Now() + 2 * 24 * 60 * 60 * 1000;

            tasks.Add(t1); tasks.Add(t2); tasks.Add(t3); tasks.Add(t4);

            ShowList();
            var tm = new DispatcherTimer();
            tm.Interval = TimeSpan.FromMilliseconds(900);
            tm.Tick += delegate
            {
                tm.Stop();
                try { Trace("LIST_OK"); SaveWindowPng(listWin, outPath); }
                catch (Exception ex) { Trace("LIST_FAIL " + ex.Message); }
                Quit();
            };
            tm.Start();
            application.Run();
        }

        static void SelfTest()
        {
            selftesting = true;
            var t = new DispatcherTimer();            t.Interval = TimeSpan.FromMilliseconds(500);
            t.Tick += delegate
            {
                t.Stop();
                try
                {
                    titleBox.Text = "测试任务：周五前把方案发给张三";
                    dueKind = "today";
                    priority = "high";
                    Submit();

                    LoadTasks();
                    var last = tasks[tasks.Count - 1];
                    Trace("SELFTEST_OK");
                    Trace("  任务数   = " + tasks.Count);
                    Trace("  标题     = " + last.title);
                    Trace("  重要度   = " + last.priority);
                    Trace("  截止     = " + DateTimeOffset.FromUnixTimeMilliseconds(last.due.Value).LocalDateTime.ToString("yyyy-MM-dd HH:mm", Inv));
                    Trace("  提醒点   = " + last.reminders.Count);
                    foreach (var r in last.reminders)
                        Trace("    " + DateTimeOffset.FromUnixTimeMilliseconds(r.at).LocalDateTime.ToString("MM-dd HH:mm", Inv) + "  " + r.label);
                    Tick();
                    Trace("  Tick     = 无异常");

                    // 自定义时间：两种方式各自算一遍，确认"按当前选中的方式"解析
                    dueKind = "custom";
                    SetCustomMode(true);
                    delayBox.Text = "7";
                    delayUnitMs = 60000;
                    long? d1 = SelectedDue();
                    long err1 = Math.Abs(d1.Value - (TaskItem.Now() + 7 * 60000));
                    Trace("  延时 7 分钟 → 误差 " + (err1 / 1000) + " 秒 " + (err1 < 5000 ? "OK" : "FAIL"));

                    SetCustomMode(false);
                    dateBox.Text = "2026-10-25";
                    timeBox.Text = "21:00";
                    long? d2 = SelectedDue();
                    string d2s = d2.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(d2.Value).LocalDateTime.ToString("yyyy-MM-dd HH:mm", Inv) : "(null)";
                    Trace("  指定日期 2026-10-25 21:00 → " + d2s + (d2s == "2026-10-25 21:00" ? " OK" : " FAIL"));

                    dueKind = "custom";
                    SetCustomMode(true);
                    delayBox.Text = "";
                    Trace("  延时留空 → " + (SelectedDue().HasValue ? "FAIL（不该给出时间）" : "拒绝保存 OK"));

                    // 默认时间：设置里选了"延时 N 分钟"后，唤出录入窗应自动预选
                    settings.autoDue = "delay";
                    settings.autoDueMinutes = 15;
                    ApplyDefaultDue();
                    bool okAuto = dueKind == "custom" && customIsDelay && delayBox.Text == "15";
                    long? dAuto = SelectedDue();
                    long errAuto = dAuto.HasValue ? Math.Abs(dAuto.Value - (TaskItem.Now() + 15 * 60000)) : long.MaxValue;
                    Trace("  默认=延时 15 分 → 预选 " + (okAuto ? "OK" : "FAIL") + " · 结果误差 " + (errAuto / 1000) + " 秒");

                    settings.autoDue = "in10";
                    ApplyDefaultDue();
                    Trace("  默认=10 分钟后 → 预选 " + (dueKind == "in10" ? "OK" : "FAIL"));
                    settings.autoDue = "none";

                    // 改期菜单里的「默认」：读的是同一份设置
                    settings.autoDue = "tomorrow";
                    string defStr = DateTimeOffset.FromUnixTimeMilliseconds(DefaultDueStamp()).LocalDateTime.ToString("yyyy-MM-dd HH:mm", Inv);
                    Trace("  改期「默认」= " + defStr + (defStr.EndsWith("21:00") ? " OK" : " FAIL"));
                    settings.autoDue = "none";

                    // 月历点某天 → 直接记在这天（录入窗要预填那一格）
                    var forDay = DateTime.Today.AddDays(2);
                    ShowPopupForDate(forDay);
                    bool okDay = dueKind == "custom" && !customIsDelay
                                 && dateBox.Text == forDay.ToString("yyyy-MM-dd", Inv);
                    Trace("POPUP-FOR-DATE " + (okDay ? "OK" : "FAIL") + " 预填=" + dateBox.Text);
                    popup.Hide();

                    // ---- v0.4：AI 拆解解析 / 接口返回解析 / 本地稍后规则（都不联网）
                    string derr;
                    var fake = "[{\"title\":\"查一下报错原因\",\"priority\":\"high\",\"delayMinutes\":10,\"kind\":\"code\"},"
                             + "{\"title\":\"写周报\",\"priority\":\"中\",\"delayMinutes\":45,\"kind\":\"writing\"}]";
                    var subs = ParseDecompose("```json\n" + fake + "\n```", out derr);
                    Trace("AI-PARSE " + (subs.Count == 2 && subs[0].priority == "high" && subs[1].priority == "mid"
                          && subs[1].minutes == 45 && subs[0].kind == "code" ? "OK" : "FAIL")
                          + " 条数=" + subs.Count + (derr == null ? "" : (" err=" + derr)));

                    string cerr;
                    string apiRaw = "{\"choices\":[{\"message\":{\"content\":\"可以\"}}]}";
                    string content = ExtractContent(apiRaw, out cerr);
                    Trace("AI-RESP " + (content == "可以" ? "OK" : "FAIL") + " content=" + content + (cerr == null ? "" : (" err=" + cerr)));

                    var snoozeHi = new TaskItem(); snoozeHi.priority = "high";
                    var snoozeLo = new TaskItem(); snoozeLo.priority = "low";
                    int hiMin = LocalSnoozeMinutes(snoozeHi), loMin = LocalSnoozeMinutes(snoozeLo);
                    Trace("AI-SNOOZE " + (hiMin >= 5 && hiMin <= 120 && loMin >= 5 && loMin <= 120 && loMin >= hiMin ? "OK" : "FAIL")
                          + " 高=" + hiMin + " 分 低=" + loMin + " 分");

                    long? cn = ParseDateTimeText("2026-10-25", "21：00");
                    string cnStr = cn.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(cn.Value).LocalDateTime.ToString("HH:mm", Inv) : "(null)";
                    Trace("TIME-CN-COLON " + (cnStr == "21:00" ? "OK" : "FAIL") + " 中文冒号→" + cnStr);

                    string tipHi = ReminderTip(snoozeHi, "还有 2 小时");
                    string tipLo = ReminderTip(snoozeLo, "今天截止");
                    Trace("AI-TIP " + (tipHi != tipLo ? "OK" : "FAIL") + " 高优先文案与低优先不同");

                    // 拖动处理里不能再对 Run 调 VisualTreeHelper（以前点"高/中/低"的文字会抛异常 → 按钮点不动）
                    var tb2 = new TextBlock();
                    var run2 = new System.Windows.Documents.Run("高");
                    tb2.Inlines.Add(run2);
                    DependencyObject p2 = null;
                    try { p2 = SafeParent(run2); } catch (Exception) { p2 = null; }
                    Trace("  Run 的父级 = " + (p2 == null ? "null FAIL" : p2.GetType().Name + (p2 is TextBlock ? " OK" : " FAIL")));

                    // ---- v0.5：计时 / 习惯 / 推荐 / AI 解析
                    var hs0 = HabitStats.Compute(tasks, DateTime.Now);
                    int estHigh = hs0.SuggestEstimate("high");
                    Trace("HABIT-EST " + (estHigh >= 5 && estHigh <= 180 ? "OK" : "FAIL") + " 高优先建议=" + estHigh + " 分");

                    var pickTask = new TaskItem();
                    pickTask.title = "自检：逾期且高优先";
                    pickTask.priority = "high";
                    pickTask.due = TaskItem.Now() - 2 * 3600 * 1000;
                    pickTask.createdAt = TaskItem.Now() - 3 * 86400000L;
                    tasks.Add(pickTask);
                    string why;
                    var pick = PickNext(DateTime.Now, out why);
                    Trace("RECOMMEND " + (pick == pickTask ? "OK" : "FAIL") + " why=" + Trim(why, 50));

                    var timed = new TaskItem();
                    timed.title = "自检：计时";
                    timed.priority = "mid";
                    tasks.Add(timed);
                    StartTask(timed);
                    timed.startedAt = TaskItem.Now() - 5 * 60 * 1000;
                    FinishTask(timed);
                    Trace("START-FINISH " + (timed.done && timed.doneAt > 0 && timed.spentMs >= 4 * 60000 ? "OK" : "FAIL")
                          + " spent=" + (timed.spentMs / 60000) + " 分");

                    var remindDone = new TaskItem();
                    remindDone.title = "自检：提醒窗完成";
                    tasks.Add(remindDone);
                    current = remindDone;
                    ResolveCurrent("done");
                    Trace("DONEAT-REMINDER " + (remindDone.done && remindDone.doneAt > 0 ? "OK" : "FAIL")
                          + " doneAt=" + remindDone.doneAt);
                    current = null;

                    var hs1 = HabitStats.Compute(tasks, DateTime.Now);
                    Trace("HABIT-SUM " + (hs1.done7 >= 2 && hs1.SummaryForAi().IndexOf("近 7 天") >= 0 ? "OK" : "FAIL")
                          + " " + hs1.SummaryForAi());

                    int pickIdx;
                    string pr, ps;
                    ParsePick("```json\n{\"index\":2,\"reason\":\"今天到期\",\"firstStep\":\"先打开文档写标题\"}\n```",
                              out pickIdx, out pr, out ps);
                    Trace("AI-PICK " + (pickIdx == 2 && pr.Length > 0 && ps.Length > 0 ? "OK" : "FAIL")
                          + " idx=" + pickIdx + " reason=" + pr);

                    // 提醒窗「开始做」：按钮存在 + 走同一条 StartTask 路径
                    var startBtn = Find<Button>(reminderWindow, "BtnStart");
                    var startTask = new TaskItem();
                    startTask.title = "自检：提醒窗开始";
                    startTask.priority = "mid";
                    tasks.Add(startTask);
                    current = startTask;
                    ResolveCurrent("start");
                    Trace("REMINDER-START " + (startBtn != null && startTask.startedAt > 0 ? "OK" : "FAIL")
                          + " startedAt=" + startTask.startedAt);
                    PauseTask(startTask);
                    current = null;

                    // 复盘结论会进入之后所有 AI 提示词
                    settings.lastInsight = "测试复盘结论：下午容易拖";
                    bool mem = HabitBrief().IndexOf("测试复盘结论") >= 0;
                    Trace("HABIT-MEMORY " + (mem ? "OK" : "FAIL"));
                    settings.lastInsight = "";

                    // ---- v0.6：高/中/低提醒窗差异
                    var lvHigh = new TaskItem();
                    lvHigh.title = "自检：高优先提醒";
                    lvHigh.priority = "high";
                    lvHigh.due = TaskItem.Now() + 2 * 3600 * 1000;
                    ApplyReminderVisual(lvHigh, "还有 2 小时");
                    var lvStart = Find<Button>(reminderWindow, "BtnStart");
                    bool lvHighOk = reminderWindow.Width == 520 && badgeText.Text == "重要"
                                    && lvStart != null && lvStart.Visibility == Visibility.Visible;
                    Trace("REMINDER-HIGH " + (lvHighOk ? "OK" : "FAIL") + " width=" + (int)reminderWindow.Width + " badge=" + badgeText.Text);

                    var lvLow = new TaskItem();
                    lvLow.title = "自检：低优先提醒";
                    lvLow.priority = "low";
                    lvLow.due = TaskItem.Now() + 2 * 3600 * 1000;
                    ApplyReminderVisual(lvLow, "今天截止");
                    bool lvLowOk = reminderWindow.Width == 440 && badgeText.Text == "顺手"
                                   && lvStart != null && lvStart.Visibility == Visibility.Collapsed;
                    Trace("REMINDER-LOW " + (lvLowOk ? "OK" : "FAIL") + " width=" + (int)reminderWindow.Width + " badge=" + badgeText.Text);

                    // ---- v0.6：AI 提醒计划解析 / 本地兜底 / 任务签名
                    var c1 = new TaskPick();
                    c1.task = new TaskItem();
                    c1.task.title = "自检：计划任务";
                    c1.task.priority = "high";
                    c1.task.estimateMin = 30;
                    c1.task.due = TaskItem.Now() + 3 * 3600 * 1000;
                    var cands = new List<TaskPick>();
                    cands.Add(c1);
                    string perr;
                    var fakePlan = ParsePushPlan(
                        "{\"summary\":\"先做计划任务\",\"items\":[{\"atMinutes\":480,\"kind\":\"nudge\",\"index\":0,\"message\":\"先写标题\"}]}",
                        cands, out perr);
                    Trace("PLAN-PARSE " + (fakePlan != null && fakePlan.items.Count == 1
                          && fakePlan.items[0].level == 0 && fakePlan.items[0].taskId == c1.task.id ? "OK" : "FAIL")
                          + (perr == null ? "" : (" err=" + perr)));

                    pushPlan = null;
                    BuildLocalPlan();
                    bool localOk = pushPlan != null && pushPlan.items.Count > 0;
                    if (localOk)
                    {
                        foreach (var it in pushPlan.items)
                        {
                            if (QuietHour(it.at) || it.at <= TaskItem.Now()) { localOk = false; break; }
                        }
                    }
                    Trace("PLAN-LOCAL " + (localOk ? "OK" : "FAIL") + " 条数=" + (pushPlan == null ? 0 : pushPlan.items.Count));
                    pushPlan = null;
                    SavePlan();

                    string sig1 = PlanSignature();
                    var sigTask = new TaskItem();
                    sigTask.title = "自检：签名";
                    tasks.Add(sigTask);
                    string sig2 = PlanSignature();
                    tasks.Remove(sigTask);
                    Trace("PLAN-SIG " + (sig1 != sig2 ? "OK" : "FAIL"));

                    // ---- v0.7：类型 / 查重 / 规则 / 装饰
                    bool kindOk = TaskKind.Guess("写方案") == "writing"
                               && TaskKind.Guess("改接口") == "code"
                               && TaskKind.Guess("回邮件") == "comms"
                               && TaskKind.Guess("交房租") == "errand"
                               && TaskKind.Guess("刷题") == "study";
                    Trace("KIND-GUESS " + (kindOk ? "OK" : "FAIL"));

                    var kindTask = new TaskItem();
                    kindTask.title = "自检：写作";
                    kindTask.priority = "high";
                    kindTask.kind = "writing";
                    int kindEst = HabitStats.Compute(tasks, DateTime.Now).SuggestEstimate("high", "writing");
                    Trace("KIND-EST " + (kindEst >= 5 && kindEst <= 180 ? "OK" : "FAIL") + " " + kindEst + " 分");

                    var dup1 = new TaskItem();
                    dup1.title = "自检：重复任务";
                    tasks.Add(dup1);
                    Trace("DUP-CHECK " + (FindSimilarOpen("自检：重复任务") == dup1 ? "OK" : "FAIL"));
                    tasks.Remove(dup1);

                    string ruleText, ruleOut;
                    SplitRuleFromReview("模式：下午容易拖。\n规则：下午不排高认知任务", out ruleText, out ruleOut);
                    Trace("RULE-PARSE " + (ruleText.IndexOf("下午容易拖") >= 0 && ruleOut == "下午不排高认知任务" ? "OK" : "FAIL")
                          + " rule=" + ruleOut);

                    Trace("STYLES " + (Styles.Length == 5 && StyleNames.Length == 5 ? "OK" : "FAIL"));
                    bool decoOk = Deco.Make(0, 0, 20, "#888888", 1) != null
                               && Deco.Make(1, 0, 20, "#888888", 1) != null
                               && Deco.Make(2, 1, 20, "#888888", 1) != null
                               && Deco.Make(3, 0, 20, "#888888", 1) != null
                               && Deco.Make(4, 0, 20, "#888888", 1) != null;
                    Trace("DECO " + (decoOk ? "OK" : "FAIL"));

                    // ---- v0.8：开机自启命令 / 日志脱敏（不碰真实注册表）
                    string autoCmd = AutoStartCommand();
                    bool autoOk = autoCmd.IndexOf("--startup", StringComparison.Ordinal) >= 0
                               && autoCmd.IndexOf("\"", StringComparison.Ordinal) == 0;
                    Trace("AUTOSTART-CMD " + (autoOk ? "OK" : "FAIL") + " " + autoCmd);

                    string masked = MaskSecrets("{\"aiKey\":\"sk-secret\",\"mobileToken\":\"tok-secret\",\"theme\":0}");
                    Trace("LOG-MASK " + (masked.IndexOf("sk-secret", StringComparison.Ordinal) < 0
                          && masked.IndexOf("tok-secret", StringComparison.Ordinal) < 0
                          && masked.IndexOf("\"aiKey\":\"***\"", StringComparison.Ordinal) >= 0
                          && masked.IndexOf("\"mobileToken\":\"***\"", StringComparison.Ordinal) >= 0 ? "OK" : "FAIL")
                          + " " + masked);

                    // 取舍 prompt 只带今天任务，未来任务不能混进去
                    var triToday = new TaskItem();
                    triToday.title = "自检：今天取舍";
                    triToday.priority = "high";
                    triToday.due = TaskItem.Now() + 3600 * 1000;
                    triToday.estimateMin = 120;
                    var triFuture = new TaskItem();
                    triFuture.title = "自检：未来取舍";
                    triFuture.priority = "high";
                    triFuture.due = TaskItem.Now() + 3 * 86400000L;
                    triFuture.estimateMin = 120;
                    tasks.Add(triToday);
                    tasks.Add(triFuture);
                    string triPrompt = BuildTriagePrompt(HabitStats.Compute(tasks, DateTime.Now),
                                                        RankOpenTasks(DateTime.Now, HabitStats.Compute(tasks, DateTime.Now)));
                    Trace("TRIAGE-PROMPT " + (triPrompt.IndexOf(triToday.title, StringComparison.Ordinal) >= 0
                          && triPrompt.IndexOf(triFuture.title, StringComparison.Ordinal) < 0 ? "OK" : "FAIL")
                          + " 只带今天任务");
                    tasks.Remove(triToday);
                    tasks.Remove(triFuture);
                }
                catch (Exception ex)
                {
                    Trace("SELFTEST_FAIL " + ex.Message);
                }
                Quit();
            };
            t.Start();
            application.Run();
        }
    }
}
