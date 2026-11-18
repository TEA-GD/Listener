function Get-ListenerGuiPlan {
    param(
        [ValidateSet('Local','Domain')][string]$AccessMode='Local',
        [ValidateRange(1,65535)][int]$Port=5080,
        [string]$ComputerName
    )
    $shareHost='127.0.0.1'; $bindHost='127.0.0.1'
    if ($AccessMode -eq 'Domain') {
        if (-not $ComputerName) {
            $network=[Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties()
            $ComputerName=[Net.Dns]::GetHostName()
            if ($network.DomainName -and [Uri]::CheckHostName($network.DomainName) -eq [UriHostNameType]::Dns -and -not $ComputerName.EndsWith('.'+$network.DomainName,[StringComparison]::OrdinalIgnoreCase)) { $ComputerName+='.'+$network.DomainName }
        }
        if ([Uri]::CheckHostName($ComputerName) -eq [UriHostNameType]::Unknown) { throw 'Cannot determine a valid server hostname.' }
        $shareHost=$ComputerName; $bindHost='0.0.0.0'
    }
    $share=[UriBuilder]::new('http',$shareHost,$Port)
    [pscustomobject]@{
        AccessMode=$AccessMode
        BindUrl="http://$($bindHost):$Port"
        BrowserUrl="http://127.0.0.1:$Port"
        ShareUrl=$share.Uri.AbsoluteUri.TrimEnd('/')
        FirewallName="Listener-GUI-Domain-TCP-$Port"
    }
}

function Set-ListenerGuiDomainFirewall {
    param([ValidateRange(1,65535)][int]$Port=5080)
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Windows Firewall configuration requires Windows.' }
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run the launcher as administrator to configure the Domain firewall rule, or clear Configure firewall and use an existing administrator/GPO rule.' }
    } finally { $identity.Dispose() }
    $ruleName="Listener-GUI-Domain-TCP-$Port"
    $rule=Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue
    if ($rule) {
        Set-NetFirewallRule -Name $ruleName -Enabled True -Profile Domain -Direction Inbound -Action Allow -RemoteAddress Any -EdgeTraversalPolicy Block | Out-Null
        Get-NetFirewallRule -Name $ruleName | Get-NetFirewallPortFilter | Set-NetFirewallPortFilter -Protocol TCP -LocalPort $Port | Out-Null
    } else {
        New-NetFirewallRule -Name $ruleName -DisplayName "Listener GUI TCP $Port (Domain network)" -Enabled True -Profile Domain -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -RemoteAddress Any -EdgeTraversalPolicy Block | Out-Null
    }
    Write-Host "Enabled Windows Firewall rule $ruleName on the Domain profile."
    if (-not (Get-NetConnectionProfile | Where-Object { $_.NetworkCategory -eq 'DomainAuthenticated' })) {
        Write-Warning 'No active DomainAuthenticated network profile was detected. The Domain firewall rule will not permit connections on Public/Private interfaces. Check domain network detection or your managed firewall policy.'
    }
}
