[CmdletBinding()]
param(
    [switch]$Run,
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$src = Join-Path $root 'src'
$dist = Join-Path $root 'dist'
if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }

# 没显式给版本时，用最近一次 git tag（v1.0.2 -> 1.0.2）
if (-not $PSBoundParameters.ContainsKey('Version')) {
    try {
        $detected = & git -C $root describe --tags --abbrev=0 2>$null
        if ($detected) { $Version = $detected.Trim().TrimStart('v') }
    } catch { }
}

if ($Version -notmatch '^\d+(\.\d+){1,3}$') {
    throw "版本号格式不对：$Version（示例：1.0.2）"
}

$fw = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path (Join-Path $fw 'csc.exe'))) { $fw = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319' }
$csc = Join-Path $fw 'csc.exe'
if (-not (Test-Path $csc)) { throw '找不到 csc.exe（需要 .NET Framework 4.x）' }

$refs = @(
    (Join-Path $fw 'WPF\PresentationFramework.dll'),
    (Join-Path $fw 'WPF\PresentationCore.dll'),
    (Join-Path $fw 'WPF\WindowsBase.dll'),
    (Join-Path $fw 'System.Xaml.dll'),
    (Join-Path $fw 'System.Web.Extensions.dll'),
    (Join-Path $fw 'System.Security.dll'),
    (Join-Path $fw 'System.dll'),
    (Join-Path $fw 'System.Core.dll'),
    (Join-Path $fw 'System.Windows.Forms.dll'),
    (Join-Path $fw 'System.Drawing.dll')
)
foreach ($r in $refs) { if (-not (Test-Path $r)) { throw "缺少引用：$r" } }

$cs = Get-ChildItem (Join-Path $src '*.cs') | ForEach-Object { $_.FullName }
$out = Join-Path $dist 'DingTalkClockIn.exe'

# csc 4.8 没有 /version 开关，用生成的 AssemblyInfo 注入版本号
$asmInfo = Join-Path $dist 'AssemblyInfo.g.cs'
$asmText = @"
using System.Reflection;
[assembly: AssemblyVersion("$Version")]
[assembly: AssemblyFileVersion("$Version")]
[assembly: AssemblyInformationalVersion("$Version")]
"@
[IO.File]::WriteAllText($asmInfo, $asmText, (New-Object Text.UTF8Encoding($true)))
$cs = @($cs) + $asmInfo

$cscArgs = @('/target:winexe', '/optimize+', '/nologo', "/out:$out",
    ('/resource:{0},ui.xaml' -f (Join-Path $src 'ui.xaml'))) +
    ($refs | ForEach-Object { "/r:$_" }) + $cs

& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "编译失败（exit=$LASTEXITCODE）" }

$len = (Get-Item $out).Length
Write-Host ("编译成功：{0}（{1:N0} 字节，版本 v{2}）" -f $out, $len, $Version)

if ($Run) { & $out }
