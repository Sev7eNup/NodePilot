# LDAP live test against a Samba AD DC (Docker)

Wire-level verification of the NodePilot LDAP path (`SystemLdapConnectionAdapter`,
LDAPS including strict certificate validation, simple bind, `tokenGroups`, JIT,
group gate, fail-closed) against a **real** AD directory — locally, without a
corporate AD. Closes the gap that all unit/controller tests stop at the
`ILdapConnectionAdapter` seam.

> **Validated on 2026-07-24:** 13/13 tests PASS + outage drill PASS
> (Samba 4.19/Ubuntu 24.04, Windows 11, Docker Desktop 29.x).
>
> **Does NOT replace** the real-AD field test required in `docs/ldap-windows-sso.md`:
> no Kerberos/Negotiate SSO, no multi-DC consensus, no HAProxy path, Samba ≠ MS AD.
>
> The Kerberos part is covered by the sister harness [`scripts/ad-sso-labtest/`](../ad-sso-labtest/README.md)
> (Hyper-V lab with a real Windows DC + domain-joined API host). Multi-DC and HAProxy
> remain open there as well.

## What the test covers

| # | Scenario | Expectation |
|---|---|---|
| 1 | `/api/auth/methods` discovery | `ldap=true` |
| 2 | `/healthz/directory` (service bind + BaseDn read over LDAPS) | `Healthy` |
| 3 | Local break-glass login despite active LDAP (local shadow) | 200 Admin |
| 4 | LDAP login `alice.demo` (UPN) — **only transitively** in the allowed group | 200, role **Admin** (proves `tokenGroups` nesting) |
| 5 | `/api/auth/me` with cookie session | Identity correct |
| 6 | Bare username + `UpnSuffix` normalization | 200, same UserId |
| 7 | Wrong password (real AD error 49) | 401 |
| 8 | Empty password (H-17 unauthenticated bind) | 401 |
| 9 | `carol.demo` without a RoleMapping hit | 200, role Viewer |
| 10 | `bob.demo` in no `AllowedGroupSids` | 401, **no** JIT row |
| 11 | Unknown directory user | 401 |
| 12 | Re-login = JIT update, no duplicate | same UserId |
| 13 | Admin settings "test LDAP" probe | ok=true |
| 14/15 | optional `-IncludeOutageDrill`: DC stop/start | 503 fail-closed → 200 recovery |

## Test directory (provisioned on first container start)

Realm `NODEPILOT.TEST`, BaseDn `DC=nodepilot,DC=test`, UPN suffix `nodepilot.test`.

| Account | Password | Groups | Purpose |
|---|---|---|---|
| `svc-nodepilot` | `Bind#20260724!Kq7z` | — | Service bind |
| `alice.demo` | `Login#20260724!Mv3p` | ONLY `NodePilot-Admins` (⊂ `NodePilot-Access`) | Happy path + nesting + admin mapping |
| `carol.demo` | `Login#20260724!Tw8r` | `NodePilot-Access` | Viewer default |
| `bob.demo` | `Login#20260724!Zh5c` | none | AllowedGroup gate |
| `Administrator` | `DcRoot#20260724!Aa` (env `ADMIN_PASS`) | — | Samba-internal only |

**Note:** The domain SID is **new per provisioning** — read the group SIDs again after every
fresh container (step 3). `ldap-live-test.ps1` reads the
access SID itself via `docker exec`.

## Procedure

Prerequisites: Docker Desktop (Linux containers), openssl (Git for Windows),
local dev Postgres (`C:\NodePilot-Postgres`), a one-time UAC confirmation for the
CA import.

### 1. Certificates + image + container

```powershell
cd scripts\ldap-testdc
.\make-certs.ps1                                  # Throwaway CA + server cert (SAN localhost/127.0.0.1, 30 days)
docker build -t nodepilot-testdc .
docker run -d --name nodepilot-testdc --hostname dc1 --cap-add SYS_ADMIN -p 636:636 nodepilot-testdc
# wait ~60 s, then check:
docker logs nodepilot-testdc | Select-String "directory setup done"
```

`--cap-add SYS_ADMIN` is mandatory (sysvol NT ACLs = `security.*` xattrs), otherwise
provisioning aborts with `NT_STATUS_ACCESS_DENIED`.

### 2. Trust the test CA (elevated, UAC prompt)

```powershell
Start-Process powershell -Verb RunAs -Wait -ArgumentList `
  "-NoProfile -Command Import-Certificate -FilePath '$PWD\certs\ca.crt' -CertStoreLocation Cert:\LocalMachine\Root"
```

The non-elevated import into `CurrentUser\Root` fails in non-interactive
shells ("user interface … not allowed") — hence the machine store via UAC.

### 3. Read the group SIDs

```powershell
docker exec nodepilot-testdc bash -c "for g in NodePilot-Access NodePilot-Admins; do ldbsearch -H /var/lib/samba/private/sam.ldb \"(sAMAccountName=`$g)\" objectSid | grep '^objectSid' | sed \"s/^/`$g /\"; done"
```

### 4. Create a throwaway DB

```powershell
# The password comes from the API's user secrets (id from NodePilot.Api.csproj):
$sec = Get-Content "$env:APPDATA\Microsoft\UserSecrets\37f6eb5a-82da-486c-ad19-f48b59916ab9\secrets.json" -Raw | ConvertFrom-Json
$cs  = if ($sec.PSObject.Properties['ConnectionStrings:Postgres']) { $sec.'ConnectionStrings:Postgres' } else { $sec.ConnectionStrings.Postgres }
if ($cs -match 'Password=([^;]+)') { $env:PGPASSWORD = $Matches[1] }
& 'C:\NodePilot-Postgres\pgsql\bin\psql.exe' -w -h 127.0.0.1 -U nodepilot -d nodepilot -c "CREATE DATABASE nodepilot_ldaptest;"
$env:PGPASSWORD = $null
```

(`psql -w` = never prompt for a password — otherwise it hangs in non-interactive shells.)

### 5. PHASE A — start the API **without** LDAP and bootstrap the break-glass admin

With LDAP active the bootstrap is **unreachable** (LDAP-first intercepts unknown
names → 401/503, AD users fail at the bootstrap gate). The order is mandatory.

```powershell
$env:ConnectionStrings__Postgres     = ($cs -replace 'Database=[^;]+','Database=nodepilot_ldaptest')
$env:ASPNETCORE_ENVIRONMENT          = 'Development'
$env:Security__AdminSetupTokenPath   = "$env:USERPROFILE\np-ldaptest-token\admin-setup.token"   # NTFS! (E: is exFAT -> writer refuses)
$env:Authentication__Ldap__Enabled   = 'false'
cd src\NodePilot.Api; dotnet run --no-launch-profile --urls http://localhost:5000
```

Then (second terminal):

```powershell
$token = (Get-Content "$env:USERPROFILE\np-ldaptest-token\admin-setup.token" -Raw).Trim()
Invoke-RestMethod -Uri http://localhost:5000/api/auth/login -Method Post -ContentType 'application/json' `
  -Headers @{ 'X-Setup-Token' = $token } `
  -Body (@{ username='breakglass.admin'; password='Boot#20260724!Adm1n' } | ConvertTo-Json)
```

### 6. PHASE B — restart the API **with** LDAP

Stop the API from phase A, then in addition to the phase A variables:

```powershell
$env:Authentication__Ldap__Enabled                          = 'true'
$env:Authentication__Ldap__Endpoints__0                     = '127.0.0.1:636'   # NEVER localhost (-> ::1, Docker IPv6 relay -> wldap32 error 81)
$env:Authentication__Ldap__BaseDn                           = 'DC=nodepilot,DC=test'
$env:Authentication__Ldap__UpnSuffix                        = 'nodepilot.test'
$env:Authentication__Ldap__ServiceBindDn                    = 'CN=svc-nodepilot,CN=Users,DC=nodepilot,DC=test'
$env:Authentication__Ldap__ServicePassword                  = 'Bind#20260724!Kq7z'
$env:Authentication__Ldap__AllowedGroupSids__0              = '<SID of NodePilot-Access from step 3>'
$env:Authentication__Ldap__GlobalRoleMappings__0__GroupSid  = '<SID of NodePilot-Admins from step 3>'
$env:Authentication__Ldap__GlobalRoleMappings__0__Role      = 'Admin'
cd src\NodePilot.Api; dotnet run --no-launch-profile --urls http://localhost:5000
```

Env vars win over `appsettings.runtime.json` (deployment policy > UI) —
the dev configuration stays untouched.

### 7. Run the suite

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\ldap-testdc\ldap-live-test.ps1 -IncludeOutageDrill
```

### 8. Clean up

```powershell
docker rm -f nodepilot-testdc
# Remove the CA (UAC):
Start-Process powershell -Verb RunAs -Wait -ArgumentList "-NoProfile -Command `"Get-ChildItem Cert:\LocalMachine\Root | Where-Object Subject -eq 'CN=NodePilot LDAP Test CA' | Remove-Item`""
# Test DB + token directory:
#   DROP DATABASE nodepilot_ldaptest WITH (FORCE);   (psql as in step 4)
Remove-Item -Recurse -Force "$env:USERPROFILE\np-ldaptest-token"
```

## Pitfalls (all hit live on 2026-07-24)

| Symptom | Cause / fix |
|---|---|
| Provision: `AD_DS_Attributes…ldf not found` | Package `samba-ad-provision` is missing (included in the Dockerfile) |
| Provision: `set_nt_acl … ACCESS_DENIED` | Container started without `--cap-add SYS_ADMIN` |
| Samba dies silently after start | Package `winbind` is missing (included in the Dockerfile) |
| Login/health: `LdapException ErrorCode=81 "server unavailable"` despite a running DC | `localhost` → `::1`; Docker's IPv6 relay does not forward → use **`127.0.0.1:636`**. Error 81 also masks certificate problems — on a corporate network use the DC **FQDN** |
| API log: "Bootstrap token … could not be written with restrictive ACLs" | Content root on exFAT (drive `E:`) — set `Security:AdminSetupTokenPath` to an NTFS path |
| Bootstrap returns 401/503 although the token is correct | LDAP was already active — keep the phase order (bootstrap first, then LDAP + restart) |
| Settings probe returns 403 | The `np_csrf` cookie value is URL-encoded — decode it before the `X-CSRF-Token` header |
| Settings probe returns 400 | Body needs the wrapper `{ "settings": { … } }` |
| `psql` hangs forever | Console password prompt — always use `-w` + `PGPASSWORD` |

## Limits

- **No Kerberos/Windows SSO** — the Negotiate handshake needs a domain join; stays reserved for the
  real-AD field test.
- **Only 1 endpoint** — the multi-DC consensus behavior (`ReconcileEndpointResults`:
  any endpoint failure ⇒ 503) is deliberately not exercised here.
- Samba behaves like AD on the protocol side (`objectSid`/`objectGUID`/`tokenGroups`/
  error 49), but is not a Microsoft DC.
