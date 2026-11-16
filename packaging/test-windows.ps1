param([string]$Executable = (Join-Path $PSScriptRoot '..\dist\Kashishu.exe'))
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testRoot = Join-Path $workspace ('artifacts\windows-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $testRoot | Out-Null
$savedEnvironment = @{}
try {
    foreach ($name in @('XDG_CONFIG_HOME','XDG_STATE_HOME','XDG_CACHE_HOME')) {
        $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
        [Environment]::SetEnvironmentVariable($name, (Join-Path $testRoot $name))
    }
    foreach ($mode in @('--self-test','--ui-self-test')) {
        $label = $mode.TrimStart('-')
        $process = Start-Process -FilePath $Executable -ArgumentList $mode -WindowStyle Hidden `
            -RedirectStandardOutput (Join-Path $testRoot "$label.out.log") `
            -RedirectStandardError (Join-Path $testRoot "$label.err.log") -PassThru
        if (-not $process.WaitForExit(60000)) {
            $process.Kill()
            throw "$label timed out; logs: $testRoot"
        }
        $process.Refresh()
        Get-Content -LiteralPath (Join-Path $testRoot "$label.out.log")
        if ($process.ExitCode -ne 0) {
            Get-Content -LiteralPath (Join-Path $testRoot "$label.err.log")
            throw "$label failed (exit $($process.ExitCode)); logs: $testRoot"
        }
    }
    Write-Host "Windows verification passed. Logs: $testRoot"
}
finally {
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name])
    }
}
