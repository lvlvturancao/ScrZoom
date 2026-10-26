# 投屏启动菜单
# 电脑分辨率自动读取，换显示器不用改。虚拟屏字的大小只由下面 $vdDpi 一个数字控制。

$scrcpy = Join-Path $PSScriptRoot 'scrcpy.exe'
$adb    = Join-Path $PSScriptRoot 'adb.exe'

Add-Type -AssemblyName System.Windows.Forms
$b  = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$pc = '{0}x{1}' -f $b.Width, $b.Height

# 虚拟屏密度：数字越小，同样分辨率里装下的内容越多、字越小
$vdDpi = 420

# 每个虚拟屏窗口的分辨率与密度。想更大更清晰就改这里，例如 1920x1080/420
$vdSize = '1280x720/320'

# 共用参数。鼠标保持默认模式，绝不能加物理鼠标参数，否则指针会跑去设备自己那块屏上
$common = @('--keyboard=uhid', '--video-bit-rate=16M', '--max-fps=60')

# 改动设备分辨率前，把设备当时的原值记在这里；正常还原后删掉。文件还在就说明上次是异常退出
$stateFile = Join-Path $PSScriptRoot 'last-scale.txt'

function Get-Device {
    $lines = & $adb devices 2>&1 | Select-Object -Skip 1
    foreach ($l in $lines) {
        if ($l -match '^(\S+)\s+device$') { return $matches[1] }
    }
    return $null
}

# 读取当前生效值，并记下原本有没有被手动改过，退出时按原样还回去
function Get-Wm {
    param([string]$dev, [string]$what)
    $raw = (& $adb -s $dev shell wm $what 2>&1) -join ' '
    $p = if ($raw -match 'Physical[^:]*:\s*([0-9x]+)') { $matches[1] } else { $null }
    $o = if ($raw -match 'Override[^:]*:\s*([0-9x]+)') { $matches[1] } else { $null }
    [pscustomobject]@{
        Physical = $p
        Override = $o
        Current  = $(if ($o) { $o } else { $p })
    }
}

function Restore-Wm {
    param([string]$dev, [string]$what, $orig)
    if ($orig.Override) { & $adb -s $dev shell wm $what $orig.Override | Out-Null }
    else                { & $adb -s $dev shell wm $what reset         | Out-Null }
}

# 改分辨率之前，把设备当时的原值写进记录文件；正常还原后删掉
function Save-State {
    param([string]$dev, $origSize, $origDpi)
    $s = $(if ($origSize.Override) { $origSize.Override } else { 'RESET' })
    $d = $(if ($origDpi.Override)  { $origDpi.Override  } else { 'RESET' })
    "$dev|$s|$d" | Set-Content -Path $stateFile -Encoding UTF8
}

function Apply-Recorded {
    param([string]$dev, [string]$sizeVal, [string]$dpiVal)
    if ($sizeVal -eq 'RESET') { & $adb -s $dev shell wm size reset     | Out-Null }
    else                      { & $adb -s $dev shell wm size $sizeVal  | Out-Null }
    if ($dpiVal -eq 'RESET')  { & $adb -s $dev shell wm density reset  | Out-Null }
    else                      { & $adb -s $dev shell wm density $dpiVal | Out-Null }
}

function Device-Online {
    param([string]$dev)
    $lines = & $adb devices 2>&1 | Select-Object -Skip 1
    foreach ($l in $lines) { if ($l -match "^$([regex]::Escape($dev))\s+device$") { return $true } }
    return $false
}

# 启动时调用：记录文件还在，说明上次没还原
function Check-Leftover {
    if (-not (Test-Path $stateFile)) { return }
    $p = ((Get-Content $stateFile -Encoding UTF8 | Select-Object -First 1) -split '\|')
    if ($p.Count -lt 3) { Remove-Item $stateFile -Force; return }
    $dev = $p[0]; $sizeVal = $p[1]; $dpiVal = $p[2]
    $szTxt = $(if ($sizeVal -eq 'RESET') { '出厂默认' } else { $sizeVal })
    $dpTxt = $(if ($dpiVal -eq 'RESET') { '出厂默认' } else { $dpiVal })
    Write-Host ''
    Write-Host ("发现上次没还原的设备：$dev（应该还原成 分辨率 $szTxt / 密度 $dpTxt）") -ForegroundColor Yellow
    if (-not (Device-Online -dev $dev)) {
        Write-Host '这台设备现在没连上，先插上再运行一次脚本。' -ForegroundColor Red
        return
    }
    $ans = Read-Host '现在就还原吗？回车=还原，输 n 跳过'
    if ($ans -notin @('n', 'N')) {
        Apply-Recorded -dev $dev -sizeVal $sizeVal -dpiVal $dpiVal
        Remove-Item $stateFile -Force
        Write-Host '已还原，记录清除。' -ForegroundColor Green
    }
}

function Start-Cast {
    param([string[]]$params)
    Write-Host ('启动：scrcpy ' + ($params -join ' ')) -ForegroundColor Green
    & $scrcpy @params
    Write-Host '投屏已结束。' -ForegroundColor DarkGray
}

# 开一个独立的虚拟屏窗口，不占菜单，可以连着开好几个
function Start-AppWindow {
    param([string]$name, [switch]$Blank)
    $dev = Get-Device
    if (-not $dev) { Write-Host '没找到已授权的设备。' -ForegroundColor Red; return }
    $p = @('-s', $dev, "--new-display=$vdSize", '--video-bit-rate=12M', '--keyboard=uhid')
    if (-not $Blank) {
        # 输的是包名（带点、纯英文数字）就直接按包名开，否则按应用名模糊匹配
        if ($name -match '^[A-Za-z0-9_.]+$' -and $name -match '\.') {
            $p += ('--start-app=' + $name)
        }
        else {
            $p += ('--start-app=?' + $name)
        }
    }
    Start-Process -FilePath $scrcpy -ArgumentList $p -WorkingDirectory $PSScriptRoot | Out-Null
    Write-Host ('  已发出：' + ($p -join ' ')) -ForegroundColor DarkGray
    # 连着开多个窗口时留点间隔，两个会话同时抢调试通道会有一个建屏失败
    Start-Sleep -Milliseconds 2500
    if ($Blank) {
        Write-Host '已开一块空白虚拟屏。' -ForegroundColor Green
    }
    else {
        Write-Host ("正在为「$name」开一个窗口。手机上找应用要几秒，稍等。" -f $name) -ForegroundColor Green
    }
}

function Show-Windows {
    $ps = @(Get-Process scrcpy -ErrorAction SilentlyContinue)
    if ($ps.Count -eq 0) { Write-Host '当前没有投屏窗口。'; return }
    Write-Host ("当前有 {0} 个投屏窗口：" -f $ps.Count) -ForegroundColor Cyan
    foreach ($p in $ps) { Write-Host ("  进程 {0}  标题 {1}" -f $p.Id, $p.MainWindowTitle) }
}

function Stop-AllWindows {
    $ps = @(Get-Process scrcpy -ErrorAction SilentlyContinue)
    if ($ps.Count -eq 0) { Write-Host '没有需要关闭的投屏窗口。'; return }
    foreach ($p in $ps) { [void]$p.CloseMainWindow() }
    Write-Host ("已关闭 {0} 个投屏窗口，手机上对应的虚拟屏同时销毁。" -f $ps.Count) -ForegroundColor Green
}

function Start-ScaledCast {
    param([string]$dev, [double]$factor, [switch]$AutoFit, [switch]$Fill)

    $origSize = Get-Wm -dev $dev -what 'size'
    $origDpi  = Get-Wm -dev $dev -what 'density'
    if (-not $origSize.Physical) { Write-Host '读不到设备出厂分辨率，取消。' -ForegroundColor Red; return }

    $extra = @()
    $wh0 = $origSize.Physical -split 'x'

    if ($Fill) {
        # 先按电脑分辨率设一次，读回设备实际允许到的尺寸，再照电脑比例修正，避免留下黑边
        & $adb -s $dev shell wm size $pc | Out-Null
        Start-Sleep -Milliseconds 800
        $got = Get-Wm -dev $dev -what 'size'
        $gw = ($got.Current -split 'x')
        $k = [math]::Min([int]$gw[0] / $b.Width, [int]$gw[1] / $b.Height)
        $newSize = '{0}x{1}' -f [math]::Round($b.Width * $k), [math]::Round($b.Height * $k)
        $newDpi  = [math]::Round([int]$origDpi.Physical * (([int](($newSize -split 'x')[1])) / [int]$wh0[1]))
        $extra = @('--fullscreen')
        if ($k -lt 1) {
            Write-Host ("电脑屏幕 $pc，这台设备最高只肯开到 $newSize（电脑分辨率的 {0:N2} 倍）/ 密度 $newDpi。设备本机屏幕显示比例会改变，应用请在投屏后再打开，关窗自动还原。" -f $k) -ForegroundColor Yellow
        }
        else {
            Write-Host ("按屏幕最大分辨率：设备改成 $newSize / 密度 $newDpi。设备本机屏幕显示比例会改变，应用请在投屏后再打开，关窗自动还原。") -ForegroundColor Yellow
        }
    }
    else {
        if ($AutoFit) {
            # 取宽、高两个方向里较小的比值：保证画面不变形，又能撑到屏幕边界
            $factor = [math]::Min($b.Width / [int]$wh0[0], $b.Height / [int]$wh0[1])
            if ($factor -lt 1) {
                Write-Host ('设备画面本来就已经比电脑能点对点显示的范围还大（算出 {0:N2} 倍），不做缩小，直接全屏投。' -f $factor) -ForegroundColor Yellow
                $factor = 1.0
            }
            $extra = @('--fullscreen')
        }

        # 倍数一律按出厂分辨率算，避免连着用两次变成叠乘
        $newSize = '{0}x{1}' -f [math]::Round([int]$wh0[0] * $factor), [math]::Round([int]$wh0[1] * $factor)
        $newDpi  = [math]::Round([int]$origDpi.Physical * $factor)
        Write-Host ("电脑屏幕 $pc，设备原本 {0} / 密度 {1}，按 {2:N2} 倍改成 {3} / 密度 {4}，关窗自动还原。" -f `
            $origSize.Current, $origDpi.Current, $factor, $newSize, $newDpi) -ForegroundColor Green
    }

    Save-State -dev $dev -origSize $origSize -origDpi $origDpi

    & $adb -s $dev shell wm size $newSize        | Out-Null
    & $adb -s $dev shell wm density $newDpi      | Out-Null
    Start-Sleep -Milliseconds 1500

    try {
        Start-Cast -params ($common + $extra)
    }
    finally {
        Restore-Wm -dev $dev -what 'size'    -orig $origSize
        Restore-Wm -dev $dev -what 'density' -orig $origDpi
        Remove-Item $stateFile -Force -ErrorAction SilentlyContinue
        Write-Host '已还原成设备原来的分辨率和显示大小。' -ForegroundColor DarkGray
    }
}

while ($true) {
    Write-Host ''
    Write-Host '===== 投屏 =====' -ForegroundColor Cyan
    Write-Host '  1   1:1 投屏（就是设备那块屏，窗口里按 Alt+F 切全屏）'
    Write-Host '  2   缩放投屏（让设备按更大像素画，电脑上看更清楚，费设备性能）'
    Write-Host '  3   虚拟屏幕（设备另开一块屏给电脑用）'
    Write-Host '  4   还原设备分辨率（上次异常退出、没还原时用）'
    Write-Host '  0   退出'
    Write-Host '提示：缩放投屏是给掌机用的；手机画面像素本来就已经超过电脑能显示的范围，放大只会更卡。' -ForegroundColor DarkGray
    $c = Read-Host '输入编号'

    if ($c -eq '4') {
        if (Test-Path $stateFile) {
            Check-Leftover
        }
        else {
            $dev = Get-Device
            if (-not $dev) { Write-Host '没找到已授权的设备。' -ForegroundColor Red; continue }
            Write-Host '没有未还原的记录。这一项会把设备的分辨率和显示大小直接恢复出厂，' -ForegroundColor Yellow
            Write-Host '如果你自己在设备上手动调过显示大小，也会被一起清掉。' -ForegroundColor Yellow
            $ans = Read-Host '确认恢复出厂吗？输 yes 执行，其他任意内容取消'
            if ($ans -eq 'yes') {
                & $adb -s $dev shell wm size reset    | Out-Null
                & $adb -s $dev shell wm density reset | Out-Null
                Write-Host '已恢复出厂分辨率和显示大小。' -ForegroundColor Green
            }
        }
        continue
    }

    if ($c -eq '1') {
        Start-Cast -params $common
    }
    elseif ($c -eq '2') {
        $dev = Get-Device
        if (-not $dev) { Write-Host '没找到已授权的设备。' -ForegroundColor Red; continue }

        Write-Host ''
        Write-Host '----- 缩放倍数 -----' -ForegroundColor Cyan
        Write-Host '提醒：先投屏、后开应用。投屏途中改分辨率会卡死当时已经开着的应用（掌机 RetroArch 实测）。' -ForegroundColor DarkGray
        Write-Host '提醒：只要高于设备原生分辨率，设备就得画更多像素，可能变卡；1 倍最省，2 倍最费。' -ForegroundColor DarkGray
        Write-Host '  1   1 倍（设备原始分辨率，等于不改）'
        Write-Host '  2   1.5 倍'
        Write-Host '  3   2 倍'
        Write-Host '  4   按设备显示比例最大化（点对点放大，自动全屏）'
        Write-Host '  5   按屏幕最大分辨率（设备本机屏幕显示比例会改变）'
        Write-Host '  0   返回'
        $sel = Read-Host '输入编号'

        if ($sel -eq '4') {
            Start-ScaledCast -dev $dev -factor 1 -AutoFit
        }
        elseif ($sel -eq '5') {
            Start-ScaledCast -dev $dev -factor 1 -Fill
        }
        else {
            $f = switch ($sel) {
                '1' { 1.0 }
                '2' { 1.5 }
                '3' { 2.0 }
                default { $null }
            }
            if ($f) { Start-ScaledCast -dev $dev -factor $f }
        }
    }
    elseif ($c -eq '3') {
        Write-Host ''
        Write-Host '----- 虚拟屏幕 -----' -ForegroundColor Cyan
        Write-Host '  1   开一个应用窗口（输应用名，几个字就够；可反复开，电脑上摆几个自己定）'
        Write-Host '  2   开一块空白虚拟屏'
        Write-Host "  3   独占全屏一块（$pc，切不到别的窗口）"
        Write-Host '  4   看当前开了几个投屏窗口'
        Write-Host '  5   关掉所有投屏窗口'
        Write-Host "  0   返回      窗口尺寸 $vdSize，改脚本顶部那一行"
        $v = Read-Host '输入编号'

        if ($v -eq '1') {
            $name = Read-Host '应用名（输几个字就行）'
            if ($name) { Start-AppWindow -name $name }
        }
        elseif ($v -eq '2') {
            Start-AppWindow -Blank
        }
        elseif ($v -eq '3') {
            Start-Cast -params (@("--new-display=$pc/$vdDpi", '--fullscreen') + $common)
        }
        elseif ($v -eq '4') {
            Show-Windows
        }
        elseif ($v -eq '5') {
            Stop-AllWindows
        }
    }
    elseif ($c -eq '0') {
        break
    }
    else {
        Write-Host '没这个编号' -ForegroundColor Yellow
    }
}
