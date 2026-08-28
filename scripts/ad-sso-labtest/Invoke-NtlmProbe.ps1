# Forces an NTLM request against POST /api/auth/windows and checks that NodePilot rejects
# it (W19, application level). RUN ON: npcli01.
#
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File .\Invoke-NtlmProbe.ps1
#        .\Invoke-NtlmProbe.ps1 -Mode Alias      # via the SPN-less DNS alias
#
# Order in the field test:
#   W19 runs in GPO audit mode (NTLM still allowed). Only then does the request reach the
#   application branch in AuthController.WindowsLogin that rejects
#   Identity.AuthenticationType == "NTLM" and writes the audit reason
#   'windows_ntlm_disabled'.
#   Once the GPO is set to "Deny all accounts" (W20), SSPI rejects first and the client
#   sees a bare 401 from the Negotiate handler without an application audit entry.
#   Both passes together prove point 4 of the field test matrix; one alone does not.
#
# klist purge alone is not enough: the client fetches a new TGT right away.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '',
    Justification = 'Wegwerf-Lab-Credentials. Der Probe-Zweck verlangt ein NetworkCredential mit explizitem Paketnamen NTLM -- das nimmt ohnehin Klartext entgegen.')]
param(
    [string]$Base = 'https://npapi01.np.lab',
    [string]$NtlmAliasFqdn = 'npapi01-ntlm.np.lab',
    [string]$NetbiosDomain = 'NPLAB',
    [string]$SamAccountName = 'np.alice',
    [string]$Password = 'Lab#20260802!Kq7z',
    [ValidateSet('CredentialCache', 'Alias')]
    [string]$Mode = 'CredentialCache',
    [switch]$PassThruResult
)
$ErrorActionPreference = 'Stop'

# In alias mode the name points at the same IP but carries no HTTP/ SPN. The KDC answers
# KDC_ERR_S_PRINCIPAL_UNKNOWN and SPNEGO falls back to NTLM. The second SAN in the Kestrel
# certificate prevents a TLS interstitial.
$targetBase = if ($Mode -eq 'Alias') { ([Uri]$Base).Scheme + '://' + $NtlmAliasFqdn } else { $Base }
$url = "$targetBase/api/auth/windows"

Add-Type -AssemblyName System.Net.Http

$handler = New-Object System.Net.Http.HttpClientHandler
$handler.AllowAutoRedirect = $false
$handler.UseCookies = $true

if ($Mode -eq 'CredentialCache') {
    # A CredentialCache with the explicit package name "NTLM" pins the auth package, which
    # is more deterministic than going through a missing SPN.
    $cache = New-Object System.Net.CredentialCache
    $cache.Add([Uri]$targetBase, 'NTLM',
        (New-Object System.Net.NetworkCredential($SamAccountName, $Password, $NetbiosDomain)))
    $handler.Credentials = $cache
} else {
    $handler.Credentials = New-Object System.Net.NetworkCredential($SamAccountName, $Password, $NetbiosDomain)
}

$client = New-Object System.Net.Http.HttpClient($handler)
$client.Timeout = [TimeSpan]::FromSeconds(30)
$content = New-Object System.Net.Http.StringContent('{}', [Text.Encoding]::UTF8, 'application/json')

$status = -1
$body = ''
$setAuthCookie = $false
$probeFailed = $false
try {
    $resp = $client.PostAsync($url, $content).Result
    $status = [int]$resp.StatusCode
    $body = $resp.Content.ReadAsStringAsync().Result

    # Contains + GetValues instead of TryGetValues([ref]): the out parameter expects
    # IEnumerable<string>, and a PowerShell [ref] on @() is Object[], so the conversion
    # throws. Such a throw would be caught below and the cookie check would never run.
    if ($resp.Headers.Contains('Set-Cookie')) {
        $cookies = @($resp.Headers.GetValues('Set-Cookie'))
        $setAuthCookie = @($cookies | Where-Object { $_ -like 'np_auth=*' -and $_ -notlike 'np_auth=;*' }).Count -gt 0
    }
} catch {
    $probeFailed = $true
    $body = "EXCEPTION: $($_.Exception.GetBaseException().Message)"
} finally {
    $client.Dispose()
    $handler.Dispose()
}

# A bare 401 proves nothing.
#
# The Negotiate handler challenges with "WWW-Authenticate: Negotiate". A CredentialCache
# registered only for the package "NTLM" finds no entry for that and sends no Authorization
# header at all, so the server never sees an NTLM attempt and answers with an empty 401.
#
# Only the message from AuthController.WindowsLogin is proof. A 401 without that text is
# inconclusive and counts as a failure, not a success.
$appRejected = $body -match 'NTLM fallback is disabled'

$result = [pscustomobject]@{
    Mode          = $Mode
    Url           = $url
    Status        = $status
    Body          = ($body -replace '\s+', ' ').Trim()
    SetAuthCookie = $setAuthCookie
    # Without this flag a crash during the measurement would be indistinguishable from a
    # genuine "no cookie set".
    ProbeFailed   = $probeFailed
    # True only when the application actively rejected the NTLM attempt.
    AppRejected   = $appRejected
}

if ($PassThruResult) { return $result }

""
"================ NTLM-PROBE ================"
"Modus            : $($result.Mode)"
"URL              : $($result.Url)"
"HTTP-Status      : $($result.Status)   (erwartet: 401)"
"np_auth gesetzt  : $($result.SetAuthCookie)   (erwartet: False)"
"Messung gelaufen : $(-not $result.ProbeFailed)   (erwartet: True)"
"App hat abgelehnt: $($result.AppRejected)   (erwartet: True)"
"Body             : $($result.Body)"
""
if ($result.Status -eq 401 -and -not $result.SetAuthCookie -and -not $result.ProbeFailed -and $result.AppRejected) {
    "PASS -- NTLM wurde abgelehnt, keine Session entstanden."
    ""
    'Evidenz nachziehen:'
    '  * Audit-Zeile (nur im GPO-Auditmodus vorhanden -- bei "Deny all accounts"'
    '    lehnt SSPI vorher ab, dann gibt es keinen Applikations-Audit):'
    '      Assert-DbState.ps1 -Scenario Ntlm'
    '  * Eventlog: Get-WinEvent -LogName "Microsoft-Windows-NTLM/Operational" -MaxEvents 20'
    '    Event 8004 = eingehendes NTLM protokolliert, Event 4004 = blockiert.'
} elseif ($result.ProbeFailed) {
    "FAIL -- die Probe ist abgebrochen, das Ergebnis ist keine Messung. Siehe Body."
    exit 1
} elseif ($result.Status -eq 401 -and -not $result.AppRejected) {
    "UNSCHLUESSIG -- 401, aber ohne die Ablehnungsmeldung der Anwendung."
    ""
    "Der Server hat wahrscheinlich gar keinen NTLM-Versuch gesehen: er challenged mit"
    "'WWW-Authenticate: Negotiate', und ein CredentialCache nur fuer 'NTLM' antwortet"
    "darauf nicht. Das 401 ist dann eine unbeantwortete Challenge, kein Testergebnis."
    ""
    "Loesung: -Mode Alias verwenden. Dafuer im DNS einen A-Record auf dieselbe IP anlegen,"
    "fuer den KEIN HTTP-SPN existiert (z.B. cm1-ntlm.<domain>), und diesen Namen als"
    "zweite SAN ins Kestrel-Zertifikat aufnehmen. Dann scheitert die SPN-Suche, SPNEGO"
    "faellt auf NTLM zurueck und die Anwendung kommt ueberhaupt erst zum Zug."
    exit 1
} else {
    "FAIL -- erwartet war 401 ohne np_auth-Cookie."
    exit 1
}
