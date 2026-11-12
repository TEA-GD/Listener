param([ValidateSet('Release','Debug')][string]$Configuration='Release')
$ErrorActionPreference='Stop'
& dotnet publish (Join-Path $PSScriptRoot 'Listener_Analyzer.csproj') -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o (Join-Path $PSScriptRoot 'output\windows')
if ($LASTEXITCODE -ne 0) { throw 'Analyzer publish failed. See the dotnet error above.' }
Write-Host 'Ready: output\windows\Listener_Analyzer.exe. Configure a local state directory and alert output before running.'
