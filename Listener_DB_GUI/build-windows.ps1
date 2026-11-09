param([ValidateSet('Release','Debug')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'Listener_DB_GUI.csproj'
$output = Join-Path $PSScriptRoot 'output\windows'
& dotnet publish $project -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $output
if ($LASTEXITCODE -ne 0) { throw 'Dashboard publish failed. See the dotnet error above.' }
Write-Host "Ready: $output. Keep the wwwroot folder alongside Listener_DB_GUI.exe."
