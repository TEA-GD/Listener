param([string]$ConfigPath=(Join-Path $PSScriptRoot 'config.json'))
$ErrorActionPreference='Stop'
if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
    throw 'Create config.json from config.example.json and set database_path, state_directory and alerts_file first.'
}
$ConfigPath=(Resolve-Path -LiteralPath $ConfigPath).Path
$exe=Join-Path $PSScriptRoot 'output\windows\Listener_Analyzer.exe'
if (Test-Path -LiteralPath $exe) { & $exe --config $ConfigPath }
else { & dotnet run --project (Join-Path $PSScriptRoot 'Listener_Analyzer.csproj') -c Release -- --config $ConfigPath }
if ($LASTEXITCODE -ne 0) { throw 'Analyzer stopped with an error. See the output above.' }
