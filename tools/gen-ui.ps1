# 从 ui\*.xaml 生成 src\Ui.cs（XAML 以 C# 逐字字符串内嵌，双引号成对转义）
# 用法（在项目根目录）：powershell -ExecutionPolicy Bypass -File tools\gen-ui.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$popup = Join-Path $root 'ui\popup.xaml'
$reminder = Join-Path $root 'ui\reminder.xaml'
$out = Join-Path $root 'src\Ui.cs'

function Read-Xaml([string]$path) {
    $text = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
    $text = $text.TrimEnd([char]13, [char]10)
    return $text.Replace('"', '""')
}

$sb = New-Object System.Text.StringBuilder
[void]$sb.Append("// 自动生成，勿手改。来源：ui/popup.xaml、ui/reminder.xaml（tools/gen-ui.ps1）`r`n")
[void]$sb.Append("namespace AdhdLite`r`n{`r`n    public static class Ui`r`n    {`r`n")
[void]$sb.Append("        public const string PopupXaml = @`"`r`n")
[void]$sb.Append((Read-Xaml $popup))
[void]$sb.Append("`r`n`";`r`n")
[void]$sb.Append("        public const string ReminderXaml = @`"`r`n")
[void]$sb.Append((Read-Xaml $reminder))
[void]$sb.Append("`r`n`";`r`n")
[void]$sb.Append("    }`r`n}`r`n")

[System.IO.File]::WriteAllText($out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
Write-Host "已生成 $out"
