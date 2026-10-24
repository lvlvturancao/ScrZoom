$ErrorActionPreference = "Stop"
Set-Location "D:\code\scrcpy-win64"
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
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
