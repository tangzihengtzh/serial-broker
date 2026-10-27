param([switch]$Rebuild, [string]$BuildDirectory = 'build', [switch]$Package)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $root
try {
    New-Item -ItemType Directory -Force -Path artifacts/app | Out-Null
    & cmake -S . -B $BuildDirectory -G 'Visual Studio 17 2022' -A x64 -DSB_TEST_TRANSPORT=OFF
    if ($LASTEXITCODE -ne 0) { throw 'CMake configuration failed' }
    $buildArgs = @('--build',$BuildDirectory,'--config','Release','--parallel','2')
    if ($Rebuild) { $buildArgs += '--clean-first' }
    & cmake @buildArgs
    if ($LASTEXITCODE -ne 0) { throw 'Native build failed' }
    & dotnet publish ui/SerialBroker.Gui/SerialBroker.Gui.csproj -c Release --no-self-contained -o artifacts/app -p:DebugType=None -p:DebugSymbols=false --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'WPF publish failed' }
    Copy-Item -LiteralPath "$BuildDirectory/Release/SerialBroker.Native.dll","$BuildDirectory/Release/serialctl.exe" -Destination artifacts/app
    if ($Package) { & "$PSScriptRoot/package.ps1" }
} finally { Pop-Location }
