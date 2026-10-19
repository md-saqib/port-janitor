param([string]$Dist = (Join-Path $PSScriptRoot '..\dist'))

$ErrorActionPreference = 'Stop'
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\PortJanitor'
if (Test-Path $key) { throw 'PortJanitor is already installed for this user. Uninstall it before running this test.' }

$dir = Join-Path $env:LOCALAPPDATA 'Programs\PortJanitor'
$app = Join-Path $dir 'PortJanitor.exe'
$links = (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'PortJanitor.lnk'), (Join-Path ([Environment]::GetFolderPath('Programs')) 'PortJanitor.lnk')
$script:failures = 0
function Check([bool]$ok, [string]$name) {
    Write-Host ($(if ($ok) { 'PASS  ' } else { 'FAIL  ' }) + $name)
    if (-not $ok) { $script:failures++ }
}

$p = Start-Process (Join-Path $Dist 'PortJanitor-Setup.exe') '/S' -Wait -PassThru
Check ($p.ExitCode -eq 0) 'Silent install exits with 0'
Check ((Test-Path $app) -and (Get-FileHash $app).Hash -eq (Get-FileHash (Join-Path $Dist 'PortJanitor.exe')).Hash) 'Installed app matches the build'
Check (Test-Path (Join-Path $dir 'Uninstall.exe')) 'Uninstaller is installed'
$shell = New-Object -ComObject WScript.Shell
foreach ($link in $links) { Check ((Test-Path $link) -and $shell.CreateShortcut($link).TargetPath -eq $app) "Shortcut points at the app: $link" }
$entry = Get-ItemProperty $key -ErrorAction SilentlyContinue
Check ($entry -and $entry.DisplayName -eq 'PortJanitor' -and $entry.QuietUninstallString -like '*Uninstall.exe" /uninstall /S') 'Uninstall entry is registered'

$p = Start-Process (Join-Path $dir 'Uninstall.exe') '/uninstall /S' -Wait -PassThru
Check ($p.ExitCode -eq 0) 'Silent uninstall exits with 0'
for ($i = 0; $i -lt 30 -and (Test-Path $dir); $i++) { Start-Sleep -Milliseconds 500 }
Check (-not (Test-Path $dir)) 'Install folder is removed'
foreach ($link in $links) { Check (-not (Test-Path $link)) "Shortcut is removed: $link" }
Check (-not (Test-Path $key)) 'Uninstall entry is removed'

if ($script:failures) { throw "$($script:failures) install check(s) failed." }
Write-Host 'All install checks passed.'
