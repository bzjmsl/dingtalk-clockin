#requires -version 5.1
<#
  注册 / 卸载 Windows 计划任务

  .\install-task.ps1                # 注册两个任务，干跑模式（不点打卡按钮）
  .\install-task.ps1 -Live          # 注册两个任务，真正打卡
  .\install-task.ps1 -Uninstall     # 删除任务
#>
[CmdletBinding()]
param(
    [switch]$Live,
    [ValidateSet('wake', 'full')]
    [string]$Mode = 'wake',
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$Script = Join-Path $Root 'dingtalk-clockin.ps1'
$TaskMorning = 'DingTalkClockIn-Morning'
$TaskEvening = 'DingTalkClockIn-Evening'

if ($Uninstall) {
    foreach ($t in @($TaskMorning, $TaskEvening)) {
        $existing = Get-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue
        if ($existing) {
            Unregister-ScheduledTask -TaskName $t -Confirm:$false
            Write-Host "已删除任务：$t"
        } else {
            Write-Host "任务不存在：$t"
        }
    }
    return
}

if (-not (Test-Path $Script)) { throw "找不到主脚本：$Script" }

$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 30) `
    -MultipleInstances IgnoreNew

$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited

$pairs = @(
    @{ Name = $TaskMorning; Time = '07:50'; Session = 'morning' },
    @{ Name = $TaskEvening; Time = '17:40'; Session = 'evening' }
)

foreach ($p in $pairs) {
    $trigger = New-ScheduledTaskTrigger -Weekly -DaysOfWeek Monday, Tuesday, Wednesday, Thursday, Friday -At $p.Time
    $act = New-ScheduledTaskAction -Execute 'powershell.exe' `
        -Argument ('-ExecutionPolicy Bypass -NoProfile -WindowStyle Hidden -File "{0}" -Session {1} -Mode {2}{3}' -f $Script, $p.Session, $Mode, $(if ($Live) { ' -Live' } else { '' })) `
        -WorkingDirectory $Root

    $existing = Get-ScheduledTask -TaskName $p.Name -ErrorAction SilentlyContinue
    if ($existing) { Unregister-ScheduledTask -TaskName $p.Name -Confirm:$false }

    Register-ScheduledTask -TaskName $p.Name -Action $act -Trigger $trigger -Settings $settings -Principal $principal `
        -Description '钉钉定时打卡（工作日，节假日自动跳过）' | Out-Null
    Write-Host ("已注册：{0} 每周一至周五 {1}（模式={2}，{3}）" -f $p.Name, $p.Time, $Mode, $(if ($Live) { '真打卡' } else { '干跑' }))
}

Write-Host ''
Write-Host '改时间：重新运行本脚本前先编辑 dingtalk-clockin.ps1 顶部的 Windows 配置，'
Write-Host '或直接改任务触发器。查看结果：logs\ 与 shots\ 目录。'
