param(
    [string]$Version = "v0.11.0"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "ADHD-lite.exe"
if (-not (Test-Path $exe)) {
    throw "找不到 ADHD-lite.exe，先运行 build.cmd"
}

$dist = Join-Path $root "dist"
$stageName = "ADHD-lite-$Version-win64"
$stage = Join-Path $dist $stageName
$zip = Join-Path $dist "$stageName.zip"

if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item $exe (Join-Path $stage "ADHD-lite.exe")

$lines = @(
    "ADHD-lite $Version 绿色版",
    "========================",
    "",
    "双击 ADHD-lite.exe 即用，不需要安装。",
    "数据会写在 exe 旁边的 data\ 目录；删掉 data\ 就是全新开始。",
    "",
    "快捷键：Ctrl+Num+（小键盘加号）",
    "退出：录入窗「退出」、悬浮球右键「退出」、或托盘图标右键「退出」。",
    "",
    "开机自启：设置 → 启动 → 打开「开机自启（登录后静默运行）」。",
    "它只写当前用户的 HKCU\Software\Microsoft\Windows\CurrentVersion\Run，",
    "不需要管理员；移动 exe 后下次启动会自动更新路径。",
    "",
    "AI 功能可选：设置 → 智能助手里填 DeepSeek API Key。",
    "不填也能正常记任务、提醒、计时和复盘（AI 自动退回本地规则）。",
    "",
    "注意：exe 未签名，Windows/安全软件可能提示未知程序；选择「仍要运行」或加入信任。",
    "它不截屏、不读取其它窗口；只在主动点 AI 按钮或开着「AI 安排提醒时间」时联网。"
)
$readme = $lines -join "`r`n"
[System.IO.File]::WriteAllText((Join-Path $stage "使用说明.txt"), $readme, (New-Object System.Text.UTF8Encoding($false)))

if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path @((Join-Path $stage "ADHD-lite.exe"), (Join-Path $stage "使用说明.txt")) -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
$size = (Get-Item $zip).Length
"打包完成: $zip"
"大小: {0:N1} KB" -f ($size / 1KB)
"SHA256: $hash"
$hash | Set-Content -Path (Join-Path $dist "SHA256.txt") -Encoding ASCII
Remove-Item -Recurse -Force $stage
