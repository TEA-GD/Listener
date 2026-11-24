$ErrorActionPreference='Stop'
dotnet publish (Join-Path $PSScriptRoot 'Listener_ConnectionAnalyzer.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o (Join-Path $PSScriptRoot 'output\windows')
if($LASTEXITCODE -ne 0){throw 'Connection analyzer publish failed'}
