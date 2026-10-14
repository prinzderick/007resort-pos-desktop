<#  Makes SERI-POS-Installer.zip from a finished publish folder (the output of `dotnet publish ... -o <dir>`).
    Leaves out this machine's own settings (appsettings.local.json and its backups). Run it on a Windows PC.  #>
param(
    [Parameter(Mandatory)][string]$PublishDir,
    [string]$Out = (Join-Path $PSScriptRoot 'SERI-POS-Installer.zip')
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path (Join-Path $PublishDir 'R007.Pos.exe'))) { throw "R007.Pos.exe not found in $PublishDir" }
$stage = Join-Path $env:TEMP ('seripos-' + [guid]::NewGuid().ToString('N'))
$pkg = Join-Path $stage 'SERI-POS-Installer'
New-Item -ItemType Directory -Force -Path (Join-Path $pkg 'app') | Out-Null
& robocopy $PublishDir (Join-Path $pkg 'app') /E /XF 'appsettings.local.json*' /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
foreach ($f in 'Install-POS.ps1', 'Install.cmd', 'Uninstall.cmd', 'README.txt') { Copy-Item (Join-Path $PSScriptRoot $f) $pkg }
if (Test-Path $Out) { Remove-Item $Out -Force }
Compress-Archive -Path $pkg -DestinationPath $Out
Remove-Item $stage -Recurse -Force
'{0}  {1:N0} MB' -f $Out, ((Get-Item $Out).Length / 1MB)
