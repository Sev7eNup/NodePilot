# Windows HTTP diagnosis by layer

Use these checks when relevant to the observed failing operation, not as a mandatory
inventory. Substitute endpoint, service and site names from actual configuration.
All commands below are reads through the existing PowerShell tool. Never change
configuration or start a scan to test a hypothesis.

## Client endpoint, resolution and proxy

First read the application's configured endpoint (for Windows Update, the
WindowsUpdate policy key contains WUServer). Preserve protocol, hostname, port and
path. The agent's WinRM transport address can legitimately differ from a machine's
internal service address. Use computer identity and local IP addresses to establish
which machine was inspected; different transport and DNS addresses alone are not
an identity error.

```powershell
Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate' | Select-Object WUServer,WUStatusServer
Get-Content -LiteralPath 'C:\Windows\System32\drivers\etc\hosts'
Get-DnsClientCache | Select-Object Entry,Data,Type,Status
Resolve-DnsName -Name 'observed-endpoint.example.com' -Type A | Select-Object Name,IPAddress
netsh winhttp show proxy
```

The tool enforces Resolve-DnsName's DnsOnly switch, which prevents LLMNR/NetBIOS
fallback; it is not the separate NoHostsFile switch. A query result alone does not
establish the application's effective resolver result. Inspect hosts/cache entries
alongside it. Match uncommented hosts entries to the actual short name
and FQDN. A loopback address on the client addresses that client, not a remote server.
Read the cache as supplementary evidence; do not flush it. Distinguish a demonstrated
local name mapping defect from an unobserved historical application's resolver result.

WinHTTP settings differ from a user's browser/WinINET proxy. Prefer the semantic
`netsh winhttp show proxy` output to truncated WinHttpSettings binary registry data.
Check the proxy address, port and bypass list against the affected operation. If a
proxy is local, inspect whether its configured TCP port has a listener. Proxy
configuration alone does not prove that every application uses it: name the subsystem
and qualify untested consumers. Do not reset or change the proxy during diagnosis.

## Services and HTTP hosting

```powershell
Get-Service -Name 'observed-service-name' | Select-Object Name,Status,StartType
Get-Website | Select-Object Name,State,PhysicalPath
Get-WebAppPoolState -Name '*' | Select-Object ItemXPath,Value
Get-WebBinding | Select-Object ItemXPath,protocol,bindingInformation
Get-WebApplication -Site 'observed-site-name' | Select-Object Path,ApplicationPool
Get-NetTCPConnection -State Listen | Select-Object LocalAddress,LocalPort,OwningProcess
```

The inline shell returns Get-Service Status and StartType as names. For independent
confirmation or package scripts, Win32_Service State and StartMode are textual:

```powershell
Get-CimInstance -ClassName 'Win32_Service' -Filter "Name='observed-service-name'" -Property Name,State,StartMode | Select-Object Name,State,StartMode
```

Running is not Stopped. Never infer a numeric enum's meaning from its apparent order.
A stopped Manual service can be idle by design; Disabled startup is a different
condition. Tie the required service to the affected operation. A running WsusService
does not prove WsusPool is started, and W3SVC running does not prove a particular
website is started. Conversely, a reachable web endpoint does not prove unrelated
background processing is running.

For Get-WebAppPoolState, ItemXPath identifies the pool and Value gives its state;
the returned Name describes the state property, not the pool name. Get-Website's
Name parameter accepts one name/pattern; omit it to list sites instead of passing
an array of names.

On an IIS server, inspect the relevant site's state, its application pool state and
its actual HTTP binding. Narrow the reads to observed names when identified:

```powershell
Get-WebBinding -Name 'observed-site-name' -Protocol http | Select-Object bindingInformation
Get-WebAppPoolState -Name 'observed-pool-name'
```

Compare the configured client endpoint port to that site's binding and current
listening sockets. An Established socket is not a listener. No match from a filtered
socket query may be reported as an error: inspect a successful broader Listen query
before treating it as missing. A stopped required site/pool or a binding on a different
port establishes a current blocking configuration once matched to the operation.
Missing IIS cmdlets mean unavailable evidence, not proof IIS is absent.

## Time, review and remedy

Establish the hosting chain, not just a similarly named pool: configured URL and
binding -> matching site's application path -> ApplicationPool -> current pool
state. Use Get-WebApplication for child applications and the site's ApplicationPool
for its root application. Match the longest applicable path prefix. Compare the
observed state with the operation's actual requirements. A status code alone does
not select a cause. Recheck the decisive mapping and state independently in review.

Do not turn a demonstrated current blocker into "only historical" just because
the investigation occurs minutes after the failed request. Report two distinct
conclusions: what currently prevents the operation, and what the available logs
prove about the original request. A missing stop-event/actor or unresolved log
timezone limits change attribution, not the independently observed current hosting
dependency. Likewise, never claim that a current state proves all earlier failures.
Choose a remedy from the verified dependency and observed defect, and identify an
end-to-end verification of the original operation. State that repair and success
verification have not yet been performed.

Keep observation times with configuration and runtime state. A successful HTTP
request before a binding or state change does not refute the later blocker. Recheck
the decisive current state when observations conflict; do not discard it because an
older log contains success. A success on another port or subsystem has narrower scope.
Separate the demonstrated current mechanism from a historical attempt whose order
cannot be established. Reviewers verify the decisive values with their own reads.

Recommend the smallest correction to the identified service, site, pool, binding,
hosts entry or proxy policy, subject to confirming its intended configuration. Do not
recommend resetting the whole client, disabling security or changing unrelated items.
Specify an authorized post-change check of the original endpoint/operation. Proposed
repairs and scan triggers are text only; never execute them in this read-only run.

References: [IIS pool runtime state](https://learn.microsoft.com/en-us/powershell/module/webadministration/get-webapppoolstate),
[IIS application mapping](https://learn.microsoft.com/en-us/powershell/module/webadministration/get-webapplication),
[IIS bindings](https://learn.microsoft.com/en-us/powershell/module/webadministration/get-webbinding),
[DNS query options](https://learn.microsoft.com/en-us/powershell/module/dnsclient/resolve-dnsname),
[WinHTTP proxy commands](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/netsh-winhttp).
