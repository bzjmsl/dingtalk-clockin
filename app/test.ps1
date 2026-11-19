[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$src = Join-Path $root 'src'
$tests = Join-Path $root 'tests'

$fw = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path (Join-Path $fw 'csc.exe'))) { $fw = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319' }
$csc = Join-Path $fw 'csc.exe'

$refs = @(
    (Join-Path $fw 'WPF\PresentationFramework.dll'),
    (Join-Path $fw 'WPF\PresentationCore.dll'),
    (Join-Path $fw 'WPF\WindowsBase.dll'),
    (Join-Path $fw 'System.Xaml.dll'),
    (Join-Path $fw 'System.Web.Extensions.dll'),
    (Join-Path $fw 'System.Security.dll'),
    (Join-Path $fw 'System.dll'),
    (Join-Path $fw 'System.Core.dll')
)

$out = Join-Path $tests 'test.exe'
$cscArgs = @('/target:exe', '/nologo', "/out:$out") +
    ($refs | ForEach-Object { "/r:$_" }) +
    @(
        (Join-Path $src 'Core.cs'),
        (Join-Path $src 'Adb.cs'),
        (Join-Path $src 'Services.cs'),
        (Join-Path $tests 'Test.cs')
    )

& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "test build failed (exit=$LASTEXITCODE)" }

& $out
exit $LASTEXITCODE
