param(
    [string]$DatabasePath=(Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'WatcherCollector\watcher-events.db'),
    [ValidateRange(1,65535)][int]$Port=5080,
    [string]$AnalysisDatabasePath = '',
    [string]$MailViewKeyFile='',
    [ValidateSet('Local','Domain')][string]$AccessMode='Local'
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Launcher.Common.ps1')
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()
$guiForm=New-Object Windows.Forms.Form
$guiForm.Text='Listener GUI launcher';$guiForm.ClientSize=New-Object Drawing.Size(650,485)
$guiForm.StartPosition='CenterScreen';$guiForm.FormBorderStyle='FixedDialog';$guiForm.MaximizeBox=$false
$guiForm.Font=New-Object Drawing.Font('Segoe UI',9)
function Add-LauncherLabel([string]$Text,[int]$Y) {
    $label=New-Object Windows.Forms.Label;$label.Text=$Text;$label.SetBounds(20,$Y,600,20);$guiForm.Controls.Add($label)
}
Add-LauncherLabel 'Choose who can open the Listener dashboard' 18
$modeBox=New-Object Windows.Forms.ComboBox;$modeBox.DropDownStyle='DropDownList';$modeBox.SetBounds(20,44,390,28)
[void]$modeBox.Items.Add('Local only - this PC');[void]$modeBox.Items.Add('Domain network - other PCs can connect')
$modeBox.SelectedIndex=0;if($AccessMode -eq 'Domain'){$modeBox.SelectedIndex=1};$guiForm.Controls.Add($modeBox)
$portBox=New-Object Windows.Forms.NumericUpDown;$portBox.SetBounds(465,44,160,28);$portBox.Minimum=1;$portBox.Maximum=65535;$portBox.Value=$Port;$guiForm.Controls.Add($portBox)
$portLabel=New-Object Windows.Forms.Label;$portLabel.Text='TCP port';$portLabel.SetBounds(465,20,160,20);$guiForm.Controls.Add($portLabel)
Add-LauncherLabel 'Collector database file' 88
$databaseBox=New-Object Windows.Forms.TextBox;$databaseBox.Text=$DatabasePath;$databaseBox.SetBounds(20,112,510,27);$guiForm.Controls.Add($databaseBox)
$dbBrowse=New-Object Windows.Forms.Button;$dbBrowse.Text='Browse...';$dbBrowse.SetBounds(540,110,85,29);$guiForm.Controls.Add($dbBrowse)
Add-LauncherLabel 'Mail viewing key file (optional; mail stays disabled without a configured key)' 154
$keyBox=New-Object Windows.Forms.TextBox;$keyBox.Text=$MailViewKeyFile;$keyBox.SetBounds(20,178,510,27);$guiForm.Controls.Add($keyBox)
$keyBrowse=New-Object Windows.Forms.Button;$keyBrowse.Text='Browse...';$keyBrowse.SetBounds(540,176,85,29);$guiForm.Controls.Add($keyBrowse)
$firewallBox=New-Object Windows.Forms.CheckBox;$firewallBox.Text='Configure Windows Firewall for Domain network (administrator required)';$firewallBox.Checked=$true;$firewallBox.SetBounds(20,222,610,26);$guiForm.Controls.Add($firewallBox)
$browserBox=New-Object Windows.Forms.CheckBox;$browserBox.Text='Open the dashboard in this PC''s browser';$browserBox.Checked=$true;$browserBox.SetBounds(20,252,610,26);$guiForm.Controls.Add($browserBox)
Add-LauncherLabel 'Browser URL to use / share' 288
$urlBox=New-Object Windows.Forms.TextBox;$urlBox.ReadOnly=$true;$urlBox.SetBounds(20,312,605,27);$guiForm.Controls.Add($urlBox)
$guiInfo=New-Object Windows.Forms.Label;$guiInfo.SetBounds(20,350,605,70);$guiInfo.Text='Domain mode enables network access, not AD sign-in. The firewall rule applies only to the Windows Domain profile. Use HTTPS via your reverse proxy for remote mail viewing. Run elevated if the database requires administrator access.';$guiForm.Controls.Add($guiInfo)
$startButton=New-Object Windows.Forms.Button;$startButton.Text='Start GUI';$startButton.SetBounds(20,435,135,32);$guiForm.Controls.Add($startButton)
$cancelButton=New-Object Windows.Forms.Button;$cancelButton.Text='Cancel';$cancelButton.SetBounds(520,435,105,32);$cancelButton.Add_Click({$guiForm.Close()});$guiForm.Controls.Add($cancelButton);$guiForm.CancelButton=$cancelButton;$guiForm.AcceptButton=$startButton
function Update-LauncherMode {
    $guiMode='Local';if($modeBox.SelectedIndex -eq 1){$guiMode='Domain'}
    $firewallBox.Enabled=($guiMode -eq 'Domain')
    $guiPlan=Get-ListenerGuiPlan -AccessMode $guiMode -Port ([int]$portBox.Value)
    $urlBox.Text=$guiPlan.ShareUrl
}
$modeBox.Add_SelectedIndexChanged({Update-LauncherMode});$portBox.Add_ValueChanged({Update-LauncherMode});Update-LauncherMode
$dbBrowse.Add_Click({$dialog=New-Object Windows.Forms.OpenFileDialog;$dialog.Filter='SQLite databases (*.db;*.sqlite)|*.db;*.sqlite|All files (*.*)|*.*';if($dialog.ShowDialog($guiForm)-eq 'OK'){$databaseBox.Text=$dialog.FileName};$dialog.Dispose()})
$keyBrowse.Add_Click({$dialog=New-Object Windows.Forms.OpenFileDialog;$dialog.Filter='Key files (*.key)|*.key|All files (*.*)|*.*';if($dialog.ShowDialog($guiForm)-eq 'OK'){$keyBox.Text=$dialog.FileName};$dialog.Dispose()})
$startButton.Add_Click({
    try {
        $guiMode='Local';if($modeBox.SelectedIndex -eq 1){$guiMode='Domain'}
        $guiElevate=($guiMode -eq 'Domain' -and $firewallBox.Checked)
        if([string]::IsNullOrWhiteSpace($databaseBox.Text)){throw 'Choose the database file first.'}
        $guiDatabase=[IO.Path]::GetFullPath($databaseBox.Text)
        $guiKey='';if($keyBox.Text){$guiKey=[IO.Path]::GetFullPath($keyBox.Text)}
        # An unelevated account may not be able to inspect ProgramData. When UAC
        # is requested, let the elevated server validate access before any firewall change.
        if(-not $guiElevate -and -not(Test-Path -LiteralPath $guiDatabase -PathType Leaf)){throw 'Choose an existing database file that this account can read, or run this launcher as administrator.'}
        if(-not $guiElevate -and $guiKey -and -not(Test-Path -LiteralPath $guiKey -PathType Leaf)){throw 'The selected mail viewing key file does not exist or is inaccessible.'}
        # File paths are passed as quoted -File arguments, never interpolated into -Command.
        $guiScriptPath=Join-Path $PSScriptRoot 'Start-Windows.ps1'
        $guiArguments=@('-NoExit','-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$guiScriptPath+'"'),'-AccessMode',$guiMode,'-Port',([int]$portBox.Value),'-DatabasePath',('"'+$guiDatabase+'"'))
        if($AnalysisDatabasePath){$guiArguments+=@('-AnalysisDatabasePath',('"'+$AnalysisDatabasePath+'"'))}
        if($guiKey){$guiArguments+=@('-MailViewKeyFile',('"'+$guiKey+'"'))}
        if($browserBox.Checked){$guiArguments+='-OpenBrowser'}
        $guiStartParameters=@{FilePath=(Join-Path ([Environment]::GetFolderPath('System')) 'WindowsPowerShell\v1.0\powershell.exe');ArgumentList=$guiArguments;PassThru=$true}
        if($guiElevate){$guiArguments+='-ConfigureFirewall';$guiStartParameters.ArgumentList=$guiArguments;$guiStartParameters.Verb='RunAs'}
        $guiServerProcess=Start-Process @guiStartParameters
        $guiForm.Close()
    } catch { [Windows.Forms.MessageBox]::Show($guiForm,$_.Exception.Message,'Listener GUI launcher','OK','Error') | Out-Null }
})
[void]$guiForm.ShowDialog();$guiForm.Dispose()
