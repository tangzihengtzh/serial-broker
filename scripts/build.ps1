param([switch]$Rebuild, [string]$BuildDirectory = 'build')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    $record = Join-Path $projectRoot 'docs\compilation-output.txt'
    "Build recorded: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')" | Set-Content -LiteralPath $record -Encoding UTF8
    "Configure: cmake --preset windows-x64 -B $BuildDirectory" | Add-Content -LiteralPath $record -Encoding UTF8
    & cmake --preset windows-x64 -B $BuildDirectory 2>&1 | Tee-Object -FilePath $record -Append
    if ($LASTEXITCODE -ne 0) { throw "CMake configure failed: $LASTEXITCODE" }
    $buildArgs = @('--build',$BuildDirectory,'--config','Release','--parallel','2')
    if ($Rebuild) { $buildArgs += '--clean-first' }
    "Build: cmake $($buildArgs -join ' ')" | Add-Content -LiteralPath $record -Encoding UTF8
    & cmake @buildArgs 2>&1 | Tee-Object -FilePath $record -Append
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $LASTEXITCODE" }
    'BUILD_EXIT_CODE=0. No application or test executable was launched.' | Tee-Object -FilePath $record -Append
    $guiPath = Join-Path $BuildDirectory 'Release\SerialBroker.exe'
    $cliPath = Join-Path $BuildDirectory 'Release\serialctl.exe'
    Get-Item -LiteralPath $guiPath,$cliPath |
        Select-Object Name,Length,LastWriteTime | Format-Table | Out-String | Add-Content -LiteralPath $record -Encoding UTF8
} finally { Pop-Location }
