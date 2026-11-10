param([string]$OutputDirectory = 'artifacts/V2.0.1')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $root
try {
    $source = Join-Path $root 'artifacts/app'
    $release = [IO.Path]::GetFullPath($OutputDirectory, $root)
    $app = Join-Path $release 'app'
    # 仅打包明确列出的正式产物，不包含测试DLL、设备日志、历史或调试符号。
    New-Item -ItemType Directory -Force -Path $app,(Join-Path $app 'docs') | Out-Null
    $binaries = @('SerialBroker.exe','SerialBroker.dll','SerialBroker.Core.dll','SerialBroker.Native.dll','ModernWpf.dll','ModernWpf.Controls.dll','SerialBroker.deps.json','SerialBroker.runtimeconfig.json','serialctl.exe')
    foreach ($name in $binaries) { Copy-Item -LiteralPath (Join-Path $source $name) -Destination $app }
    foreach ($folder in (Get-ChildItem -LiteralPath $source -Directory)) {
        $satellites = @(Get-ChildItem -LiteralPath $folder.FullName -File -Filter '*.resources.dll')
        if ($satellites.Count -gt 0) {
            $destination = Join-Path $app $folder.Name
            New-Item -ItemType Directory -Force -Path $destination | Out-Null
            foreach ($file in $satellites) { Copy-Item -LiteralPath $file.FullName -Destination $destination }
        }
    }
    foreach ($name in @('README.md','README.zh-CN.md','LICENSE','THIRD-PARTY-NOTICES.md')) { Copy-Item -LiteralPath $name -Destination $app }
    foreach ($name in @('V2-USER-GUIDE.md','V2-USER-GUIDE.html','PROTOCOL.md','CHANGELOG.md','VALIDATION.md','DESIGN.md','RELEASE-V2.0.1.md')) { Copy-Item -LiteralPath (Join-Path $root "docs/$name") -Destination (Join-Path $app 'docs') }
    $archive = Join-Path $release 'SerialBroker-V2.0.1-win-x64.zip'
    Compress-Archive -Path (Join-Path $app '*') -DestinationPath $archive -Force
    $hash = Get-FileHash -LiteralPath $archive -Algorithm SHA256
    [IO.File]::WriteAllText((Join-Path $release 'SHA256SUMS.txt'),($hash.Hash.ToLowerInvariant() + '  SerialBroker-V2.0.1-win-x64.zip' + [Environment]::NewLine))
    Copy-Item -LiteralPath (Join-Path $app 'docs') -Destination $source -Recurse -Force
    Get-Item -LiteralPath $archive | Select-Object FullName,Length
} finally { Pop-Location }
