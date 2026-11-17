param([ValidateSet('Release','Debug')][string]$Configuration='Release')
$ErrorActionPreference='Stop'
& dotnet publish (Join-Path $PSScriptRoot 'Listener_MailReceiver.csproj') -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o (Join-Path $PSScriptRoot 'output\windows')
if ($LASTEXITCODE -ne 0) { throw 'Mail receiver publish failed. See dotnet output above.' }
