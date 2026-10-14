<#
.SYNOPSIS  Installs the SERI Resort POS on this Windows PC (the app is self-contained: nothing else to install first).
.DESCRIPTION
  Run Install.cmd (double-click). It asks two short questions, copies the app, writes this PC's settings, and adds a
  "SERI Resort POS" shortcut to the desktop and Start menu. Running it again UPDATES the app and keeps this PC's settings
  and its registration. Registration itself is done on first start (a code from IT).
#>
param(
    [string]$InstallDir = 'C:\SERI-POS',
    [string]$OnlineUrl = 'https://api.007resorts.com',
    [string]$LocalUrl = '',
    [string]$TerminalName = '',
    [switch]$Unattended,
    [switch]$NoShortcuts,
    [switch]$NoElevate,
    [switch]$Reconfigure
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $here 'app'
if (-not (Test-Path (Join-Path $source 'R007.Pos.exe'))) { throw "The 'app' folder next to this script is missing or incomplete. Unzip the whole installer first." }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin -and -not $NoElevate) {
    $passThrough = @()
    foreach ($k in $PSBoundParameters.Keys) {
        $v = $PSBoundParameters[$k]
        if ($v -is [switch]) { if ($v.IsPresent) { $passThrough += "-$k" } } else { $passThrough += "-$k"; $passThrough += "`"$v`"" }
    }
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList (@('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"") + $passThrough)
    exit
}

function Ask($question, $default) {
    if ($Unattended) { return $default }
    $suffix = if ($default) { " [$default]" } else { ' [press Enter to skip]' }
    $answer = Read-Host "$question$suffix"
    if ([string]::IsNullOrWhiteSpace($answer)) { return $default }
    return $answer.Trim()
}

Write-Host ''
Write-Host 'SERI Resort POS - installer' -ForegroundColor Green
Write-Host '---------------------------'
$settingsPath = Join-Path $InstallDir 'appsettings.local.json'
$needSettings = $Reconfigure -or -not (Test-Path $settingsPath)
if ($needSettings) {
    $LocalUrl = Ask 'Address of the PROPERTY server inside the building (for example http://192.168.1.75)' $LocalUrl
    $OnlineUrl = Ask 'Address of the ONLINE server' $OnlineUrl
    $TerminalName = Ask 'A name for this till (for example Reception POS 2)' $TerminalName
}

Get-Process -Name 'R007.Pos' -ErrorAction SilentlyContinue | ForEach-Object { Write-Host 'Closing the running POS...'; $_.CloseMainWindow() | Out-Null; Start-Sleep -Seconds 2; if (-not $_.HasExited) { $_.Kill() } }

Write-Host "Copying the app to $InstallDir ..."
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
& robocopy $source $InstallDir /E /XF 'appsettings.local.json*' /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copying failed (robocopy exit code $LASTEXITCODE)." }
Get-ChildItem -Path $InstallDir -Recurse -File | Unblock-File -ErrorAction SilentlyContinue

if ($needSettings) {
    # Start on the property server when one was given, otherwise on the online server; the title-click dialog switches later.
    $startUrl = if ($LocalUrl) { $LocalUrl } else { $OnlineUrl }
    $pos = [ordered]@{ ApiBaseUrl = $startUrl }
    if ($OnlineUrl) { $pos.OnlineUrl = $OnlineUrl }
    if ($LocalUrl) { $pos.LocalUrl = $LocalUrl }
    if ($TerminalName) { $pos.DeviceRegistration = [ordered]@{ DeviceName = $TerminalName } }
    $json = (@{ Pos = $pos } | ConvertTo-Json -Depth 5)
    [IO.File]::WriteAllText($settingsPath, $json, (New-Object Text.UTF8Encoding $false))
    Write-Host "Saved this PC's settings: $settingsPath"
} else {
    Write-Host "Kept this PC's existing settings ($settingsPath)."
}

if (-not $NoShortcuts) {
    $shell = New-Object -ComObject WScript.Shell
    $targets = @((Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'SERI Resort POS.lnk'),
                 (Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'SERI Resort POS.lnk'))
    foreach ($t in $targets) {
        $lnk = $shell.CreateShortcut($t)
        $lnk.TargetPath = Join-Path $InstallDir 'R007.Pos.exe'
        $lnk.WorkingDirectory = $InstallDir
        $lnk.Description = 'SERI Resort POS'
        $lnk.Save()
    }
    Write-Host 'Added "SERI Resort POS" to the desktop and the Start menu.'
}

Write-Host ''
Write-Host 'Installed.' -ForegroundColor Green
Write-Host @'

Next:
  1. Open "SERI Resort POS".
  2. First start only: it asks for the server and a REGISTRATION CODE. Get a code from IT (admin: People > Devices >
     Register a device, with a Home facility chosen). Enter it with a name for this till, then sign in with your staff number and PIN.
  3. To switch this till between the property server and the online server: click the "SERI Resort" title at the top 7 times.
'@
if (-not $Unattended) { Read-Host 'Press Enter to close' | Out-Null }
