param([string]$ConfigPath=(Join-Path $PSScriptRoot 'config.json'))
$ErrorActionPreference='Stop'
$exe=Join-Path $PSScriptRoot 'Listener_ConnectionAnalyzer.exe'
if(-not(Test-Path -LiteralPath $exe)){$exe=Join-Path $PSScriptRoot 'output\windows\Listener_ConnectionAnalyzer.exe'}
if(Test-Path -LiteralPath $exe){& $exe --config $ConfigPath}
else{dotnet run --project (Join-Path $PSScriptRoot 'Listener_ConnectionAnalyzer.csproj') -c Release -- --config $ConfigPath}
if($LASTEXITCODE -ne 0){throw 'Connection analyzer stopped; see console output'}
