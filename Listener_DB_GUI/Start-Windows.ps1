param(
    [string]$DatabasePath = (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'WatcherCollector\watcher-events.db'),
    [ValidateRange(1,65535)][int]$Port = 5080,
    [string]$AnalysisDatabasePath = '',
    [string]$MailViewKeyFile,
    [ValidateSet('Local','Domain')][string]$AccessMode='Local',
    [switch]$ConfigureFirewall,
    [switch]$OpenBrowser
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Launcher.Common.ps1')
if ($ConfigureFirewall -and $AccessMode -ne 'Domain') { throw '-ConfigureFirewall is only available with -AccessMode Domain.' }
if (-not (Test-Path -LiteralPath $DatabasePath -PathType Leaf)) { throw "Database not found or inaccessible: $DatabasePath" }
try {
    $guiDatabaseProbe=[IO.File]::Open($DatabasePath,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $guiDatabaseProbe.Dispose()
} catch { throw "Cannot read database: $DatabasePath. Run with an account allowed to read it." }
if($MailViewKeyFile){
    if(-not(Test-Path -LiteralPath $MailViewKeyFile -PathType Leaf)){throw 'The mail viewing key file does not exist or is inaccessible.'}
    if([IO.File]::ReadAllText($MailViewKeyFile).Trim().Length -lt 32){throw 'The mail viewing key file must contain at least 32 characters.'}
}
$guiPlan=Get-ListenerGuiPlan -AccessMode $AccessMode -Port $Port
# Check before changing firewall state or opening a browser. The GUI server still
# handles any race where another process takes the port after this check.
$guiProbe=[Net.Sockets.Socket]::new([Net.Sockets.AddressFamily]::InterNetwork,[Net.Sockets.SocketType]::Stream,[Net.Sockets.ProtocolType]::Tcp)
try {
    $guiProbe.ExclusiveAddressUse=$true
    $guiAddress=[Net.IPAddress]::Loopback;if($AccessMode -eq 'Domain'){$guiAddress=[Net.IPAddress]::Any}
    $guiProbe.Bind([Net.IPEndPoint]::new($guiAddress,$Port))
} catch { throw "TCP port $Port is already in use or unavailable. Stop the existing GUI server before switching mode, or choose another port." }
finally { $guiProbe.Dispose() }
$guiBrowserJob=$null
$previousAnalysis = $env:WATCHER_ANALYSIS_DB_PATH
$previousPath = $env:WATCHER_DB_PATH
$previousMailFile = $env:LISTENER_MAIL_VIEW_TOKEN_FILE
$previousMailToken = $env:LISTENER_MAIL_VIEW_TOKEN
try {
    if ($AnalysisDatabasePath) { $env:WATCHER_ANALYSIS_DB_PATH = [IO.Path]::GetFullPath($AnalysisDatabasePath) }
    if ($MailViewKeyFile) { $env:LISTENER_MAIL_VIEW_TOKEN = $null; $env:LISTENER_MAIL_VIEW_TOKEN_FILE = (Resolve-Path -LiteralPath $MailViewKeyFile).Path }
    $env:WATCHER_DB_PATH = (Resolve-Path -LiteralPath $DatabasePath).Path
    if ($ConfigureFirewall) { Set-ListenerGuiDomainFirewall -Port $Port }
    Write-Host "GUI access mode: $AccessMode; listening on $($guiPlan.BindUrl)"
    Write-Host "Open on this PC: $($guiPlan.BrowserUrl)"
    if ($AccessMode -eq 'Domain') {
        Write-Host "Share with domain-network users: $($guiPlan.ShareUrl)" -ForegroundColor Cyan
        Write-Host 'Network access does not enable Windows/AD sign-in. Mail alerts still require the configured viewing key.'
        if (-not $ConfigureFirewall) { Write-Host "Ensure an existing Windows Firewall/GPO rule permits inbound TCP $Port on the Domain profile." }
    }
    Write-Host 'Keep this server console open; Ctrl+C stops the GUI. Stop an existing GUI before changing its access mode on the same port.'
    if ($OpenBrowser) {
        $guiBrowserJob=Start-Job -ArgumentList $guiPlan.BrowserUrl -ScriptBlock {
            param($BrowserUrl)
            for($attempt=0;$attempt -lt 60;$attempt++) {
                try {
                    $probe=[Net.WebRequest]::Create($BrowserUrl);$probe.Timeout=1000
                    $response=$probe.GetResponse();$response.Close()
                    Start-Process $BrowserUrl | Out-Null
                    return
                } catch { Start-Sleep -Milliseconds 500 }
            }
        }
    }
    $exe=Join-Path $PSScriptRoot 'Listener_DB_GUI.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { $exe=Join-Path $PSScriptRoot 'output\windows\Listener_DB_GUI.exe' }
    if (Test-Path -LiteralPath $exe -PathType Leaf) { & $exe --urls $guiPlan.BindUrl }
    else { & dotnet run --project (Join-Path $PSScriptRoot 'Listener_DB_GUI.csproj') -c Release -- --urls $guiPlan.BindUrl }
    if ($LASTEXITCODE -ne 0) { throw 'Dashboard stopped with an error. See the server console above.' }
}
finally {
    if($guiBrowserJob){Stop-Job -Job $guiBrowserJob -ErrorAction SilentlyContinue;Remove-Job -Job $guiBrowserJob -Force -ErrorAction SilentlyContinue}
    $env:WATCHER_ANALYSIS_DB_PATH = $previousAnalysis; $env:WATCHER_DB_PATH = $previousPath; $env:LISTENER_MAIL_VIEW_TOKEN_FILE = $previousMailFile; $env:LISTENER_MAIL_VIEW_TOKEN = $previousMailToken }
