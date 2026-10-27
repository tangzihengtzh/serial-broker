param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $root
try {
    if (-not $SkipBuild) { & "$PSScriptRoot/build.ps1" }
    & cmake -S . -B build-test -G 'Visual Studio 17 2022' -A x64 -DSB_TEST_TRANSPORT=ON
    if ($LASTEXITCODE -ne 0) { throw 'Test native configuration failed' }
    & cmake --build build-test --config Release --parallel 2
    if ($LASTEXITCODE -ne 0) { throw 'Test native build failed' }
    & dotnet build tests/SerialBroker.Tests/SerialBroker.Tests.csproj -c Release --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Functional test build failed' }
    # 实际执行待交付的托管程序集；只有模拟功能测试的原生DLL由夹具替换。
    Copy-Item -LiteralPath artifacts/app/SerialBroker.dll,artifacts/app/SerialBroker.Core.dll -Destination tests/SerialBroker.Tests/bin/Release/net8.0-windows
    & tests/SerialBroker.Tests/bin/Release/net8.0-windows/SerialBroker.Tests.exe $root
    if ($LASTEXITCODE -ne 0) { throw 'Functional tests failed' }
    & tests/SerialBroker.Tests/bin/Release/net8.0-windows/SerialBroker.Tests.exe $root --production
    if ($LASTEXITCODE -ne 0) { throw 'Production boundary tests failed' }
} finally { Pop-Location }
