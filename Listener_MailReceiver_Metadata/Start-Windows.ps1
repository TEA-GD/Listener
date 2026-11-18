param(
    [Parameter(Mandatory=$true)][string]$InternalDomains,
    [string]$DatabasePath=(Join-Path $env:ProgramData 'WatcherMailMetadata\mail-metadata.db'),
    [string]$TokenFile=(Join-Path $env:ProgramData 'WatcherMailMetadata\receiver.key'),
    [string]$ListenUrl='http://127.0.0.1:6517',
    [ValidateRange(1024,67108864)][int]$MaximumRequestBytes=1048576
)
$ErrorActionPreference='Stop'
if (-not (Test-Path -LiteralPath $TokenFile -PathType Leaf)) { throw 'Create a protected receiver key file first; see README.md.' }
$previousToken=$env:LISTENER_MAIL_TOKEN
$previousFile=$env:LISTENER_MAIL_TOKEN_FILE
try {
    $env:LISTENER_MAIL_TOKEN=$null
    $env:LISTENER_MAIL_TOKEN_FILE=(Resolve-Path -LiteralPath $TokenFile).Path
    $arguments=@('--DatabasePath',$DatabasePath,'--InternalDomains',$InternalDomains,'--urls',$ListenUrl,'--MaximumRequestBytes',"$MaximumRequestBytes")
    $exe=Join-Path $PSScriptRoot 'output\windows\Listener_MailReceiver_Metadata.exe'
    if (Test-Path -LiteralPath $exe) { & $exe @arguments }
    else { & dotnet run --project (Join-Path $PSScriptRoot 'Listener_MailReceiver_Metadata.csproj') -c Release -- @arguments }
    if ($LASTEXITCODE -ne 0) { throw 'Mail receiver stopped with an error. See output above.' }
}
finally { $env:LISTENER_MAIL_TOKEN=$previousToken; $env:LISTENER_MAIL_TOKEN_FILE=$previousFile }
