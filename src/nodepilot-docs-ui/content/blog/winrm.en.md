# Understanding WinRM: what a successful connection actually proves

`Test-WSMan` returns a response. Initially, this establishes only that the addressed WinRM service responded to an identification request. Diagnosis depends on which additional prerequisites the particular invocation actually tests.

Without an explicitly supplied authentication parameter, `Test-WSMan` sends its identification request anonymously. A response therefore does not establish successful user authentication. Even an authenticated test does not replace opening the intended PowerShell endpoint. [Microsoft: Test-WSMan behavior](https://learn.microsoft.com/en-us/powershell/module/microsoft.wsman.management/test-wsman).

Several separate checks stand between a reachable service and a permitted operation. Keeping them separate provides a path through configuration in which permissions are examined where they actually matter.

These examples concern Windows with Active Directory and PowerShell remoting over WS-Management. Computer names and paths are placeholders. Diagnostic commands are explicitly distinguished from configuration changes.

## From the client to the remote system's identity

Windows Remote Management is Microsoft's implementation of WS-Management. PowerShell remoting uses this management service as its transport, while the session itself executes within a PowerShell endpoint on the target. This creates several independently testable prerequisites. [Microsoft: Windows Remote Management](https://learn.microsoft.com/en-us/windows/win32/winrm/portal).

| Layer | What must be present | What success does not yet establish |
|---|---|---|
| Network | A reachable target port and suitable firewall rules | That the expected service is responding |
| WinRM | A running service and suitable listener | That the user can authenticate |
| Authentication | An accepted identity and appropriate mechanism | That the PowerShell endpoint is accessible |
| Session endpoint | A registered configuration and access permission | That the intended application operation is permitted |
| Target resource | Permissions on the service, file, or application | That another computer accepts the same identity |

Regular listeners use TCP 5985 for HTTP and TCP 5986 for HTTPS. A running service alone says nothing about a usable listener. Run the following queries locally on the target to inspect the actual configuration, including bound addresses. [Microsoft: WinRM installation and configuration](https://learn.microsoft.com/en-us/windows/win32/winrm/installation-and-configuration-for-windows-remote-management).

```powershell
Get-Service -Name WinRM
winrm enumerate winrm/config/listener
winrm get winrm/config/service
```

With multiple network adapters in particular, compare the expected management address with `ListeningOn`. A configuration can exist formally while the network path being addressed misses it.

### Listener addresses and permitted source computers

For central deployment, the “Allow remote server management through WinRM” policy is under Computer Configuration, Administrative Templates, “Windows Components”, “Windows Remote Management (WinRM)”, “WinRM Service”.

Its IPv4 and IPv6 filters determine which local addresses the target computer listens on. They do not list permitted management computers. An asterisk, `*`, means all available local addresses, while an empty filter means no address of that family. This distinction matters when a server serves several network segments. [Microsoft: AllowRemoteServerManagement policy](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-remotemanagement#allowremoteservermanagement).

Restricting incoming connections to management networks belongs in the firewall. The HTTP policy also does not automatically create an HTTPS listener with a certificate. Service configuration, listeners, and firewall rules need to be managed as related but separate settings.

For an inventory, the active network profile and resultant policies matter as much as the locally visible configuration:

```powershell
Get-NetConnectionProfile
gpresult /Scope Computer /r
winrm get winrm/config
```

If WinRM shows `Source="GPO"` for a setting, a local change is not the authoritative correction. A firewall rule may also behave differently depending on the active network profile. Before another attempt, establish which policy and profile actually apply. [Microsoft: Remoting troubleshooting](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_remote_troubleshooting).

A staged introduction follows from this: begin with a limited server group, then test connectivity from the intended management network and attempt access from a network that is not permitted. This checks both functionality and the intended restriction.

### Encryption and server identity

With Kerberos, using HTTP for PowerShell remoting does not mean commands and results travel in plaintext. After authentication, WinRM protects messages at the protocol layer. HTTPS adds TLS transport and allows server identity to be checked through a certificate. Transport and authentication are separate decisions. [Microsoft: PowerShell remoting security](https://learn.microsoft.com/en-us/powershell/scripting/security/remoting/winrm-security).

For domain-joined systems with working Kerberos, HTTP can therefore be a justified operational choice. HTTPS is particularly useful when Kerberos is unavailable or network policy requires TLS. Basic provides no message encryption of its own. Combining Basic, HTTP, and `AllowUnencrypted=true` gives up that protection and is unsuitable as a troubleshooting measure. [Microsoft PowerShell Team: Risks of AllowUnencrypted](https://devblogs.microsoft.com/powershell/compromising-yourself-with-winrms-allowunencrypted-true/).

`TrustedHosts` is also easily misunderstood. An entry does not confirm a server's identity. It suppresses the corresponding check for listed destinations on connections where the remote identity cannot otherwise be verified through that mechanism. It neither grants server permissions nor replaces a firewall rule. A blanket `*` extends this exception to arbitrary destinations. [Microsoft: TrustedHosts and server identity](https://learn.microsoft.com/en-us/powershell/scripting/security/remoting/winrm-security).

HTTPS requires a suitable server certificate in the local computer's certificate store. The DNS name used must match the certificate, and the client must be able to validate its validity and trust chain. Enhanced Key Usage must include Server Authentication. Microsoft describes these prerequisites, including Subject Alternative Name checks, in its [HTTPS configuration guidance](https://learn.microsoft.com/en-us/troubleshoot/windows-client/system-management-components/configure-winrm-for-https).

Operations also needs an owner for certificate renewal. After replacement, check which certificate is actually bound to the listener and whether the management computer still accepts the connection. A newly issued certificate in the store alone is not a successful functional test.

### Kerberos and SPN assignment

A fully qualified DNS name provides a better diagnostic starting point than an IP address. With `Negotiate`, the connection may negotiate Kerberos or NTLM. Explicitly specifying `Kerberos` prevents successful NTLM fallback from concealing a Kerberos failure. [Microsoft: Test-WSMan authentication methods](https://learn.microsoft.com/en-us/powershell/module/microsoft.wsman.management/test-wsman).

```powershell
$target = 'srv-app01.contoso.com'

Resolve-DnsName -Name $target
Test-NetConnection -ComputerName $target -Port 5985
Test-WSMan -ComputerName $target -Authentication Kerberos
```

Perform these checks from the actual management computer. If later automation uses a different identity, also test under its conditions. A successful session from an administrator's workstation does not establish that the executing service has the same network access and permissions.

One particularly informative error is `0x80090322`. For WinRM, PowerShell uses an SPN in the form `HTTP/<FQDN>` by default. If that SPN is assigned to another service account, perhaps for a web application, Kerberos authentication can fail. An initial read-only check is therefore:

```powershell
setspn -Q HTTP/srv-app01.contoso.com
```

Any assignment found must be compared with the intended service account. It should not be deleted merely because WinRM reports an error. Microsoft describes port-specific SPNs together with `IncludePortInSPN` among the solutions for certain conflicts. This is a coordinated configuration change, not a general repair switch. [Microsoft: WinRM error 0x80090322](https://learn.microsoft.com/en-us/troubleshoot/windows-server/system-management-components/error-0x80090322-when-connecting-powershell-to-remote-server-via-winrm).

## The session and its permissions

A reachable WinRM service does not guarantee a PowerShell session. Session configurations have their own security descriptors controlling access to each endpoint. Permissions on the resources actually being addressed continue to apply afterwards. [Microsoft: Session configurations](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_session_configurations).

Registered configurations can be listed locally on the target in an elevated PowerShell session:

```powershell
Get-PSSessionConfiguration |
    Select-Object Name, PSVersion, Permission
```

With Windows PowerShell 5.1 and PowerShell 7 installed side by side, the intended runtime must also be established. `Enable-PSRemoting` configures endpoints for the PowerShell installation in which it runs. Running it in PowerShell 7 does not automatically replace Windows PowerShell configuration. The command changes service, firewall, and session settings and therefore belongs in planned deployment. [Microsoft: Enable-PSRemoting](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/enable-psremoting).

The following test assumes that `PowerShell.7` is registered and accessible to the user:

```powershell
Invoke-Command -ComputerName 'srv-app01.contoso.com' `
    -Authentication Kerberos -ConfigurationName 'PowerShell.7' `
    -ScriptBlock {
        [pscustomobject]@{
            Computer = $env:COMPUTERNAME
            Identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
            Version  = $PSVersionTable.PSVersion.ToString()
            Edition  = $PSVersionTable.PSEdition
        }
    }
```

This exposes the target computer, execution identity, and runtime within the same session. It provides better evidence than assuming a locally installed PowerShell version is automatically used on the remote computer too.

### A limited interface with JEA

A service account intended to inspect a particular service does not necessarily need an unrestricted administrative endpoint. Just Enough Administration, or JEA, allows limited management functions to be exposed. A Role Capability can specify both cmdlets and their permitted parameter values. [Microsoft: JEA Role Capabilities](https://learn.microsoft.com/en-us/powershell/scripting/security/remoting/jea/role-capabilities).

A fragment for a read-only service query might look like this:

```powershell
@{
    VisibleCmdlets = @{
        Name = 'Get-Service'
        Parameters = @{
            Name = 'Name'
            ValidateSet = 'Spooler'
        }
    }
}
```

This is not a complete endpoint configuration. The Role Capability must be deployed as a `.psrc` file in a suitable module and assigned to a role in the session configuration. The fragment restricts the value of a supplied `Name` parameter. It does not make that parameter mandatory: without `Name`, `Get-Service` could still list services. If only one fixed service should be visible, a custom wrapper function with a hard-coded service name provides a narrower interface. Broad permissions such as `*-Service` or arbitrary executable programs would undermine that boundary.

JEA configuration also includes the execution identity. Virtual accounts support local management tasks, while a gMSA can provide a domain identity for required network access. When an identity is shared, attributing an action to the original user depends on additional logging. [Microsoft: JEA session configuration](https://learn.microsoft.com/en-us/powershell/scripting/security/remoting/jea/session-configurations).

The default privileges of virtual accounts deserve particular attention. On member servers they belong to the local Administrators group by default, and on domain controllers to Domain Admins. Different group assignments can be specified explicitly. A member-server configuration should therefore not be transferred unchanged to a domain controller. [Microsoft: JEA security considerations](https://learn.microsoft.com/en-us/powershell/scripting/security/remoting/jea/security-considerations).

### Accessing a third computer

The typical case starts on management computer A. A session is opened from there to server B, where a command needs to access a share on server C. The first authentication succeeds, but the second access is denied because the original credentials are not simply forwarded.

Before configuring delegation, establish what the second access actually involves. SMB and another WinRM session have different prerequisites. For both traditional and resource-based Kerberos constrained delegation, Microsoft explicitly identifies the limitation that a second hop for WinRM is unsupported. Successful file-share access therefore does not prove that nested remoting works. [Microsoft: The second hop in PowerShell remoting](https://learn.microsoft.com/en-us/powershell/scripting/security/remoting/ps-remoting-second-hop).

CredSSP can delegate credentials to server B, increasing the trust placed in that server. If B is compromised, those credentials are also at risk. It should therefore not be enabled merely because a script reports “Access denied” on a share. The same Microsoft article explains the alternatives and their boundaries.

Often, the process can be divided so that A contacts both resources directly. If a configuration file already resides on A, for example, it can be transferred to B through an existing session. B needs no authentication to C for that transfer. `Copy-Item` supports `-ToSession` for this purpose. [Microsoft: Copy-Item with remote sessions](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.management/copy-item).

The following example writes a file to the target computer and can overwrite a destination file with the same name. It assumes an existing source file and an existing, writable destination folder:

```powershell
$session = New-PSSession -ComputerName 'srv-app01.contoso.com' `
    -Authentication Kerberos -ErrorAction Stop

try {
    Copy-Item -LiteralPath 'C:\Staging\appsettings.json' `
        -Destination 'C:\ProgramData\ContosoApp\appsettings.json' `
        -ToSession $session -ErrorAction Stop
}
finally {
    Remove-PSSession -Session $session
}
```

This approach solves the specific file transfer. It does not give the remote process a general identity for further network access. If the application itself needs continuing access to a share, its service identity requires the appropriate permissions.

## Data and load within a running session

Transferred PowerShell objects are generally serialized snapshots of state. After deserialization, their properties are available, but the original methods of the live object are not. Locally received service information is therefore not a proxy through which arbitrary methods continue to execute on the server. [Microsoft: Remote command output](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_remote_output).

The required operation belongs in the remote ScriptBlock. Return small results suitable for subsequent processing:

```powershell
Invoke-Command -ComputerName 'srv-app01.contoso.com' `
    -Authentication Kerberos -ScriptBlock {
        Get-Service -Name Spooler -ErrorAction Stop |
            Select-Object Name, Status, StartType
    }
```

Formatting commands such as `Format-Table` are useful only at the final presentation stage. For data exchange, preserve the necessary properties instead of producing human-formatted output on the target already.

### Quotas across several jobs

A single successful invocation says little about how a hundred concurrent jobs behave. WinRM limits, among other things, shells per user as well as processes and memory per shell. Effective quotas can be read on the target:

```powershell
winrm get winrm/config/winrs
```

Group Policy can override local quotas. Check values in the actual environment before treating an assumed default as the cause. [Microsoft: Remote shell quotas](https://learn.microsoft.com/en-us/windows/win32/winrm/quotas).

`Invoke-Command -ThrottleLimit` restricts concurrency for that invocation. It does not establish a shared upper limit for several simultaneous invocations. [Microsoft: Invoke-Command](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/invoke-command).

Central automation therefore has an operational requirement to limit concurrency across jobs too. Otherwise, several schedulers or script instances multiply the load even when each invocation uses a conservative value. First record concurrent sessions, durations, and failures per target. Increasing a quota is justified only once the actual bottleneck is established.

## Correlating and recording findings

A fixed diagnostic sequence helps because each step tests a different prerequisite. Once a layer is confirmed, investigation can move to the next boundary without changing working settings preemptively.

| Observation | Next check |
|---|---|
| DNS returns an unexpected address | Compare name resolution with the intended management name |
| TCP 5985 or 5986 is unreachable | Inspect routing, firewall profile, and listener address |
| WS-Management responds but Kerberos fails | Check the identity used and SPN assignment |
| Authenticated test succeeds but the session fails | Check the endpoint name and its permissions |
| Session works but the application operation fails | Inspect the effective identity and target resource permissions |
| Only access from B to C fails | Establish the second hop's protocol and identity |
| Failures appear only with many jobs | Measure shared concurrency and effective quotas |

For an HTTPS connection, the test itself must use the correct transport:

```powershell
Test-WSMan -ComputerName 'srv-app01.contoso.com' `
    -UseSSL -Port 5986 -Authentication Kerberos
```

This test assumes an environment where Kerberos is possible. In a workgroup, authentication needs a different plan. An HTTPS listener alone does not replace suitable user authentication.

Operational diagnosis also includes logging for the PowerShell runtime actually used. Windows PowerShell 5.1 uses `Microsoft-Windows-PowerShell/Operational` for Script Block Logging, while PowerShell 7 uses `PowerShellCore/Operational`. With Script Block Logging enabled, events with ID 4104 appear there. Configuration and availability should be checked separately for both runtimes. [Microsoft: Windows PowerShell logging](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_logging?view=powershell-5.1), [Microsoft: PowerShell logging on Windows](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_logging_windows).

These logs can contain script content and therefore confidential details. Access, forwarding, and retention belong in logging configuration. Microsoft describes Protected Event Logging as an option for protecting sensitive event content. For analysis, record the job identifier, target computer, and timestamp so that a failure can be associated with the relevant execution.

The same correlation applies to a workflow started through NodePilot: identify the target and execution, examine the affected connection layer, and only then correct the relevant setting. Windows authentication and permissions also apply to that execution.
