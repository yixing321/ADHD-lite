param(
    [string]$Sdk = "",
    [string]$JavaHome = "C:\Program Files\Java\jdk-22"
)

$ErrorActionPreference = "Stop"
$src = $PSScriptRoot
if (-not $Sdk) {
    if ($env:ANDROID_HOME) { $Sdk = $env:ANDROID_HOME }
    elseif (Test-Path (Join-Path $env:LOCALAPPDATA "Android\Sdk")) { $Sdk = Join-Path $env:LOCALAPPDATA "Android\Sdk" }
    else { throw "找不到 Android SDK，请设置 ANDROID_HOME 或传 -Sdk" }
}
$buildTools = Join-Path $Sdk "build-tools\35.0.0"
$androidJar = Join-Path $Sdk "platforms\android-35\android.jar"
$javac = Join-Path $JavaHome "bin\javac.exe"
$keytool = Join-Path $JavaHome "bin\keytool.exe"
foreach ($p in @($androidJar, (Join-Path $buildTools "aapt2.exe"), (Join-Path $buildTools "d8.bat"),
                 (Join-Path $buildTools "zipalign.exe"), (Join-Path $buildTools "apksigner.bat"), $javac, $keytool)) {
    if (-not (Test-Path $p)) { throw "缺少工具: $p" }
}

# aapt2 在中文路径下会报 "failed to open directory"，所以整个构建放到 ASCII 临时目录。
$root = Join-Path ([System.IO.Path]::GetTempPath()) "adhd-android-build"
$app = Join-Path $root "app"
$build = Join-Path $root "build"
$dist = Join-Path $src "dist"
$apkName = "ADHD-lite-android-v0.11.9.apk"
$finalLocal = Join-Path $root $apkName
$finalApk = Join-Path $dist $apkName
$keystore = Join-Path $src "debug.keystore"

if (Test-Path $root) { Remove-Item -Recurse -Force $root }
New-Item -ItemType Directory -Force -Path $root, $dist | Out-Null
Copy-Item (Join-Path $src "app") $app -Recurse
New-Item -ItemType Directory -Force -Path (Join-Path $build "gen"), (Join-Path $build "classes"), (Join-Path $build "dex") | Out-Null

if (-not (Test-Path $keystore)) {
    & $keytool -genkeypair -v -keystore $keystore -storepass android -alias androiddebugkey `
        -keypass android -dname "CN=Android Debug,O=Android,C=US" -keyalg RSA -keysize 2048 -validity 10000 | Out-Null
}
$workstore = Join-Path $root "debug.keystore"
Copy-Item $keystore $workstore

Write-Host "[1/6] aapt2 compile resources"
& (Join-Path $buildTools "aapt2.exe") compile --dir (Join-Path $app "res") -o (Join-Path $build "res.zip")
if ($LASTEXITCODE -ne 0) { throw "aapt2 compile 失败" }

Write-Host "[2/6] aapt2 link"
& (Join-Path $buildTools "aapt2.exe") link -o (Join-Path $build "base.apk") `
    -I $androidJar --manifest (Join-Path $app "AndroidManifest.xml") `
    -R (Join-Path $build "res.zip") --java (Join-Path $build "gen") `
    --auto-add-overlay -A (Join-Path $app "assets")
if ($LASTEXITCODE -ne 0) { throw "aapt2 link 失败" }

Write-Host "[3/6] javac"
$sources = @(Get-ChildItem (Join-Path $app "java") -Recurse -Filter *.java | ForEach-Object { $_.FullName })
$rjava = @(Get-ChildItem (Join-Path $build "gen") -Recurse -Filter *.java | ForEach-Object { $_.FullName })
& $javac --release 8 -encoding UTF-8 -classpath $androidJar -d (Join-Path $build "classes") ($sources + $rjava)
if ($LASTEXITCODE -ne 0) { throw "javac 失败" }

Write-Host "[4/6] d8"
$classes = @(Get-ChildItem (Join-Path $build "classes") -Recurse -Filter *.class | ForEach-Object { $_.FullName })
& (Join-Path $buildTools "d8.bat") --lib $androidJar --min-api 23 --output (Join-Path $build "dex") $classes
if ($LASTEXITCODE -ne 0) { throw "d8 失败" }

Write-Host "[5/6] package dex"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open((Join-Path $build "base.apk"), "Update")
try {
    $entry = $zip.CreateEntry("classes.dex", [System.IO.Compression.CompressionLevel]::Optimal)
    $out = $entry.Open()
    try {
        $in = [System.IO.File]::OpenRead((Join-Path $build "dex\classes.dex"))
        try { $in.CopyTo($out) } finally { $in.Dispose() }
    } finally { $out.Dispose() }
} finally { $zip.Dispose() }

& (Join-Path $buildTools "zipalign.exe") -f 4 (Join-Path $build "base.apk") (Join-Path $build "aligned.apk")
if ($LASTEXITCODE -ne 0) { throw "zipalign 失败" }

Write-Host "[6/6] sign"
& (Join-Path $buildTools "apksigner.bat") sign --ks $workstore --ks-key-alias androiddebugkey `
    --ks-pass pass:android --key-pass pass:android --out $finalLocal (Join-Path $build "aligned.apk")
if ($LASTEXITCODE -ne 0) { throw "apksigner 失败" }
& (Join-Path $buildTools "apksigner.bat") verify --verbose $finalLocal
if ($LASTEXITCODE -ne 0) { throw "APK 验签失败" }
Copy-Item $finalLocal $finalApk -Force

$size = [math]::Round((Get-Item $finalApk).Length / 1KB, 1)
Write-Host ""
Write-Host "APK 生成: $finalApk"
Write-Host "大小: $size KB"
