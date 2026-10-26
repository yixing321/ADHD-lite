# ADHD-lite Android 客户端

独立 APK，和桌面端 `ADHD-lite.exe` 通过 Tailscale / 同一 Wi-Fi 同步。

## 安装

1. 把 `dist\ADHD-lite-android-v0.11.2.apk` 发到手机（微信/QQ/数据线都可）。
2. 在手机上允许「安装未知来源应用」，安装 APK。
3. 首次打开会请求通知权限；Android 12+ 再点一次「允许精确闹钟」，并在设置里把电池优化设为「不受限制」，否则提醒可能不准点。
4. 在电脑端：`ADHD-lite.exe` → 设置 → 手机 / 同步 → 点亮「手机同步」。
5. 把设置页里的 **地址** 和 **令牌** 填进手机 App：
   - 都登录 Tailscale 时用 `http://100.x.x.x:17890`（推荐）；
   - 同一 Wi-Fi 用 `http://192.168.x.x:17890`。
6. 点「保存并连接」。电脑开着时手机即可增删改、开始/暂停、计时、AI 拆解/取舍。

## v0.11.2 新增

- **4 种底色**：柔和 / 冷白 / 素雅 / 墨夜，每种 6 个强调色；墨夜固定深色。默认「柔和 · 暖砂 · 浅色」。
- **点按反馈**：按钮/分页/任务/日历格按下缩放变暗；完成/开始/暂停/改期先显示「正在…」，成功后提示「已完成 / 已开始」；同步中点按会提示“正在同步”，不再静默。
- **连接诊断**：设置页「连接诊断」分别显示 `/health`（电脑可达）和 `/state`（令牌通过），再附手机通知状态。
- **连接状态行**：设置页直接显示“已连接 HH:mm:ss · 任务 N 条”或具体失败原因。
- 延时、响铃通道、通道设置沿用 v0.11.1。

## v0.11.1 新增

- **「延时」截止**：今天 / 1 小时后 / 明天 / **延时** / 自定义；延时填数字 + 选「分钟后 / 小时后 / 秒后」，和桌面录入窗一致。
- **外观收口**：更深的 6 个强调色 + 页头/统计/分页/任务标签/留白纹样。
- **通知换成响铃通道**：新通道 `adhd_alarm_v1` 使用系统闹铃声音 + 长震动 + 锁屏可见；设置页新增「通知通道设置」，可直接打开系统里的「横幅/声音/震动」开关。
- **测试通知（5 秒）**改用 Handler 直发，不依赖精确闹钟权限，能单独验证「通道 + 横幅 + 声音」。
- 设置页返回键、版本/更新、5 秒测试沿用 v0.11.0。

## v0.11.0 新增

- **设置页返回键**：右上角「← 返回主界面」；同步轮询不再把设置页关掉。Android 返回键按「设置 → 主界面 → 今天 → 退出」处理。
- **版本与更新**：设置页显示 App 版本；电脑端打包新 APK 后提示新版本，点「下载并安装」从电脑 `/apk` 下载并交给系统安装器（Android 仍会确认一次，不能静默安装）。
- **通知权限/状态**：锁屏可见、精确闹钟、后台、已排闹钟数状态和入口。
- 自定义截止、月历、AI 计划本地通知沿用 v0.10.1。

## 提醒是怎么工作的

- 每次同步后，手机把未完成任务的截止时间写进 Android `AlarmManager`；
- 到点由**手机本地**发通知，所以电脑关机也能响；
- 手机重启后 `BootReceiver` 会从缓存重新排闹钟；
- 任务在手机上被完成/改期/稍后时，会先更新本地闹钟，再尝试同步电脑。
- 到点通知走 `adhd_alarm_v1` 响铃通道；如果只有通知栏条目、没有横幅或声音，点「通知通道设置」把「横幅（悬浮通知）/ 声音 / 震动」打开，并检查系统勿扰/静音。
- 通知不弹时先看设置页：通知权限、通道、响铃、精确闹钟、电池优化（设为「不受限制」）。「测试通知（5 秒）」只验证手机本地通道，不需要电脑开着。

## 同步 / 离线

- 手机每 3 秒轮询一次电脑；电脑端改动会出现在手机上。
- 手机断网时显示缓存；完成/开始/暂停/改期/新增会进入本地 outbox，回连后补发。
- 新增任务的临时 id 以 `local-` 开头，同步成功后由电脑端返回的真实任务替换。
- 没有做复杂的多人冲突合并：同一任务同时改，以电脑端最后一次写入为准。

## 安全

- 只通过 Tailscale 或局域网访问，不暴露公网；Tailscale 本身加密。
- 手机只拿任务数据，**不拿 `settings.json` 和 API Key**。
- 令牌只存在电脑的 `settings.json` 和手机 App 的本地存储；`startup.log` 里会脱敏成 `***`。
- 局域网 HTTP 没有额外 TLS；不要在公共 Wi-Fi 上开这个端口。

## 构建

需要 JDK + Android SDK（platform 35 / build-tools 35.0.0）：

```powershell
cd D:\ADHD清单
powershell -NoProfile -ExecutionPolicy Bypass -File android\build-apk.ps1
```

产物：`android\dist\ADHD-lite-android-v0.11.2.apk`（versionCode 14，debug 签名）。

说明：`aapt2` 在中文路径下会报 `failed to open directory`，脚本会先把 `app/` 复制到 ASCII 临时目录编译。脚本必须保存为 **UTF-8 with BOM**（Windows PowerShell 5.1 否则会把中文注释按 ANSI 解析，变量会变空）。

## 目录

```
app/AndroidManifest.xml
app/assets/mobile.html          手机界面（HTML/CSS/JS）
app/java/com/adhd/lite/
  MainActivity.java             WebView + JS bridge（版本/更新/权限）
  AlarmScheduler.java           把任务截止时间排进 AlarmManager
  ReminderReceiver.java         到点发通知
  BootReceiver.java             重启后重排
  Notifications.java            通知渠道 / 锁屏可见 / 通知内容
  ApkProvider.java              v0.11 给系统安装器读 update.apk 的最小 ContentProvider
build-apk.ps1                   aapt2 + javac + d8 + zipalign + apksigner
debug.keystore                  调试签名（升级 APK 必须用同一个）
dist/                           生成的 APK（v0.11.2 已提交，便于直接安装）
```
