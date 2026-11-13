[CmdletBinding()]
param([switch]$Run)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$src = Join-Path $root 'src'
$dist = Join-Path $root 'dist'
if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }

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

$cscArgs = @('/target:winexe', '/optimize+', '/nologo', "/out:$out",
    ('/resource:{0},ui.xaml' -f (Join-Path $src 'ui.xaml'))) +
    ($refs | ForEach-Object { "/r:$_" }) + $cs

& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "编译失败（exit=$LASTEXITCODE）" }

$len = (Get-Item $out).Length
Write-Host ("编译成功：{0}（{1:N0} 字节）" -f $out, $len)

if ($Run) { & $out }
