# Windows SSO/Kerberos field test against a real AD (Hyper-V lab)

Wire-level verification of the NodePilot Windows SSO path (`POST /api/auth/windows`,
Negotiate/Kerberos) against a **real** Active Directory. Closes the gap that all
backend tests end at a synthetic `ClaimsPrincipal` or at the
`ILdapConnectionAdapter` seam — there is not a single real handshake there.

> **Status: Kerberos core verified against a real AD on 2026-08-02.** Run against
> Windows Server 2025 (`corp.contoso.com`, domain/forest mode 2016, one DC), NodePilot
> under a gMSA on a member server, test from a domain-joined Windows 11
> client: **22 PASS / 1 FAIL / 6 SKIP**. The one FAIL was a wrongly passed
> break-glass parameter, not a product defect.
>
> Still open: HAProxy path, multi-DC consensus, OIDC/SCIM, HA restart, as well as
> W15/W16 (revocation), W14 (DC outage), W20/W21 (Restrict-NTLM GPO), W22 and the
> browser evidence W6b/W6c. The preview label can therefore be **refined, but not
> removed**.
>
> **Important regarding the browser path:** the lab run proves the HTTP stack, **not** the browser.
> A first manual attempt on 2026-08-02 opened a credential dialog because the
> browser policy had never been set on the client; the silent second attempt after a
> logout was the credential cache of the browser session, not SSO. W6b is therefore **open**,
> not passed — and was sharpened by exactly this criterion.

### Results of the lab run 2026-08-02

| Item | Evidence |
|---|---|
| **W6a Kerberos** | After login, `Server: HTTP/cm1.corp.contoso.com @ CORP.CONTOSO.COM` is in the ticket cache — a real service ticket, not NTLM |
| **W5d transitive groups** | `nptest.alice` is **only** in `NPTest-Admins`; the admission gate is set to `NPTest-Access`. Login returns 200 + Role=Admin ⇒ `tokenGroups` is resolved transitively, not `memberOf`. Audit: `oldGroupSidsCount: 4` |
| **W9 identity equality** | The same person through both paths ⇒ **the same UserId**. Audit shows `LOGIN_SUCCESS … source: "Ldap"` and `source: "Windows"` on one identity |
| W5a ambient path | The path the browser button takes returns 200 |
| W5c / W8 | `np_auth` httpOnly + `np_csrf` set, no token in the body; JWT contains **no** group/SID claims |
| W10 | Re-login ⇒ `USER_WINDOWS_JIT_UPDATED`, same UserId, no duplicate |
| W11 / W12 | `carol` → Viewer; `bob` → 401 with audit `USER_DIRECTORY_ACCESS_REFUSED / reason=no_allowed_directory_group` |
| W13 | Disabled AD account ⇒ no login |
| W18 | 5 parallel first logins ⇒ all 200, exactly one JIT create |
| W25 | SSO without `np_csrf` ⇒ 200, no 403 |
| W2 | `/healthz/directory` `Healthy` over real LDAPS against the enterprise CA |
| **W19 inconclusive** | 401, but **without** a rejection message and without `windows_ntlm_disabled` in the audit — the server did not see any NTLM attempt at all. See "False PASS" below |

On a WORKGROUP machine without a KDC the following was additionally verified: **W0** (boot aborts with
`AllowNtlmFallback=true`), the **two-phase bootstrap** (bootstrap admin receives
`IsBreakGlass=true`), `/healthz/ready` stays 200 with a dead DC — and there the
**app-level NTLM branch actually triggered**: 401 with *"Kerberos required — NTLM fallback is disabled"*
plus audit `reason=windows_ntlm_disabled, mechanism=NTLM`. So the check is **not
inert**; it just could not be triggered in the lab.

### False PASS: why W19 proves nothing in the lab

The Negotiate handler challenges with `WWW-Authenticate: Negotiate`. A `CredentialCache`
registered only for the `NTLM` package finds no entry for it and sends
**no** `Authorization` header at all — the server never sees an NTLM attempt and
answers with an empty 401. The probe then reported PASS although the audit contained not
a single `windows_ntlm_disabled` line.

`Invoke-NtlmProbe.ps1` now additionally requires the **application's rejection message** in the
body; a 401 without it counts as *inconclusive*, not as success. To really prove W19 in the lab,
`-Mode Alias` is needed: an A record for the same IP for which **no**
HTTP SPN exists, plus this name as a second SAN in the Kestrel certificate. Then
the SPN lookup fails, SPNEGO falls back to NTLM, and the application gets its turn at all.

**Complements** `scripts/ldap-testdc/` (LDAP password path against Samba, 13/13 PASS on
2026-07-24). The Samba DC there deliberately cannot do Kerberos: the Negotiate handshake
needs a domain join, and ASP.NET Negotiate uses SSPI on Windows, not GSSAPI/keytab.

**Does NOT replace** the complete field test from `docs/ldap-windows-sso.md` (10-point
matrix). Not covered: HAProxy path (point 3 stays half open), multi-DC consensus
(points 1/6), OIDC/SCIM (8/9), HA restart (10). After a green
run the preview label can be **refined, but not removed**.

## Lab topology

| Role | FQDN | Task |
|---|---|---|
| DC | `dc01.np.lab` | AD DS, DNS, LDAPS:636 |
| API member | `npapi01.np.lab` | NodePilot API (Kestrel HTTPS:443), Postgres |
| Client | `npcli01.np.lab` | Edge/Chrome, test suite |
| NTLM alias | `npapi01-ntlm.np.lab` | **A record for the same IP, without SPN** |

Realm `NP.LAB`, NetBIOS `NPLAB`, BaseDn `DC=np,DC=lab`, UPN suffix `np.lab`.

The NTLM alias is the core of the negative test: same IP, no `HTTP/` SPN → SPNEGO falls back
deterministically to NTLM, TLS stays valid (alias as a second SAN in the
Kestrel certificate). Cleaner than access by IP address, because no
certificate warning interstitial gets in the way.

## Test directory

`LabIdentity.ps1` supplies the defaults for every provisioning script, probe, and database
check: prefix `nptest`, OU `NodePilot-SsoTest`, and groups `NPTest-Access` / `NPTest-Admins`.
Pass the same `-UserPrefix` to every command when using a custom prefix (1-12 letters,
digits, or hyphens). Pass matching OU/group overrides to the scripts that use them.
`Setup-ApiHost.ps1` derives its service bind DN from those inputs unless explicitly overridden.

| Account | Groups | Purpose |
|---|---|---|
| `svc-nptest-dir` | – | LDAPS service bind |
| `nptest.alice` | **only** `NPTest-Admins` (in `NPTest-Access`) | Happy path, transitive mapping → Admin |
| `nptest.carol` | `NPTest-Access` | Viewer default (no RoleMapping hit) |
| `nptest.bob` | none | AllowedGroup gate: 401, **no** JIT row |
| `nptest.dave` | `NPTest-Access` | Race drill (leave untouched until W18) |
| `nptest.erin` | `NPTest-Access` | Disable/revocation drills |

The nesting is essential: alice is **only** in `NPTest-Admins`. A successful
login therefore proves that NodePilot reads transitive `tokenGroups` rather than just `memberOf`.

## Scripts

| File | Host | Purpose |
|---|---|---|
| `Setup-LabDirectory.ps1` | dc01 | OU, groups (incl. nesting), users, UPNs — idempotent |
| `Get-LabSids.ps1` | dc01 | Read SIDs, `-AsEnvBlock` renders the PHASE B env block |
| `Setup-ApiHost.ps1` | npapi01 | Read-only preflight: domain join, time skew, DNS/CNAME, SPN situation, etypes, cert SAN, LDAPS bind, firewall |
| `Set-BrowserSsoPolicy.ps1` | npcli01 | `AuthServerAllowlist` + ZoneMap, `-Remove` to roll back |
| `ad-sso-live-test.ps1` | npcli01 | Main suite (W1–W25) |
| `Invoke-NtlmProbe.ps1` | npcli01 | Forced NTLM request (W19) |
| `Get-KerberosEvidence.ps1` | both | klist / Security 4624 / NTLM operational / SPN state → `evidence/` |
| `Assert-DbState.ps1` | npapi01 | DB evidence (identity, gate, race, revocation, NTLM, audit) |

## Procedure

### 1. Time synchronization (first!)

Kerberos tolerates ±5 min. A larger skew yields `KRB_AP_ERR_SKEW` and in the
failure pattern looks like "SPN wrong". **Measure, do not reconfigure across the board:**

```powershell
$cred = Import-CliXml C:\lab-cred\hyd.xml
$a = Invoke-Command -VMName <DC>  -Credential $cred -ScriptBlock { (Get-Date).ToUniversalTime() }
$b = Invoke-Command -VMName <API> -Credential $cred -ScriptBlock { (Get-Date).ToUniversalTime() }
"Skew: {0:N1}s (limit 300s)" -f ($b - $a).TotalSeconds
```

> The common recommendation to disable Hyper-V time synchronization in the
> guests does **not apply across the board**. Behind a private switch without an NTP uplink,
> the IC provider is often the *only* time source. In the measured lab the DC ran on
> stratum 6 via "VM IC Time Synchronization Provider", the skew between DC and
> API host was **0.3 seconds** — disabling it would have destroyed the synchronization
> instead of improving it.
>
> The time**zone** is irrelevant: Kerberos computes in UTC. In the lab the guests reported
> `Pacific Standard Time` with a local display in CEST, the UTC skew was still
> under a second. Never infer skew from the local time of day.

### 2. Directory on the DC

```powershell
.\Setup-LabDirectory.ps1
.\Get-LabSids.ps1                 # overview
.\Get-LabSids.ps1 -AsEnvBlock     # env block for PHASE B
```

The domain SID is new per provisioning — **never** hardcode SIDs.

### 3. LDAPS on the DC

Either AD CS as an enterprise root (the DC automatically pulls a
`Kerberos Authentication` certificate, LDAPS is then active without further action), or
self-signed with `CN=dc01.np.lab`, a matching SAN and EKU *Server Authentication* in
`LocalMachine\My` of the DC.

The root CA must be in `LocalMachine\Root` of the **API host**. There is no
in-app bypass — certificate validation is unconditional.

### 4. Preflight on npapi01 (elevated)

```powershell
.\Setup-ApiHost.ps1 -ApiFqdn npapi01.np.lab -NtlmAliasFqdn npapi01-ntlm.np.lab -DcFqdn dc01.np.lab
```

Every `FAIL` later produces a Kerberos failure pattern that looks like a product defect.
Do not continue without a green preflight.

**Service identity — two variants:**

*Variant A (recommended for the first run): LocalSystem / machine account.* On
domain join Windows registers `HOST/npapi01.np.lab`; `HOST` is mapped to
`http` via `sPNMappings`, so the KDC issues the ticket for `HTTP/npapi01.np.lab` **implicitly**.
No `setspn` needed — that eliminates the most common cause of failure.

*Variant B (mandatory follow-up run, because it is documented that way): gMSA.* An explicit SPN wins
over the implicit HOST alias:

```powershell
Add-KdsRootKey -EffectiveTime ((Get-Date).AddHours(-10))   # lab shortcut
New-ADServiceAccount svc-nodepilot -DNSHostName npapi01.np.lab `
    -PrincipalsAllowedToRetrieveManagedPassword 'NPAPI01$'
setspn -S HTTP/npapi01.np.lab NPLAB\svc-nodepilot$
setspn -Q HTTP/npapi01.np.lab   # must show EXACTLY one hit
```

`-S` instead of `-A` is not cosmetic: `-A` creates duplicates, and a duplicate SPN leads
to `KRB_AP_ERR_MODIFIED` or to a **silent** NTLM fallback.

Run the complete matrix under variant A, then repeat W5/W6/W19/W20 under variant B
— otherwise the documented production path stays unproven.

### 5. NTLM policy (GPO on the npapi01 OU)

*Computer Configuration → Windows Settings → Security Settings → Local Policies →
Security Options*

1. **Audit mode first:** *Restrict NTLM: Audit Incoming NTLM Traffic* =
   `Enable auditing for all accounts` → event **8004**.
2. **Enforce only after W19:** *Restrict NTLM: Incoming NTLM traffic* =
   `Deny all accounts` → event **4004**, registry
   `HKLM\SYSTEM\CurrentControlSet\Control\Lsa\MSV1_0\RestrictReceivingNTLMTraffic = 2`.

**The order is mandatory.** With the Deny GPO active, SSPI itself
(`AcceptSecurityContext`) already rejects NTLM — the application branch in `AuthController.WindowsLogin`
that audits `windows_ntlm_disabled` is then **unreachable**. W19 (app rejects) and
W20 (OS rejects) together prove point 4 of the field test matrix; neither alone does.

`Authentication:Windows:NtlmDisabledByPolicy=true` is an operator **attestation**,
not enforcement. Only step 2 proves it.

### 6. Browser policy on npcli01 (elevated)

```powershell
.\Set-BrowserSsoPolicy.ps1 -ApiFqdn npapi01.np.lab
```

Then restart Edge/Chrome **completely** and check `edge://policy` —
`AuthServerAllowlist` must appear as *Applied* (screenshot = evidence).

Deliberately **not** set: `AuthNegotiateDelegateAllowlist` (delegation is neither
needed nor wanted here) and `AuthSchemes` (the browser is the wrong place to
block NTLM — SPNEGO also carries NTLM under `negotiate`; step 5 is responsible for that).

### 7. PHASE A — API without SSO, bootstrap break-glass

The break-glass invariant aborts the boot if, with an active external provider, no
active local admin with a password and `IsBreakGlass=true` exists. With SSO active
the bootstrap is unreachable. **The order is mandatory.**

**First create the token directory correctly.** `RestrictedFileWriter` checks **every**
parent folder and refuses as soon as an untrusted principal has `Delete`/`CreateFiles` there.
An NTFS path alone is not enough: `%TEMP%` typically carries foreign ACEs, and a folder freshly
created under `C:\ProgramData` inherits `Write` for `BUILTIN\Benutzer`. Without this step the API does start,
but logs "could not be written with restrictive ACLs" and **the bootstrap is disabled**:

```powershell
$dir = 'C:\ProgramData\NodePilot-Labtest'
New-Item -ItemType Directory -Path $dir -Force | Out-Null
$me  = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
$acl = New-Object System.Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true, $false)   # inheritance off, take nothing over
$acl.SetOwner($me)
foreach ($sid in @($me,
                   (New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')),
                   (New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')))) {
  $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
    $sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
}
Set-Acl -Path $dir -AclObject $acl
```

```powershell
$env:ASPNETCORE_ENVIRONMENT        = 'Production'
$env:ConnectionStrings__Postgres   = '...Database=nodepilot_adssotest...'
$env:Security__AdminSetupTokenPath = 'C:\ProgramData\NodePilot-Labtest\admin-setup.token'
$env:Kestrel__Https__Enabled               = 'true'
$env:Kestrel__Https__HttpsPort             = '443'
$env:Kestrel__Https__CertificateThumbprint = '<Thumbprint>'
$env:Authentication__Ldap__Enabled    = 'false'
$env:Authentication__Windows__Enabled = 'false'
```

```powershell
$token = (Get-Content 'C:\ProgramData\NodePilot-Labtest\admin-setup.token' -Raw).Trim()
Invoke-RestMethod -Uri https://npapi01.np.lab/api/auth/login -Method Post `
  -ContentType 'application/json' -Headers @{ 'X-Setup-Token' = $token } `
  -Body (@{ username='breakglass.admin'; password='Boot#20260802!Adm1n' } | ConvertTo-Json)
```

If the service runs as LocalSystem/gMSA, the token file with an owner-only ACL is not directly
readable even for admins → read it with backup semantics:
`robocopy C:\ProgramData\NodePilot-Labtest $env:TEMP admin-setup.token /B`

### 8. PHASE B — restart the API with LDAPS + Windows SSO

Take over the env block from `Get-LabSids.ps1 -AsEnvBlock` and additionally keep the Kestrel variables
from phase A. Env vars win over `appsettings.runtime.json`
(deployment policy > UI) — the dev configuration stays untouched.

**Why LDAPS is mandatory despite "Windows SSO only":** The boot validator already runs for
`Ldap:Enabled || Windows:Enabled` and demands endpoints, `BaseDn`, service bind and
at least one `AllowedGroupSids`. At run time the controller performs an authoritative LDAPS
`LookupBySubjectAsync` on **every** Windows login — Kerberos PAC groups
are deliberately not trusted because they can be hours old.

`Ldap:Enabled=true` is needed for the run because W9 (identity equality) requires the
password path. W23 tests the Windows-only topology separately.

Leave `Cluster:Enabled` at `false` — otherwise `DirectorySynchronizationService` and
`ExternalAuthorizationStalenessService` do not run and the revocation drills measure nothing.
Check `/healthz/leader` → 200 beforehand.

### 9. Run the suite (npcli01, locally interactive as nptest.alice)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\ad-sso-live-test.ps1 `
    -IncludeNtlmProbe -IncludeRaceDrill -IncludeDisableDrill `
    -DbPassword '<postgres-pw>' -DbName nodepilot_adssotest
```

Drills separately, because they are long-running or disruptive:

For W16, prepare a long-running test workflow and give `nptest.erin` the Operator role
through a separate role-mapping group that does **not** grant admission or nest into
`NPTest-Access`. Erin must still depend on direct `NPTest-Access` membership for admission;
adding her to `NPTest-Admins` would invalidate this experiment. The default Viewer role
supports W15 only. A requested W16 fails if execution is rejected, lacks an ID, or never
reaches Running. Only an authentication denial (401/403) alongside a healthy independent
admin session counts as revocation. Network/server errors fail the drill. Group membership
is restored in `finally`, including failures after removal.

```powershell
.\ad-sso-live-test.ps1 -IncludeOutageDrill -DcVmName dc01 -HyperVHost <hv-host>
.\ad-sso-live-test.ps1 -IncludeRevocationDrill -RevocationWorkflowId <guid>   # 3-16 min
.\ad-sso-live-test.ps1 -IncludeRateLimitDrill                                 # throttles the IP for ~60 s
```

### 10. Secure the evidence

The database checks require evidence for the selected prefix. Revocation also requires
exactly one Erin user before asserting zero active sessions, so an absent identity cannot
produce a green result. Queries fail on database errors.

```powershell
.\Get-KerberosEvidence.ps1 -Role Client              # on npcli01
.\Get-KerberosEvidence.ps1 -Role Server              # on npapi01, elevated
.\Assert-DbState.ps1 -DbPassword '<pw>' -DbName nodepilot_adssotest
```

### 11. Clean up

```powershell
.\Set-BrowserSsoPolicy.ps1 -Remove                   # npcli01
# Remove variant C SPNs (if the API ran as a domain user in between):
setspn -D HTTP/npapi01.np.lab NPLAB\<account>
# Test DB: DROP DATABASE nodepilot_adssotest WITH (FORCE);
Remove-Item -Recurse -Force 'C:\ProgramData\NodePilot-Labtest'
```

## Test matrix

Automated in `ad-sso-live-test.ps1`:

| # | Scenario | Expectation |
|---|---|---|
| W1 | `/api/auth/methods` | `windows=true`, `windowsEndpoint=/api/auth/windows` |
| W2 | `/healthz/{live,ready,directory,leader}` | all 200, directory `Healthy` |
| W3 | Break-glass login despite active SSO | 200, Admin |
| W5 | **Happy path Kerberos** | 200, role **Admin** (only transitively!), `np_auth` httpOnly + `np_csrf`, **no** token in the body |
| W6a | `klist tickets` | Service ticket `HTTP/npapi01.np.lab` present |
| W7 | `/api/auth/me` with SSO cookie | 200, correct identity |
| W8 | JWT payload decoded | **no** group/SID claims (see below for the positive form) |
| W9 | LDAP login of the same person | same `userId`, exactly one `ExternalIdentity` |
| W10 | Second SSO login | same `userId` (JIT update, no duplicate) |
| W11 | carol | 200, role Viewer |
| W12 | bob | 401, **no** user row |
| W25 | SSO without `np_csrf` | 200 (cookie bootstrap exception), no 403 |
| W13 | `-IncludeDisableDrill` | disabled AD account → no login |
| W14 | `-IncludeOutageDrill` | DC gone → **503** fail-closed, break-glass stays 200, `/healthz/ready` stays 200 |
| W15/16 | `-IncludeRevocationDrill` | group removal → session revoked ≤ 15 min, execution stopped |
| W18 | `-IncludeRaceDrill` | 5 parallel first logins → exactly 1 user, 1 identity, 1 `USER_WINDOWS_JIT_CREATED` |
| W19 | `-IncludeNtlmProbe` | forced NTLM → 401, no `np_auth`, audit `windows_ntlm_disabled` |
| W22 | `-IncludeRateLimitDrill` | 429; measures how many tokens an SSO login costs |

### Manual test points

Not scriptable — evidence belongs in `evidence/`:

| # | Scenario | Procedure / evidence |
|---|---|---|
| W0 | Boot validator | Start PHASE B with `Authentication__Windows__AllowNtlmFallback=true` → startup abort ("Kerberos-only"). Reset afterwards |
| W4 | Break-glass invariant | Second DB with a user without `IsBreakGlass`, SSO on → startup abort before SSO traffic |
| W6b | **Browser SSO without prompt** | **Precondition:** browser policy set (`Set-BrowserSsoPolicy.ps1`), **all** browser processes terminated (`Get-Process msedge \| Stop-Process -Force`), `cmdkey /list` without an entry for the origin, no ESSO/credential filler installed. Then start Edge, `edge://policy` shows `AuthServerAllowlist` as *Applied*, open `/login`, click the SSO button. **Only a login with NO credential dialog at all passes** — a 200 alone is not enough. Additionally Fiddler: two legs, `401 WWW-Authenticate: Negotiate` → `200` with `Authorization: Negotiate YII...` (SPNEGO, **not** `TlRMTVNTUA==` = NTLMSSP). Evidence: `.saz`, `edge://policy` screenshot, dashboard screenshot |
| W6c | **Control test for W6b** | `Set-BrowserSsoPolicy.ps1 -Remove`, start **cold** again, click → the dialog **must** come back. Without this point, "no dialog" remains a chance finding: a warm cache, a saved password or a credential filler produce the same picture. Afterwards restore the policy |
| W17 | No false tombstoning | Point `BaseDn` at an empty OU, restart, wait for one sync cycle → log "all-not-found pass rejected", `IsTombstoned` count unchanged |
| W20 | NTLM negative (OS level) | Deny GPO active, repeat W19 → 401 from the handler, **no** app audit; event 4004 |
| W21 | Kerberos survives the GPO | W5 with the Deny GPO active → 200 |
| W23 | Windows-only topology | Restart with `Ldap__Enabled=false` → boot ok, SSO ok, `methods.ldap=false`, `/healthz/directory` still `Healthy` |
| W24 | Restart semantics | `Windows__Enabled=false` **without** restart → `methods` keeps reporting `windows=true` (process-start snapshot); after restart `windows=false` and endpoint 404 |
| W26 | Username collision | Create a local user `nptest.alice@np.lab` without an external identity, SSO with a different SID → 401, audit `USER_WINDOWS_REFUSED_COLLISION`, **no** auto-merge |
| W27 | SPN duplicate (destructive, last) | `setspn -A HTTP/npapi01.np.lab NPLAB\nptest.bob`, repeat W5 → Kerberos breaks; afterwards `setspn -D`. Purpose: document the failure pattern |

### Order dependencies

W0/W4 before PHASE B · W1–W12 in sequence · W13/W15/W16 only with `nptest.erin` (never with alice, otherwise
the admin is gone) · W18 before any other `nptest.dave` contact · **W19 strictly before W20**
(afterwards the app branch is dead) · W17/W23/W24 need restarts, put them at the end · W27 dead last.

## Pitfalls

| Symptom / risk | Cause & fix |
|---|---|
| Diffuse 401 despite a correct SPN | **HTTP/2.** The HTTP/1.1 pinning only takes effect with `Kestrel:Https:Enabled=true` **and** `Authentication:Windows:Enabled=true` (`KestrelHttpsConfigurator.cs:92/138`). `dotnet run --urls https://...` bypasses the path, ALPN negotiates h2. Either `Kestrel__Https__Enabled=true` or deliberately plain-text HTTP |
| `KRB_AP_ERR_SKEW`, looks like "SPN wrong" | Time skew > 5 min. **Measure the UTC skew** (step 1) instead of blindly disabling the IC time sync — behind a private switch it is often the only source |
| **gMSA/service account without SPN** ⇒ Kerberos fails silently | If the service runs under a gMSA or domain user, the `HOST/` SPN of the **computer account** does not cover it: the ticket is encrypted with the key of the computer account and unreadable for the service process. SPNEGO falls back to NTLM, the test wrongly reports "NTLM rejection works". Check with `setspn -L '<DOMAIN>\<gmsa>$'`, set with `setspn -S HTTP/<fqdn> '<DOMAIN>\<gmsa>$'` |
| TLS fails with "underlying connection was closed: An unexpected error occurred on a send" | **Not a TLS problem.** In a remote runspace (`Invoke-Command`) `ServerCertificateValidationCallback = { $true }` cannot run — "There is no Runspace available to run scripts in this thread" —, the callback throws and the connection breaks. Correct solution: actually trust the certificate on the client (`LocalMachine\Root`), do not bypass validation. Control test: `curl.exe` without `-k` |
| Suite aborts with "running scripts is disabled on this system" | Execution policy of the client. Start it via a child process: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File …`; the Kerberos tickets of the logon session are preserved |
| **Credential dialog when clicking the SSO button** | The origin is not in `AuthServerAllowlist` or not in the intranet zone. The browser then does **not** present the existing ticket automatically and prompts instead. Run `Set-BrowserSsoPolicy.ps1`, restart the browser **completely**, check `edge://policy` for *Applied* |
| **First click prompts, second does not** | The HTTP auth credential cache of the browser **session**. A NodePilot logout ends the application session, not this cache. This is **no** SSO evidence — only a cold start of all browser processes makes the test meaningful |
| **Silent despite a cold start, although no policy is set** | A persistent entry in the Windows Credential Manager ("Remember my credentials" in the dialog) or an ESSO/credential filler on the endpoint. Both fill in the password and **thereby mask the missing policy**. Check `cmdkey /list` at the console; the proof can only be made on a client without a fill tool |
| W19 reports 401, but the audit stays empty | Unanswered `Negotiate` challenge, no NTLM attempt. Use `-Mode Alias` with an SPN-free A record — see "False PASS" above |
| Access **from the API host itself** falls back to NTLM/401 | LSA loopback check. Always test from npcli01. Do **not** set `DisableLoopbackCheck` — it falsifies the result and is a security regression |
| `localhost` instead of FQDN | No `HTTP/localhost` SPN. For LDAP additionally `::1` resolution → `LdapException 81`. FQDNs everywhere |
| Kerberos breaks **silently** to NTLM | SPN duplicate or SPN on the wrong account. `setspn -S` (never `-A`), `setspn -X` before every run. Delete variant C SPNs after the rebuild |
| SPN does not match the called name | **CNAME canonicalization** — Chromium forms the SPN from the A record target. Use a direct A record in the lab |
| TLS warning or wrong SPN | Certificate name ≠ SPN host. One chain: URL host = SPN host = cert SAN; NTLM alias as a second SAN |
| "Bootstrap token … could not be written with restrictive ACLs" | **NTFS is not enough.** `RestrictedFileWriter` checks every ancestor for `Delete`/`CreateFiles` for untrusted principals. `%TEMP%` fails on foreign ACEs, a new `C:\ProgramData` folder on inherited `Write` for `BUILTIN\Benutzer`, an exFAT drive on missing ACLs (owner "Everyone"). Create the directory protected with the snippet from step 7. **The API starts anyway** — only the bootstrap is silently disabled |
| Bootstrap returns 401/503 although the token is correct | SSO was already active — PHASE A before PHASE B |
| `windows_ntlm_disabled` is missing in the audit | The Deny GPO is already active, SSPI rejects before the controller. Go back to audit mode for W19 |
| `-UseDefaultCredentials` returns 401 | **Double hop.** Via `Enter-PSSession`/WinRM the process has no delegatable TGT. Use a local interactive session |
| `LdapException 81` "server unavailable" despite a running DC | Also masks certificate problems: check DC SAN, CA trust in `LocalMachine\Root` of the API host, FQDN instead of IP |
| `/api/auth/methods` shows old values | `ActiveAuthenticationConfiguration` is a process-start snapshot. Restart after every `Authentication:*` change |
| `KDC_ERR_ETYPE_NOSUPP` | RC4-only account plus AES-enforcing policy. `Get-ADComputer NPAPI01 -Properties msDS-SupportedEncryptionTypes` |
| gMSA cannot be created | The KDS root key needs 10 h. In the lab `Add-KdsRootKey -EffectiveTime ((Get-Date).AddHours(-10))` |
| 429 earlier than expected | `UseRateLimiter()` sits **before** `UseAuthentication()` — the anonymous challenge leg also consumes a token. Effective budget ≈25 logins/min/IP instead of 50 |

### Expected JWT shape (W8)

The token carries the **long** claim URIs, not the short forms — verified against the dev API on
2026-08-02:

```
http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier
http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name
http://schemas.microsoft.com/ws/2008/06/identity/claims/role
jti, iat, np_iat_ms, np_session, np_secstamp, exp, iss, aud
```

The check is therefore deliberately a **negative** check (no `group`/`primarysid`/
`objectsid`/`sid` claim) and not a positive whitelist — the latter would be brittle against any change
to the `OutboundClaimTypeMap`. In URI form a group claim is called
`.../claims/groupsid`, so the pattern catches both spellings.

### Integrated Windows Authentication vs. credential filler (ESSO)

Both methods take the user to the destination without typing — they are still not the same thing,
and confusing them costs the entire test value.

- **IWA/Kerberos** (what NodePilot does): the browser presents a short-lived,
  service-bound ticket. **No password exists in the entire process.**
- **Credential filler** (Windows Credential Manager, ESSO products such as Imprivata,
  Evidian, NetIQ SecureLogin): a password is entered into the dialog — just not
  by the human. Legitimate and intended for applications that cannot do Kerberos; NodePilot
  does not need it.

What matters for the test: **the two cannot be distinguished server-side.** SSPI produces a
valid Kerberos AP-REQ in both cases, and the audit identically says `source: "Windows"`.
A filler therefore completely masks a missing browser policy. The proof can consequently
only be made client-side — on a client without a fill tool, with an empty vault
and after a cold start. That is exactly why the control test W6c exists.

## Diagnostic traps that are not a product defect

- **The UI swallows the cause of the error.** `LoginPage` maps 401 (no ticket), 401
  (NTLM), 401 (no group), 503 (DC gone), 404 (SSO off) and 429 to the **same**
  message. Never derive verdicts from the UI, always from HTTP status + audit line.
- **`windows_directory_unavailable` is ambiguous.** An LDAPS misconfiguration
  yields the same reason code as a real DC outage. For W14 `/healthz/directory` must
  have been `Healthy` beforehand, otherwise the 503 is not meaningful.
- **Collisions do not produce `LOGIN_FAILED`.** For username collision, identity conflict
  and tombstone the controller writes only a metric; the audit comes from
  `ExternalUserMapper` as `USER_WINDOWS_REFUSED_*`. SIEM rules that only
  watch `LOGIN_*` lose these cases.
- **Two independent clocks control revocation.** `DirectorySynchronizationService`
  (1–5 min, leader-only) reacts to group removal/disable;
  `ExternalAuthorizationStalenessService` holds the 15-minute ceiling and catches the
  DC outage. For the drills set `DirectorySyncIntervalMinutes=1`, but take the
  15-minute upper bound as the pass threshold.

## Limits

- **No HAProxy** — point 3 of the field test matrix (handshake through the production proxy,
  `http-reuse never`, connection-scoped Negotiate) stays open.
- **Only one DC** — multi-DC consensus (`ReconcileEndpointResults`: any endpoint failure
  ⇒ 503, **no** failover) cannot be exercised. Points 1 and 6 stay open, as does the
  design-vs-doc tension noted in `docs/ldap-windows-sso.md`.
- **No OIDC/SCIM** (points 8/9) and **no HA restart** (point 10).
- The browser handshake and the effectiveness of the GPO remain manual.
  `Invoke-WebRequest -UseDefaultCredentials` proves Kerberos on the HTTP stack, **not**
  the browser policy — both proofs are needed, one does not replace the other.
