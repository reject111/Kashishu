# Builds the self-contained Windows executable into dist\LyricifyIsland.exe.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

git -C $PSScriptRoot submodule update --init --recursive
if ($LASTEXITCODE -ne 0) { throw 'git submodule update failed' }
$dotnetPath = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnetPath -or -not (& $dotnetPath --list-sdks)) {
    $userDotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    if ((Test-Path -LiteralPath $userDotnet) -and (& $userDotnet --list-sdks)) {
        $dotnetPath = $userDotnet
    }
    else {
        throw '.NET 10 SDK is required (https://dotnet.microsoft.com/download)'
    }
}

$publishRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$publish = [IO.Path]::GetFullPath((Join-Path $publishRoot ("lyricify-island-publish-" + [guid]::NewGuid().ToString('N'))))
try {
    & $dotnetPath publish "$PSScriptRoot\LyricifyIsland.csproj" `
        --configuration Release `
        --runtime win-x64 `
        --framework net10.0-windows10.0.19041.0 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None `
        --output $publish
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

    New-Item -ItemType Directory -Force "$PSScriptRoot\dist" | Out-Null
    Copy-Item "$publish\LyricifyIsland.exe" "$PSScriptRoot\dist\LyricifyIsland.exe" -Force
    Copy-Item "$PSScriptRoot\THIRD_PARTY_NOTICES.md" "$PSScriptRoot\dist\THIRD_PARTY_NOTICES.md" -Force
    New-Item -ItemType Directory -Force "$PSScriptRoot\dist\licenses" | Out-Null
    Copy-Item "$PSScriptRoot\LICENSE" "$PSScriptRoot\dist\licenses\LyricifyIsland-Apache-2.0.txt" -Force
    Copy-Item "$PSScriptRoot\licenses\OppoPodsManager-GPL-3.0.txt" "$PSScriptRoot\dist\licenses\" -Force
    Copy-Item "$PSScriptRoot\licenses\NWayland-MIT.txt" "$PSScriptRoot\dist\licenses\" -Force
    Copy-Item "$PSScriptRoot\vendor\Lyricify-Lyrics-Helper\LICENSE" `
        "$PSScriptRoot\dist\licenses\Lyricify-Lyrics-Helper-Apache-2.0.txt" -Force

    Write-Host "Built $PSScriptRoot\dist\LyricifyIsland.exe"
}
finally {
    if ([IO.Path]::GetDirectoryName($publish) -ne $publishRoot -or
        [IO.Path]::GetFileName($publish) -notmatch '^lyricify-island-publish-[0-9a-f]{32}$') {
        throw 'Refusing to clean an unexpected publish directory'
    }
    Remove-Item -LiteralPath $publish -Recurse -Force -ErrorAction SilentlyContinue
}
