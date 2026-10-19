param(
    [string]$Version = '0.0.0-dev',
    [switch]$Test
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc. PortJanitor needs the .NET Framework 4.8 that ships with Windows 10/11." }

$Version = $Version.TrimStart('v')
$numeric = ($Version -split '[-+]')[0]
if ($numeric -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3 or v1.2.3-beta (got '$Version')." }

$obj = Join-Path $root 'obj'
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $obj, $dist | Out-Null

$icon = Join-Path $root 'assets\PortJanitor.ico'
if (-not (Test-Path $icon)) { & (Join-Path $root 'tools\make-icon.ps1') -Path $icon }

$versionFile = Join-Path $obj 'Version.g.cs'
@"
[assembly: System.Reflection.AssemblyVersion("$numeric.0")]
[assembly: System.Reflection.AssemblyFileVersion("$numeric.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("$Version")]
"@ | Set-Content -Encoding UTF8 $versionFile

$refs = 'System.dll', 'System.Core.dll', 'System.Data.dll', 'System.Drawing.dll', 'System.Management.dll', 'System.Windows.Forms.dll', 'System.Xml.dll' | ForEach-Object { "/r:$_" }
$source = Join-Path $root 'src\PortJanitor.cs'
$exe = Join-Path $dist 'PortJanitor.exe'

& $csc /nologo /target:winexe /optimize+ "/out:$exe" "/win32icon:$icon" "/win32manifest:$root\src\app.manifest" $refs $source $versionFile
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Host "Built $exe ($Version)"

$setup = Join-Path $dist 'PortJanitor-Setup.exe'
& $csc /nologo /target:winexe /optimize+ "/out:$setup" "/win32icon:$icon" "/win32manifest:$root\src\app.manifest" "/resource:$exe,PortJanitor.exe" $refs (Join-Path $root 'src\Setup.cs') $versionFile
if ($LASTEXITCODE -ne 0) { throw 'Setup build failed.' }
Write-Host "Built $setup ($Version)"

if ($Test) {
    $testExe = Join-Path $obj 'SmokeTest.exe'
    & $csc /nologo /target:exe /optimize+ /main:PortJanitor.Tests.SmokeTest "/out:$testExe" $refs $source (Join-Path $root 'tests\SmokeTest.cs') $versionFile
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    & $testExe
    if ($LASTEXITCODE -ne 0) { throw "Smoke tests failed (exit code $LASTEXITCODE)." }
}
