#requires -version 5.1
<#
  钉钉定时打卡（Windows + ADB 驱动）

  规则：
    - 只在工作日执行：周一至周五，自动排除周末与法定节假日，支持调休补班
    - 时间不固定：在每个时间窗口内随机取一个时刻，而不是每天同一秒
    - 默认干跑：只走到打卡页并截图，不点打卡按钮；加 -Live 才真正点击

  示例：
    .\dingtalk-clockin.ps1 -Session morning -CheckOnly    # 只打印今天要不要打、几点打
    .\dingtalk-clockin.ps1 -Session morning              # 干跑：走到打卡页，不点
    .\dingtalk-clockin.ps1 -Session morning -Live        # 真打
#>
[CmdletBinding()]
param(
    [ValidateSet('auto', 'morning', 'evening')]
    [string]$Session = 'auto',
    [ValidateSet('wake', 'full')]
    [string]$Mode = 'wake',
    [switch]$Live,
    [switch]$Force,
    [switch]$CheckOnly,
    [string]$Adb
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$LogDir = Join-Path $Root 'logs'
$ShotDir = Join-Path $Root 'shots'
$CacheDir = Join-Path $Root 'cache'
$TmpDir = Join-Path $Root 'tmp'
foreach ($d in @($LogDir, $ShotDir, $CacheDir, $TmpDir)) {
    if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
}

# ------------------------------------------------------------------ 配置区
$Config = @{
    # 锁屏密码：留空表示手机无密码；填写后以明文保存在本文件里，请勿提交到远程仓库
    Pin             = ''

    # 时间窗口：脚本会在窗口内随机挑一个时刻执行
    Windows         = @{
        morning = @{ Start = '07:50'; End = '08:00' }
        evening = @{ Start = '17:40'; End = '17:50' }
    }

    # 打卡页绿色按钮中心（1080x1920）
    PunchButton     = @{ X = 540; Y = 1168 }

    # 钉钉首页顶部「打卡」入口（uiautomator 找不到文字时的兜底坐标）
    DingTalkEntry   = @{ X = 701; Y = 290 }

    HomeActivity    = 'com.alibaba.android.rimet/com.alibaba.android.rimet.biz.LaunchHomeActivity'
    PunchActivity   = 'com.alibaba.lightapp.runtime.ariver.TheOneActivityMainTask'

    # 锁屏数字键盘坐标（1080x1920）
    PinPad          = @{
        '0' = @(540, 1600); '1' = @(260, 1013); '2' = @(540, 1013); '3' = @(820, 1013)
        '4' = @(260, 1210); '5' = @(540, 1210); '6' = @(820, 1210)
        '7' = @(260, 1405); '8' = @(540, 1405); '9' = @(820, 1405)
    }
    UnlockSwipe     = @(540, 1500, 540, 400)
    MaxPageWaitSec  = 40
    PageSettleSec   = 10
    UseHolidayApi   = $true
    # 识别到外勤打卡（绿色按钮）且真要打卡时是否中止，避免记录异常定位
    AbortOnFieldPunch = $true
    # wake 模式：唤醒解锁后保持亮屏多久，给极速打卡留出识别时间
    WakeHoldSec     = 60
    # wake 模式要把钉钉切到前台，极速打卡只有在钉钉界面处于前台时才会触发
    WakeModeOpenDingTalk = $true
}
# ------------------------------------------------------------------------

if (-not $Adb) {
    $candidates = @(
        (Join-Path $Root 'platform-tools\adb.exe'),
        (Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe'),
        'C:\platform-tools\adb.exe'
    )
    $Adb = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $Adb) { $Adb = 'adb.exe' }
}

function Log {
    param([string]$Message, [ValidateSet('INFO', 'WARN', 'ERROR')][string]$Level = 'INFO')
    $line = '{0} [{1}] {2}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'), $Level, $Message
    Write-Host $line
    Add-Content -Path (Join-Path $LogDir ((Get-Date).ToString('yyyy-MM-dd') + '.log')) -Value $line -Encoding UTF8
}

function Invoke-Adb {
    param([string[]]$Arguments, [int]$TimeOutSec = 30)
    if (-not (Test-Path $Adb)) { throw "找不到 adb：$Adb" }
    $outFile = Join-Path $TmpDir 'adb-out.txt'
    $errFile = Join-Path $TmpDir 'adb-err.txt'
    $proc = Start-Process -FilePath $Adb -ArgumentList $Arguments -NoNewWindow -PassThru `
        -RedirectStandardOutput $outFile -RedirectStandardError $errFile
    if (-not $proc.WaitForExit($TimeOutSec * 1000)) {
        try { $proc.Kill() } catch { }
        Log ("adb 超时：{0}" -f ($Arguments -join ' ')) 'WARN'
        return $null
    }
    $stdout = if (Test-Path $outFile) { (Get-Content $outFile -Raw -ErrorAction SilentlyContinue) } else { '' }
    if ($null -eq $stdout) { $stdout = '' }
    return $stdout.Trim()
}

function Invoke-Shell {
    param([string]$Command, [int]$TimeOutSec = 30)
    return Invoke-Adb -Arguments @('shell', $Command) -TimeOutSec $TimeOutSec
}

function Get-Tap {
    param([int]$X, [int]$Y)
    Invoke-Shell "input tap $X $Y" 15 | Out-Null
}

function Save-Screen {
    param([string]$Tag)
    $remote = '/sdcard/_dt_' + $Tag + '.png'
    $local = Join-Path $ShotDir ('{0}-{1}.png' -f (Get-Date).ToString('yyyyMMdd-HHmmss'), $Tag)
    Invoke-Shell "screencap -p $remote" 30 | Out-Null
    Invoke-Adb -Arguments @('pull', $remote, $local) -TimeOutSec 30 | Out-Null
    Invoke-Shell "rm -f $remote" 10 | Out-Null
    if (Test-Path $local) { return $local }
    return $null
}

function Test-Locked {
    $policy = Invoke-Shell 'dumpsys window policy' 20
    if (-not $policy) { return $true }
    return ($policy -match 'mShowingLockscreen=true')
}

function Test-Awake {
    $policy = Invoke-Shell 'dumpsys window policy' 20
    if (-not $policy) { return $false }
    return ($policy -match 'mAwake=true')
}

function Invoke-Wake {
    # vivo 这版系统不响应 KEYCODE_WAKEUP(224)，只能用 POWER(26) 唤醒。
    # 必须先确认是熄屏状态，否则会把亮着的屏关掉。
    if (Test-Awake) { return $true }
    for ($i = 1; $i -le 2; $i++) {
        Invoke-Shell 'input keyevent 26' 15 | Out-Null
        Start-Sleep -Milliseconds 1500
        if (Test-Awake) { return $true }
    }
    return $false
}

function Get-PunchButtonColor {
    param([string]$PngPath)
    if (-not $PngPath -or -not (Test-Path $PngPath)) { return '' }
    Add-Type -AssemblyName System.Drawing -ErrorAction SilentlyContinue
    $bmp = [System.Drawing.Bitmap]::FromFile($PngPath)
    try {
        $points = @(
            @($Config.PunchButton.X, $Config.PunchButton.Y),
            @(($Config.PunchButton.X - 80), $Config.PunchButton.Y),
            @(($Config.PunchButton.X + 80), $Config.PunchButton.Y)
        )
        $blue = 0
        $green = 0
        foreach ($p in $points) {
            $c = $bmp.GetPixel($p[0], $p[1])
            # 内勤打卡是蓝色按钮，外勤打卡是绿色按钮，两种都要认
            if ($c.R -lt 80 -and $c.B -gt 230 -and $c.G -gt 90 -and $c.G -lt 190) { $blue++ }
            if ($c.R -lt 80 -and $c.G -gt 170 -and $c.B -gt 100 -and $c.B -lt 190) { $green++ }
        }
        if ($blue -ge 2) { return 'blue' }
        if ($green -ge 2) { return 'green' }
        return ''
    } finally {
        $bmp.Dispose()
    }
}

function Test-PunchButton {
    param([string]$PngPath)
    return ((Get-PunchButtonColor -PngPath $PngPath) -ne '')
}

function Unlock-Device {
    Invoke-Shell 'svc power stayon true' 15 | Out-Null
    for ($i = 1; $i -le 3; $i++) {
        if (-not (Test-Locked)) { return $true }
        Log "解锁尝试 $i"
        if (-not (Invoke-Wake)) {
            Log '唤醒失败' 'WARN'
            continue
        }
        if (-not (Test-Locked)) { return $true }
        $s = $Config.UnlockSwipe
        Invoke-Shell ("input swipe {0} {1} {2} {3} 250" -f $s[0], $s[1], $s[2], $s[3]) 15 | Out-Null
        Start-Sleep -Milliseconds 1200
        foreach ($ch in $Config.Pin.ToCharArray()) {
            $key = [string]$ch
            if (-not $Config.PinPad.ContainsKey($key)) { continue }
            $p = $Config.PinPad[$key]
            Invoke-Shell ("input tap {0} {1}" -f $p[0], $p[1]) 15 | Out-Null
            Start-Sleep -Milliseconds 1100
        }
        Start-Sleep -Seconds 2
        if (-not (Test-Locked)) { return $true }
    }
    Save-Screen -Tag 'unlock-failed' | Out-Null
    return $false
}

function Get-Focus {
    $w = Invoke-Shell 'dumpsys window' 25
    if (-not $w) { return '' }
    $m = [regex]::Match($w, 'mCurrentFocus=Window\{[^}]+\s+u0\s+([^\}\s]+)')
    if ($m.Success) { return $m.Groups[1].Value }
    return ''
}

function Wait-Focus {
    param([string]$Pattern, [int]$TimeOutSec = 30)
    $deadline = (Get-Date).AddSeconds($TimeOutSec)
    while ((Get-Date) -lt $deadline) {
        $focus = Get-Focus
        if ($focus -like "*$Pattern*") { return $true }
        Start-Sleep -Milliseconds 1500
    }
    return $false
}

function Get-UiText-Bounds {
    param([string]$Text, [int]$MaxTop = 400)
    $remote = '/sdcard/_dt_ui.xml'
    Invoke-Shell "uiautomator dump $remote" 30 | Out-Null
    $local = Join-Path $TmpDir 'ui.xml'
    if (Test-Path $local) { Remove-Item $local -Force }
    Invoke-Adb -Arguments @('pull', $remote, $local) -TimeOutSec 20 | Out-Null
    if (-not (Test-Path $local)) { return $null }
    try {
        [xml]$xml = Get-Content $local -Encoding UTF8 -Raw
    } catch {
        return $null
    }
    $nodes = $xml.SelectNodes("//node[@text='$Text']")
    foreach ($n in $nodes) {
        $b = [regex]::Match($n.bounds, '\[(\d+),(\d+)\]\[(\d+),(\d+)\]')
        if (-not $b.Success) { continue }
        $top = [int]$b.Groups[2].Value
        if ($top -gt $MaxTop) { continue }
        return @{
            X = [int](([int]$b.Groups[1].Value + [int]$b.Groups[3].Value) / 2)
            Y = [int](([int]$b.Groups[2].Value + [int]$b.Groups[4].Value) / 2)
        }
    }
    return $null
}

function Get-WorkdayState {
    param([datetime]$Date)
    $key = $Date.ToString('yyyy-MM-dd')

    foreach ($f in @('workdays-extra.txt', 'holidays-extra.txt')) {
        $path = Join-Path $Root $f
        if (Test-Path $path) {
            $hit = Get-Content $path | Where-Object { $_.Trim() -eq $key }
            if ($hit) {
                if ($f -eq 'workdays-extra.txt') { return @{ IsWorkday = $true; Reason = '手工指定上班日' } }
                return @{ IsWorkday = $false; Reason = '手工指定休息日' }
            }
        }
    }

    $cache = Join-Path $CacheDir "$key.json"
    if (Test-Path $cache) {
        try {
            $c = Get-Content $cache -Raw | ConvertFrom-Json
            return @{ IsWorkday = [bool]$c.IsWorkday; Reason = [string]$c.Reason }
        } catch { }
    }

    if ($Config.UseHolidayApi) {
        try {
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            $r = Invoke-RestMethod -Uri "https://timor.tech/api/holiday/info/$key" -TimeoutSec 8
            if ($r -and $r.code -eq 0 -and $r.type) {
                $t = [int]$r.type.type   # 0 工作日 1 周末 2 节日 3 调休(补班)
                $isWork = ($t -eq 0 -or $t -eq 3)
                $reason = if ($t -eq 0) { '工作日' } elseif ($t -eq 3) { '调休补班' } else { '休息日：' + $r.type.name }
                $obj = @{ IsWorkday = $isWork; Reason = $reason }
                $obj | ConvertTo-Json | Set-Content -Path $cache -Encoding UTF8
                return $obj
            }
        } catch {
            Log "节假日接口不可用，退回按星期判断：$($_.Exception.Message)" 'WARN'
        }
    }

    $dow = $Date.DayOfWeek
    $isWork = ($dow -ne 'Saturday' -and $dow -ne 'Sunday')
    return @{ IsWorkday = $isWork; Reason = '按星期判断（未排除节假日）' }
}

function Get-TargetTime {
    param([hashtable]$Window)
    $start = [datetime]::ParseExact($Window.Start, 'HH:mm', $null)
    $end = [datetime]::ParseExact($Window.End, 'HH:mm', $null)
    $span = [int]($end - $start).TotalSeconds
    if ($span -lt 1) { $span = 1 }
    $offset = Get-Random -Minimum 0 -Maximum $span
    return (Get-Date).Date.AddHours($start.Hour).AddMinutes($start.Minute).AddSeconds($offset)
}

# ------------------------------------------------------------------ 主流程
try {
    if ($Session -eq 'auto') {
        $Session = if ((Get-Date).Hour -lt 12) { 'morning' } else { 'evening' }
    }
    Log "===== 开始 $Session（dry-run=$(-not $Live)）====="

    $today = (Get-Date).Date
    $work = Get-WorkdayState -Date $today
    Log ("今天是 {0}，{1}，{2}" -f $today.ToString('yyyy-MM-dd dddd'), $(if ($work.IsWorkday) { '工作日' } else { '休息日' }), $work.Reason)

    if (-not $work.IsWorkday -and -not $Force) {
        Log '今天不用打卡，退出'
        exit 0
    }

    $window = $Config.Windows[$Session]
    $winStart = $today.AddHours([int]$window.Start.Split(':')[0]).AddMinutes([int]$window.Start.Split(':')[1])
    $winEnd = $today.AddHours([int]$window.End.Split(':')[0]).AddMinutes([int]$window.End.Split(':')[1])
    $target = Get-TargetTime -Window $window
    Log ("时间窗口 {0}-{1}，本次目标时刻 {2}" -f $window.Start, $window.End, $target.ToString('HH:mm:ss'))

    if ($CheckOnly) {
        Log '仅检查模式：不做任何操作'
        exit 0
    }

    $now = Get-Date
    if (-not $Force) {
        if ($now -lt $winStart) {
            $wait = [int]($target - $now).TotalSeconds
            Log "距离目标还有 $wait 秒，等待中…"
            while ((Get-Date) -lt $target) { Start-Sleep -Seconds 1 }
        } elseif ($now -gt $winEnd) {
            Log '已过时间窗口，退出（需要强制执行请加 -Force）'
            exit 0
        }
    }

    $devices = Invoke-Adb @('devices') 25
    if (-not $devices -or $devices -notmatch 'device\s*$' -and $devices -notmatch "\r?\n\S+\s+device") {
        Log '手机未连接或未授权，退出' 'ERROR'
        exit 1
    }
    if ($devices -match 'unauthorized') {
        Log '手机显示 unauthorized，请在手机上点允许 USB 调试' 'ERROR'
        exit 1
    }

    if (-not (Unlock-Device)) {
        Log '解锁失败，退出' 'ERROR'
        exit 1
    }
    Log '已解锁'

    if ($Mode -eq 'wake') {
        if ($Config.WakeModeOpenDingTalk) {
            Invoke-Shell "am start -n $($Config.HomeActivity)" 25 | Out-Null
            Start-Sleep -Seconds 5
        }
        $shot = Save-Screen -Tag 'woke'
        Log ("保持亮屏 {0} 秒，等待极速打卡生效（截图：{1}）" -f $Config.WakeHoldSec, $shot)
        Start-Sleep -Seconds $Config.WakeHoldSec
        Log 'wake 模式完成，退出'
        exit 0
    }

    Invoke-Shell "am start -n $($Config.HomeActivity)" 25 | Out-Null
    if (-not (Wait-Focus -Pattern 'rimet' -TimeOutSec 30)) {
        Invoke-Shell 'monkey -p com.alibaba.android.rimet -c android.intent.category.LAUNCHER 1' 30 | Out-Null
        if (-not (Wait-Focus -Pattern 'LaunchHomeActivity' -TimeOutSec 30)) {
            Log '钉钉没有打开，退出' 'ERROR'
            exit 2
        }
    }
    if ((Get-Focus) -like '*TheOneActivityMainTask*') {
        Log '当前停在打卡页，先退回钉钉首页'
        Invoke-Shell 'input keyevent 4' 15 | Out-Null
        Start-Sleep -Seconds 2
    }
    Start-Sleep -Seconds 2
    Log '钉钉首页已就绪'

    $entry = Get-UiText-Bounds -Text '打卡' -MaxTop 400
    if ($entry) {
        Log ("定位到「打卡」入口：{0},{1}" -f $entry.X, $entry.Y)
        Get-Tap -X $entry.X -Y $entry.Y
    } else {
        Log '未读到入口文字，使用兜底坐标'
        Get-Tap -X $Config.DingTalkEntry.X -Y $Config.DingTalkEntry.Y
    }

    if (-not (Wait-Focus -Pattern 'TheOneActivityMainTask' -TimeOutSec 30)) {
        Log '没有进入打卡页，退出' 'ERROR'
        exit 2
    }
    Log '打卡页已打开，等待小程序渲染'
    Start-Sleep -Seconds $Config.PageSettleSec

    $shot = $null
    $btnColor = ''
    $deadline = (Get-Date).AddSeconds($Config.MaxPageWaitSec)
    while ((Get-Date) -lt $deadline) {
        $shot = Save-Screen -Tag 'punchpage'
        $btnColor = Get-PunchButtonColor -PngPath $shot
        if ($btnColor) { break }
        Start-Sleep -Seconds 2
    }

    if (-not $btnColor) {
        Log "没能识别到打卡按钮，截图：$shot" 'ERROR'
        exit 2
    }
    $btnLabel = if ($btnColor -eq 'blue') { '内勤（蓝色按钮）' } else { '外勤（绿色按钮，会记录定位）' }
    Log "已确认打卡按钮可见：$btnLabel"

    if ($btnColor -eq 'green' -and $Config.AbortOnFieldPunch -and -not $Force) {
        Log '当前不在考勤范围，会打成外勤，已中止（确需外勤请加 -Force）' 'ERROR'
        exit 4
    }

    if (-not $Live) {
        Log '干跑模式：不点击打卡按钮，结束'
        exit 0
    }

    Get-Tap -X $Config.PunchButton.X -Y $Config.PunchButton.Y
    Log '已点击打卡按钮，等待结果'

    $ok = $false
    $deadline = (Get-Date).AddSeconds(25)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        $after = Save-Screen -Tag 'result'
        if ($after -and -not (Test-PunchButton -PngPath $after)) { $ok = $true; break }
    }

    if ($ok) {
        Log "打卡动作已完成，结果截图：$after"
        exit 0
    }

    Log "打卡后界面未确认变化，请人工核对截图：$after" 'WARN'
    exit 3
} catch {
    Log "异常：$($_.Exception.Message)" 'ERROR'
    exit 1
}
