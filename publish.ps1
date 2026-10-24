param(
  [string]$Notes = "",
  [string]$BaseUrl = "https://shuiyue-30d.pages.dev",
  [switch]$Force,
  [switch]$SkipBuild
)
# 发布一条链：编译 → 打 zip → 算校验和 → 写 update.json。版本号只认 app.cs 里那一个来源。
$ErrorActionPreference = "Stop"
Set-Location "D:\code\scrcpy-win64"

$src = Get-Content "app.cs" -Raw -Encoding UTF8
$ver = [regex]::Match($src, 'AppVersion\s*=\s*"([^"]+)"').Groups[1].Value
$codeStr = [regex]::Match($src, 'AppVersionCode\s*=\s*(\d+)').Groups[1].Value
if (-not $ver -or -not $codeStr) { throw "app.cs 里没找到 AppVersion / AppVersionCode" }
$code = [int]$codeStr
Write-Host "版本 $ver（编号 $code）"

if (-not $SkipBuild) {
  & powershell -NoProfile -ExecutionPolicy Bypass -File ".\build.ps1"
  if ($LASTEXITCODE -ne 0) { throw "编译失败，未发布" }
}
if (-not (Test-Path "水月清影.exe")) { throw "找不到 水月清影.exe" }

$pub = "cloudflare-pages\public"
New-Item -ItemType Directory -Force -Path (Join-Path $pub "shots") | Out-Null
$zipName = "shuiyue-$ver.zip"
$zipPath = Join-Path $pub $zipName
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

# zip 顶层就是 exe + 前端 + engine/，解压出来即可直接运行
$parts = @(
  "水月清影.exe", "index.html",
  "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll", "WebView2Loader.dll",
  "LICENSE", "engine"
)
foreach ($p in $parts) { if (-not (Test-Path $p)) { throw "发布缺少 $p" } }
Write-Host "打包中…"
Compress-Archive -Path $parts -DestinationPath $zipPath -CompressionLevel Optimal

$size = (Get-Item $zipPath).Length
$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower()
$today = Get-Date -Format "yyyy-MM-dd"
if (-not $Notes) { $Notes = "版本 $ver。" }
$notesEsc = $Notes.Replace('\', '\\').Replace('"', '\"').Replace("`r`n", '\n').Replace("`n", '\n')

$json = "{`n" +
  "  `"versionName`": `"$ver`",`n" +
  "  `"versionCode`": $code,`n" +
  "  `"title`": `"水月清影 $ver`",`n" +
  "  `"notes`": `"$notesEsc`",`n" +
  "  `"url`": `"$BaseUrl/$zipName`",`n" +
  "  `"size`": $size,`n" +
  "  `"sha256`": `"$hash`",`n" +
  "  `"force`": $(if ($Force) { 'true' } else { 'false' }),`n" +
  "  `"published`": `"$today`"`n" +
  "}`n"
[System.IO.File]::WriteAllText((Join-Path (Get-Location) "$pub\update.json"), $json, (New-Object System.Text.UTF8Encoding($false)))

Write-Host ""
Write-Host "=== 发布产物已就绪 ==="
Write-Host "安装包  $zipName  $([math]::Round($size/1MB,1)) MB"
Write-Host "校验和  $hash"
Write-Host "清单    cloudflare-pages\public\update.json"
Write-Host "下载链接  $BaseUrl/$zipName"
Write-Host ""
Write-Host "下一步：跑 deploy-pages.ps1 部署，或先本地核对 update.json 内容。"
