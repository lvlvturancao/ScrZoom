param(
  [string]$Project = "shuiyue"
)
# 把 cloudflare-pages/public 部署到 Cloudflare Pages。产物由 publish.ps1 生成。
# npx 的代理提示写在 stderr，Stop 会把它当致命错误提前中断；这里改为按退出码显式判断
$ErrorActionPreference = "Continue"
Set-Location $PSScriptRoot

# 这台机器调 Cloudflare API 需要走本机代理，端口漂移时调用前自己设 HTTPS_PROXY 覆盖
if (-not $env:HTTPS_PROXY) {
  # 本机代理只在真开着时启用；别人机器没有这个端口就直连
  $probe = Test-NetConnection -ComputerName 127.0.0.1 -Port 7897 -InformationLevel Quiet -WarningAction SilentlyContinue
  if ($probe) { $env:HTTPS_PROXY = "http://127.0.0.1:7897" }
}

# 全局约束：Cloudflare 用 API Token，不走浏览器 OAuth。
# Token 放在 ~/.cloudflare/api-token，账户号从 wrangler 缓存补上；没有文件时才退回原登录态。
$tokenFile = Join-Path $env:USERPROFILE ".cloudflare\api-token"
if (-not $env:CLOUDFLARE_API_TOKEN -and (Test-Path $tokenFile)) {
  $env:CLOUDFLARE_API_TOKEN = (Get-Content $tokenFile -Raw).Trim()
  Write-Host "已读取 Cloudflare API Token（不显示内容）"
}
$acctFile = Join-Path $PSScriptRoot ".wrangler\cache\pages.json"
if (-not $env:CLOUDFLARE_ACCOUNT_ID -and (Test-Path $acctFile)) {
  try {
    $acct = (Get-Content $acctFile -Raw | ConvertFrom-Json).account_id
    if ($acct) { $env:CLOUDFLARE_ACCOUNT_ID = ([string]$acct).Trim() }
  } catch { }
}

$pub = "cloudflare-pages\public"
foreach ($f in @("index.html", "update.json")) {
  if (-not (Test-Path (Join-Path $pub $f))) { throw "缺少 $f，先跑 publish.ps1" }
}

Write-Host "确认登录态…"
# npx 会把「检测到代理」之类的提示写到 stderr；合并收集，别让脚本误判成致命错误
$who = (npx --yes wrangler whoami 2>&1 | Out-String)
Write-Host $who
if ($LASTEXITCODE -ne 0) {
  throw "wrangler 未登录或登录态过期。需要在这台电脑的浏览器里跑 npx wrangler login 点允许（手机上完成不了）。"
}

Write-Host "确保 Pages 项目存在…"
$mk = (npx --yes wrangler pages project create $Project --production-branch main 2>&1 | Out-String)
Write-Host $mk

Write-Host "部署 $pub …"
$dep = (npx --yes wrangler pages deploy $pub --project-name $Project --branch main --commit-dirty=true 2>&1 | Out-String)
Write-Host $dep
if ($LASTEXITCODE -ne 0) { throw "部署失败，原因见上面的 wrangler 输出" }
