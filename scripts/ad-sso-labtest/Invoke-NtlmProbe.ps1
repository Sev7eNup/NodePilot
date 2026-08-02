# Erzwingt einen NTLM-Request gegen POST /api/auth/windows und prueft, dass NodePilot ihn
# ablehnt (W19, App-Ebene). AUSFUEHREN AUF: npcli01.
#
# Aufruf: powershell -NoProfile -ExecutionPolicy Bypass -File .\Invoke-NtlmProbe.ps1
#         .\Invoke-NtlmProbe.ps1 -Mode Alias      # ueber den SPN-freien DNS-Alias
#
# WICHTIG -- Reihenfolge im Feldtest:
#   W19 laeuft im GPO-AUDITMODUS (NTLM noch erlaubt). Nur dann erreicht der Request
#   ueberhaupt den Applikationszweig in AuthController.WindowsLogin, der
#   Identity.AuthenticationType == "NTLM" ablehnt und den Audit-Reason
#   'windows_ntlm_disabled' schreibt.
#   Sobald die GPO auf "Deny all accounts" steht (W20), weist bereits SSPI ab -- der
#   Client sieht dann ein nacktes 401 aus dem Negotiate-Handler OHNE Applikations-Audit.
#   Beide Paesse zusammen belegen Punkt 4 der Feldtest-Matrix; einer allein nicht.
#
# klist purge allein reicht NICHT: der Client holt sofort ein neues TGT.
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

# Beim Alias-Modus zeigt der Name auf dieselbe IP, traegt aber keinen HTTP/-SPN. Der KDC
# antwortet mit KDC_ERR_S_PRINCIPAL_UNKNOWN, SPNEGO faellt auf NTLM zurueck. Die zweite
# SAN im Kestrel-Zertifikat verhindert dabei ein TLS-Interstitial.
$targetBase = if ($Mode -eq 'Alias') { ([Uri]$Base).Scheme + '://' + $NtlmAliasFqdn } else { $Base }
$url = "$targetBase/api/auth/windows"

Add-Type -AssemblyName System.Net.Http

$handler = New-Object System.Net.Http.HttpClientHandler
$handler.AllowAutoRedirect = $false
$handler.UseCookies = $true

if ($Mode -eq 'CredentialCache') {
    # CredentialCache mit explizitem Paketnamen "NTLM" bindet das Auth-Paket hart --
    # deterministischer als der Umweg ueber einen fehlenden SPN.
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

    # Contains + GetValues statt TryGetValues([ref]): der out-Parameter erwartet
    # IEnumerable<string>, eine PowerShell-[ref] auf @() ist Object[] und die Konvertierung
    # wirft. Frueher landete dieser Wurf im catch, ueberschrieb den Body und liess
    # $setAuthCookie auf $false stehen -- die Probe meldete dann PASS ("keine Session"),
    # obwohl die Cookie-Pruefung nie gelaufen war. Ein gruener Test ohne Messung.
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

# Entscheidend: ein nacktes 401 beweist NICHTS.
#
# Der Negotiate-Handler challenged mit "WWW-Authenticate: Negotiate". Ein CredentialCache,
# der nur fuer das Paket "NTLM" registriert ist, findet dafuer keinen Eintrag und schickt
# ueberhaupt keinen Authorization-Header -- der Server sieht nie einen NTLM-Versuch und
# antwortet mit einem leeren 401. Genau das ist am 2026-08-02 im Lab passiert: die Probe
# meldete PASS, im Audit stand aber keine einzige windows_ntlm_disabled-Zeile.
#
# Belegend ist deshalb nur die Meldung aus AuthController.WindowsLogin. Ein 401 ohne
# diesen Text ist UNSCHLUESSIG und wird als Fehlschlag gewertet, nicht als Erfolg.
$appRejected = $body -match 'NTLM fallback is disabled'

$result = [pscustomobject]@{
    Mode          = $Mode
    Url           = $url
    Status        = $status
    Body          = ($body -replace '\s+', ' ').Trim()
    SetAuthCookie = $setAuthCookie
    # Ohne dieses Flag waere ein Absturz mitten in der Messung von einem echten
    # "kein Cookie gesetzt" nicht zu unterscheiden.
    ProbeFailed   = $probeFailed
    # True nur, wenn die Anwendung den NTLM-Versuch aktiv abgelehnt hat.
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
