param(
  [string]$Project = "shuiyue"
)
# 把 cloudflare-pages/public 部署到 Cloudflare Pages。产物由 publish.ps1 生成。
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# 这台机器调 Cloudflare API 需要走本机代理，端口漂移时调用前自己设 HTTPS_PROXY 覆盖
if (-not $env:HTTPS_PROXY) {
  # 本机代理只在真开着时启用；别人机器没有这个端口就直连
  $probe = Test-NetConnection -ComputerName 127.0.0.1 -Port 7897 -InformationLevel Quiet -WarningAction SilentlyContinue
  if ($probe) { $env:HTTPS_PROXY = "http://127.0.0.1:7897" }
}

$pub = "cloudflare-pages\public"
foreach ($f in @("index.html", "update.json")) {
  if (-not (Test-Path (Join-Path $pub $f))) { throw "缺少 $f，先跑 publish.ps1" }
}

Write-Host "确认登录态…"
npx --yes wrangler whoami
if ($LASTEXITCODE -ne 0) {
  throw "wrangler 未登录或登录态过期。需要在这台电脑的浏览器里跑 npx wrangler login 点允许（手机上完成不了）。"
}

Write-Host "确保 Pages 项目存在…"
npx --yes wrangler pages project create $Project --production-branch main 2>&1 | Out-String | Write-Host

Write-Host "部署 $pub …"
npx --yes wrangler pages deploy $pub --project-name $Project --branch main --commit-dirty=true
