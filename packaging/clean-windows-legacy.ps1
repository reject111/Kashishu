# Remove only obsolete Windows outputs. macOS app bundles and the lyrics helper keep their names.
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$dist = Join-Path $workspace 'dist'
if (Test-Path -LiteralPath $dist) {
    $obsolete = @(Get-ChildItem -LiteralPath $dist -File | Where-Object {
        $_.Name -match '^LyricifyIsland(?:-Setup-[\w.-]+)?\.(?:exe|pdb|dll|deps\.json|runtimeconfig\.json)$'
    })
    $oldLicense = Join-Path $dist 'licenses\LyricifyIsland-Apache-2.0.txt'
    if (Test-Path -LiteralPath $oldLicense) { $obsolete += Get-Item -LiteralPath $oldLicense }
    foreach ($file in $obsolete) {
        if (-not $file.FullName.StartsWith($dist + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing to clean outside dist'
        }
        Remove-Item -LiteralPath $file.FullName -Force
    }
}
foreach ($outputName in @('bin','obj')) {
    $output = Join-Path $workspace $outputName
    if (-not (Test-Path -LiteralPath $output)) { continue }
    foreach ($file in Get-ChildItem -LiteralPath $output -Recurse -File | Where-Object {
        $_.Name -like 'LyricifyIsland.*' -and $_.FullName -notmatch '[\\/]osx-[^\\/]*[\\/]'
    }) {
        if (-not $file.FullName.StartsWith($output + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing to clean outside build output'
        }
        Remove-Item -LiteralPath $file.FullName -Force
    }
}
