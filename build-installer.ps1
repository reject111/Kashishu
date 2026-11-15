# Publishes the app and packages dist\Kashishu-Setup-<version>.exe with Inno Setup 6.
param(
    [string]$Version,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $Version) {
    [xml]$project = Get-Content "$PSScriptRoot\Kashishu.csproj"
    $Version = ($project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
}
if (-not $Version) { throw 'Version is missing from Kashishu.csproj' }

if (-not $SkipBuild) {
    & "$PSScriptRoot\build-windows.ps1"
    if ($LASTEXITCODE -ne 0) { throw 'build-windows.ps1 failed' }
}

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw 'Inno Setup 6 is required: winget install JRSoftware.InnoSetup'
}

& $iscc "/DAppVersion=$Version" "$PSScriptRoot\installer\Kashishu.iss"
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed' }
Write-Host "Built $PSScriptRoot\dist\Kashishu-Setup-$Version.exe"
