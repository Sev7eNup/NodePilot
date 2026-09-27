# Adding HTTPS to an existing WinRM connection

HTTPS access is to be provided for `srv-app01.contoso.com`. WinRM is already running and PowerShell remoting is configured. The change can therefore focus on the server certificate, its listener binding, and subsequent verification from the management computer.

HTTPS adds TLS transport protection and a server identity that can be verified through its certificate. This is particularly useful across trust boundaries or without Kerberos. Existing authorization checks remain part of access control.

## 1. Select the certificate

For a connection to `srv-app01.contoso.com`, the certificate must cover that name, usually through a matching Subject Alternative Name. A certificate for another name does not become suitable merely because both DNS entries resolve to the same IP address.

On the target system, the server certificate and its private key belong in the local computer's personal certificate store. The certificate must be valid and intended for Server Authentication. The management computer must be able to validate its trust chain. A certificate from the designated enterprise PKI is a suitable option for regular operation. Microsoft describes the required properties in its [HTTPS configuration guidance](https://learn.microsoft.com/en-us/troubleshoot/windows-client/system-management-components/configure-winrm-for-https).

An initial inventory runs locally on the target server:

```powershell
Get-ChildItem -Path Cert:\LocalMachine\My |
    Select-Object Subject, DnsNameList, NotAfter,
        HasPrivateKey, EnhancedKeyUsageList, Thumbprint
```

The output helps with selection. It does not replace complete verification of the certificate chain and revocation status from the eventual management computer.

## 2. Create the listener and restrict network access

The new listener adds a transport to the existing remoting configuration. It does not register a missing PowerShell endpoint. Before making a change, enumerate listeners to establish whether an HTTPS configuration already exists:

```powershell
winrm enumerate winrm/config/listener
```

A new listener can be created in an elevated PowerShell session on the target server using an explicitly selected certificate. Replace the hostname and thumbprint with the appropriate values. The command changes configuration but does not update an existing listener. [Microsoft: HTTPS listener with New-WSManInstance](https://learn.microsoft.com/en-us/powershell/module/microsoft.wsman.management/new-wsmaninstance).

```powershell
$listenerConfig = @{
    ResourceURI = 'winrm/config/Listener'
    SelectorSet = @{
        Transport = 'HTTPS'
        Address   = '*'
    }
    ValueSet = @{
        Hostname              = 'srv-app01.contoso.com'
        CertificateThumbprint = '<Server certificate thumbprint>'
    }
}

New-WSManInstance @listenerConfig
winrm enumerate winrm/config/listener
```

`Address='*'` binds the listener to all available local addresses. The management computers allowed to reach it must additionally be restricted through the firewall. The regular HTTPS port requires a suitable rule for TCP 5986, preferably limited to the intended management addresses and network profiles.

Adding this listener does not remove an existing HTTP listener. If access is to become HTTPS-only, disabling the previous transport belongs in a separate transition step after its consumers have migrated to HTTPS and been tested. [Microsoft: WinRM listeners and configuration](https://learn.microsoft.com/en-us/windows/win32/winrm/installation-and-configuration-for-windows-remote-management).

## 3. Test under the eventual operating conditions

Use the same DNS name that the automation will use later. First test TCP connectivity, followed by an authenticated WS-Management request. This example assumes a domain environment with working Kerberos:

```powershell
$target = 'srv-app01.contoso.com'

Test-NetConnection -ComputerName $target -Port 5986
Test-WSMan -ComputerName $target -UseSSL -Port 5986 `
    -Authentication Kerberos
```

Without `-UseSSL`, the command does not test the intended HTTPS connection. The authentication parameter matters equally: without it, `Test-WSMan` sends an anonymous identification request. A response then does not establish successful user authentication. [Microsoft: Test-WSMan](https://learn.microsoft.com/en-us/powershell/module/microsoft.wsman.management/test-wsman).

An actual remote session additionally tests access to the selected PowerShell endpoint. In the following example, `Microsoft.PowerShell` must exist on the target and permit access by the user:

```powershell
Invoke-Command -ComputerName $target -UseSSL -Port 5986 `
    -Authentication Kerberos -ConfigurationName 'Microsoft.PowerShell' `
    -ScriptBlock {
        [pscustomobject]@{
            Computer = $env:COMPUTERNAME
            Identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
            Version  = $PSVersionTable.PSVersion.ToString()
        }
    }
```

The endpoint has its own access permissions. Successful TLS connection establishment therefore says nothing about whether the account may execute commands there. [Microsoft: PowerShell session configurations](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_session_configurations).

## Authentication remains a separate decision

Even over HTTP, WinRM encrypts PowerShell messages at the message layer after Kerberos authentication. HTTPS adds TLS, with the server certificate allowing verification of the remote system. User authentication remains a separate task. An HTTPS listener therefore neither grants access rights nor automatically solves delegation problems. [Microsoft: PowerShell remoting security](https://learn.microsoft.com/en-us/powershell/scripting/security/remoting/winrm-security).

Within a domain, HTTPS and Kerberos can be used together. Outside that setting, the authentication method needs separate definition. Distinguish the server certificate for TLS from explicitly configured client certificate authentication. Providing the former does not automatically enable the latter.

## Certificate maintenance starts after setup

For a name mismatch, compare the DNS name with the certificate. For a failed trust check, investigate the certificate chain and its availability on the management computer. Permanently skipping certificate validation would leave these causes unresolved.

After renewal, verify which certificate the listener actually uses. Its `CertificateThumbprint` must match the selected certificate. A valid certificate in the server store alone does not establish that binding. [Microsoft: Compare the certificate and listener](https://learn.microsoft.com/en-us/troubleshoot/windows-client/system-management-components/configure-winrm-for-https).

The final test again runs from the intended management computer and includes opening an actual session. Thumbprint and expiry date belong among the checked details. For a connection later used through NodePilot, this test should also remain part of operations after the next certificate renewal.
