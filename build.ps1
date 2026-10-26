$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# 编译器用系统自带的 .NET Framework 4；优先 64 位目录，找不到再退回 32 位
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc)) { throw "找不到 .NET Framework 4 的 csc.exe，装一下 .NET Framework 4.x 再试" }

$need = @("app.cs", "assets\app.ico", "Microsoft.Web.WebView2.Core.dll",
          "Microsoft.Web.WebView2.WinForms.dll", "WebView2Loader.dll")
foreach ($f in $need) { if (-not (Test-Path $f)) { throw "缺少 $f，无法编译（WebView2 的 dll 随仓库提供，确认已完整拉取）" } }

& $csc /nologo /target:winexe /platform:x64 /win32icon:"assets\app.ico" /out:"水月清影.exe" `
  /reference:"Microsoft.Web.WebView2.Core.dll" `
  /reference:"Microsoft.Web.WebView2.WinForms.dll" `
  /reference:System.dll /reference:System.Core.dll /reference:System.Data.dll `
  /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
  /reference:System.Web.Extensions.dll /reference:System.Xml.dll `
  app.cs
Write-Host "=== exit: $LASTEXITCODE ==="
if (Test-Path "水月清影.exe") {
  Get-Item "水月清影.exe" | Select-Object Name, Length, LastWriteTime | Format-List
}
