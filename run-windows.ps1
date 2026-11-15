# Builds on first use, then launches the Windows executable with the given arguments.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not (Test-Path "$PSScriptRoot\dist\Kashishu.exe")) {
    & "$PSScriptRoot\build-windows.ps1"
}
& "$PSScriptRoot\dist\Kashishu.exe" @args
