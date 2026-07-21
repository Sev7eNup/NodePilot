<#
.SYNOPSIS
  Seeds 14 useful, DISABLED-by-default system-alert policies (ADR 0008) into a running NodePilot instance.

.DESCRIPTION
  System-alert policies are catalog entries until configured; these are created disabled and route-less, so
  nothing fires until an operator adds a route and enables them. They deliberately exercise the new
  capabilities: several sources carry TWO policies (a Warning threshold and a Critical one), per-policy
  sustain windows, severity overrides, and a source parameter (cancel-rate window).

  Run against a local dev instance (localhost bypass grants in-process admin, so no token is needed):
      pg_ctl start ... ; dotnet run --project src/NodePilot.Api --urls http://localhost:5000
      powershell -File scripts/seed-system-alert-policies.ps1
#>
param([string]$BaseUrl = 'http://localhost:5000')

function Cond($field, $op, $val) {
  @{ type = 'comparison'; left = @{ kind = 'variable'; source = 'event'; name = $field }; op = $op; right = @{ kind = 'literal'; value = "$val" } } |
    ConvertTo-Json -Depth 6 -Compress
}
function UnaryCond($field, $op) {
  @{ type = 'comparison'; left = @{ kind = 'variable'; source = 'event'; name = $field }; op = $op } | ConvertTo-Json -Depth 6 -Compress
}

# name, description, sourceId, condition, sustainSeconds, severity, scope, sourceParameters
$policies = @(
  @{ name = 'Backlog erhöht';               desc = 'In-Flight-Executions (Pending+Running) dauerhaft hoch.';        sourceId = 'backlog';                     cond = (Cond 'depth' '>' 200);               sustain = 120; sev = 'Warning';  params = $null },
  @{ name = 'Backlog kritisch';             desc = 'Execution-Backlog explodiert — Kapazität prüfen.';             sourceId = 'backlog';                     cond = (Cond 'depth' '>' 1000);              sustain = 60;  sev = 'Critical'; params = $null },
  @{ name = 'Warteschlange staut';          desc = 'Nur wartende (Pending) Executions dauerhaft hoch.';           sourceId = 'pending';                     cond = (Cond 'pending' '>' 100);             sustain = 300; sev = 'Warning';  params = $null },
  @{ name = 'Abbruch-Sturm';                desc = 'Ungewöhnlich viele Abbrüche im Zeitfenster.';                 sourceId = 'cancel-rate';                 cond = (Cond 'cancels' '>' 20);              sustain = 0;   sev = 'Critical'; params = @{ windowMinutes = 10 } },
  @{ name = 'Maschine nicht erreichbar';    desc = 'Eine Managed Machine hat den letzten Connectivity-Check verfehlt.'; sourceId = 'machine-unreachable';    cond = (UnaryCond 'reachable' 'isFalse');    sustain = 0;   sev = 'Critical'; params = $null },
  @{ name = 'Dienst-Heartbeat veraltet';    desc = 'Ein Background-Service meldet seit >5min keinen Heartbeat.';   sourceId = 'service-stale';               cond = (Cond 'staleSeconds' '>' 300);        sustain = 0;   sev = 'Warning';  params = $null },
  @{ name = 'Credential läuft bald ab';     desc = 'Credential läuft in ≤14 Tagen ab — rechtzeitig erneuern.';    sourceId = 'credential-expiring';         cond = (Cond 'daysLeft' '<=' 14);            sustain = 0;   sev = 'Warning';  params = $null },
  @{ name = 'Credential abgelaufen';        desc = 'Credential ist bereits abgelaufen — Läufe werden scheitern.';  sourceId = 'credential-expiring';         cond = (UnaryCond 'expired' 'isTrue');       sustain = 0;   sev = 'Critical'; params = $null },
  @{ name = 'Workflow ohne Erfolg (24h)';   desc = 'Geplanter Workflow hatte seit 24h keinen erfolgreichen Lauf.'; sourceId = 'workflow-no-recent-success';  cond = (Cond 'minutesSinceSuccess' '>' 1440); sustain = 0; sev = 'Warning'; params = $null },
  @{ name = 'Langsame Ausführung';          desc = 'Eine Ausführung lief ungewöhnlich lange (>30min).';           sourceId = 'execution-result';            cond = (Cond 'durationMs' '>' 1800000);      sustain = 0;   sev = 'Warning';  params = $null },
  @{ name = 'Ausführung hängt';             desc = 'Ein Lauf ist seit >30min in Running ohne Abschluss.';         sourceId = 'execution-stuck';             cond = (Cond 'runningMinutes' '>' 30);       sustain = 0;   sev = 'Warning';  params = $null },
  @{ name = 'Workflow-Fehlerrate hoch';     desc = 'Fehlerrate eines Workflows im Fenster über 20 %.';            sourceId = 'workflow-health';             cond = (Cond 'failureRatePct' '>' 20);       sustain = 0;   sev = 'Warning';  params = $null },
  @{ name = 'Alarm-Zustellung defekt';      desc = 'Die Alerting-Pipeline selbst scheitert (SMTP/Webhook).';       sourceId = 'alert-delivery-failed';       cond = (Cond 'failures' '>' 3);              sustain = 0;   sev = 'Critical'; params = @{ windowMinutes = 15 } },
  @{ name = 'Zeitplan verpasst';            desc = 'Ein geplanter Lauf ist nicht rechtzeitig gestartet.';         sourceId = 'schedule-missed';             cond = (UnaryCond 'missed' 'isTrue');        sustain = 0;   sev = 'Warning';  params = $null }
)

$created = 0
foreach ($p in $policies) {
  $body = @{
    name = $p.name; description = $p.desc; isEnabled = $false; sourceId = $p.sourceId; presetId = $null;
    sourceParameters = $p.params; conditionJson = $p.cond; sustainForSeconds = $p.sustain;
    severityOverride = $p.sev; scopeKind = 'Global'; targets = @(); routes = @();
    cooldownMinutes = 0; minOccurrences = 1; occurrenceWindowMinutes = 0
  } | ConvertTo-Json -Depth 8
  try {
    Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/alerting/system/policies" -ContentType 'application/json' -Body $body | Out-Null
    Write-Host "  + $($p.name)  [$($p.sourceId), $($p.sev)]"
    $created++
  } catch {
    Write-Warning "  ! $($p.name): $($_.Exception.Message)"
  }
}
Write-Host "Created $created/14 disabled system-alert policies. Add a route and enable them under /alerts → System-Alarme."
