<#
.SYNOPSIS
  Seeds 17 useful, DISABLED-by-default custom alerting rules into a running NodePilot instance.

.DESCRIPTION
  Custom alerting rules (Kind=Custom) fire on execution-scoped events and filter over the shared
  event-field catalog (NotificationContext.ToFieldMap). Unlike the system-alert policies
  (scripts/seed-system-alert-policies.ps1, which watch infra/health gauges), these react to what
  individual workflow runs actually do. They are created disabled with a single Email route so nothing
  fires until an operator reviews the target and enables them.

  They deliberately cover the common day-to-day questions an operator asks: "did anything fail on
  auth?", "did a child workflow blow up?", "did something time out / get killed by the system /
  fail over?", "is the queue backing up?", "is a run that still succeeds creeping slower?", "is a
  specific workflow flapping?", "did a scheduled/webhook run fail unattended?", "is it a
  permission/network problem?" — each exercising a different event type and filter field.

  Two rules use the flap mechanic (minOccurrences/occurrenceWindowMinutes): with the default dedup
  key ({ruleId}:{workflowId}:{eventType}) these fire only when the SAME workflow trips N times inside
  the window, i.e. "this workflow is flapping", not a single blip.

  Note on filters: contains/startsWith/endsWith are Ordinal (case-sensitive); 'matches' is a regex
  with inline (?i) for case-insensitivity — that's why error-text / trigger-source rules use 'matches'.

  Run against a local dev instance:
      pg_ctl start ... ; dotnet run --project src/NodePilot.Api --urls http://localhost:5000
      powershell -File scripts/seed-custom-alert-rules.ps1 -AlertEmail ops@example.com
#>
param(
  [string]$BaseUrl = 'http://localhost:5000',
  [string]$AlertEmail = 'test@gmail.com'
)

function Cond($field, $op, $val) {
  @{ type = 'comparison'; left = @{ kind = 'variable'; source = 'event'; name = $field }; op = $op; right = @{ kind = 'literal'; value = "$val" } } |
    ConvertTo-Json -Depth 6 -Compress
}
function UnaryCond($field, $op) {
  @{ type = 'comparison'; left = @{ kind = 'variable'; source = 'event'; name = $field }; op = $op } | ConvertTo-Json -Depth 6 -Compress
}

# name, description, eventTypes, filter (or $null), cooldownMinutes, minOccurrences, occurrenceWindowMinutes
$rules = @(
  # --- batch 1: outcome & lifecycle basics ---
  @{ name = 'Credential-/Login-Fehler';    desc = 'Ein Lauf scheiterte an Authentifizierung/Credential (falsches Passwort, gesperrtes Konto, WinRM-Login).';                events = @('CredentialFailure');    filter = $null;                                                    cd = 15; minOcc = 1; win = 0  },
  @{ name = 'Sub-Workflow fehlgeschlagen'; desc = 'Ein per startWorkflow/forEach aufgerufener Child-Workflow ist fehlgeschlagen — im Parent-Ergebnis oft nicht sichtbar.';    events = @('ExecutionFailed');      filter = (UnaryCond 'isSubWorkflow' 'isTrue');                      cd = 5;  minOcc = 1; win = 0  },
  @{ name = 'Timeout-Fehler';              desc = 'Ein Lauf schlug mit einer Timeout-Meldung fehl — haengende Remote-Operation oder zu knappes Zeitlimit.';                    events = @('ExecutionFailed');      filter = (Cond 'errorMessage' 'matches' '(?i)(timeout|timed out)'); cd = 10; minOcc = 1; win = 0  },
  @{ name = 'System-/Timeout-Abbruch';     desc = 'Ein Lauf wurde vom System abgebrochen (Timeout oder Dienst-Shutdown), nicht durch einen Benutzer.';                        events = @('ExecutionCancelled');   filter = (Cond 'cancelledBy' '==' 'system');                       cd = 0;  minOcc = 1; win = 0  },
  @{ name = 'HA-Failover-Abbruch';         desc = 'Nach einem HA-Leader-Wechsel wurden laufende Executions abgebrochen — Hinweis auf Failover/Neustart des aktiven Knotens.'; events = @('ExecutionCancelled');   filter = (Cond 'cancelledBy' '==' 'failover');                     cd = 0;  minOcc = 1; win = 0  },
  @{ name = 'Lauf staut in Warteschlange'; desc = 'Ein Lauf wartete ungewoehnlich lange auf den Start (ExecutionQueuedLong) — Kapazitaets-/Dispatch-Druck.';                   events = @('ExecutionQueuedLong');  filter = $null;                                                    cd = 10; minOcc = 1; win = 0  },
  @{ name = 'Sehr langer Erfolgs-Lauf';    desc = 'Ein Lauf war erfolgreich, dauerte aber ungewoehnlich lange (>30 min) — schleichende Performance-Regression.';               events = @('ExecutionSucceeded');   filter = (Cond 'durationMs' '>' 1800000);                          cd = 0;  minOcc = 1; win = 0  },

  # --- batch 2: scope, patterns, cancel-origin & flap ---
  @{ name = 'Fehler in Produktions-Ordner';    desc = 'Ein Lauf in einem Produktions-Ordner ist fehlgeschlagen (folderPath enthaelt "prod") — hoehere Prioritaet.';           events = @('ExecutionFailed');    filter = (Cond 'folderPath' 'matches' '(?i)prod');                 cd = 5;  minOcc = 1; win = 0  },
  @{ name = 'Wiederholter Fehler (Flap >=3/15min)'; desc = 'Derselbe Workflow schlug >=3x in 15 min fehl — Fail-Loop statt Einzelaussetzer.';                                    events = @('ExecutionFailed');    filter = $null;                                                    cd = 30; minOcc = 3; win = 15 },
  @{ name = 'Abbruch-Haeufung (>=3/15min)';    desc = 'Derselbe Workflow wurde >=3x in 15 min abgebrochen — etwas killt ihn wiederholt.';                                       events = @('ExecutionCancelled'); filter = $null;                                                    cd = 30; minOcc = 3; win = 15 },
  @{ name = 'cancel-all / Quarantaene';        desc = 'Ein Workflow wurde per cancel-all massenweise gestoppt (Quarantaene-Aktion).';                                           events = @('ExecutionCancelled'); filter = (Cond 'cancelledBy' '==' 'cancelAll');                    cd = 0;  minOcc = 1; win = 0  },
  @{ name = 'Reconciler-Abbruch nach Neustart'; desc = 'Beim Dienst-Start hat der Reconciler haengende Laeufe abgebrochen — Hinweis auf Restart/Crash-Recovery.';                events = @('ExecutionCancelled'); filter = (Cond 'cancelledBy' '==' 'reconciler');                   cd = 0;  minOcc = 1; win = 0  },
  @{ name = 'Remote-Step-Fehler (mit Maschine)'; desc = 'Ein fehlgeschlagener Lauf endete auf einer Zielmaschine (targetMachine gesetzt) — Remote-/WinRM-Problem, nicht engine-lokal.'; events = @('ExecutionFailed'); filter = (UnaryCond 'targetMachine' 'isNotEmpty');                 cd = 5;  minOcc = 1; win = 0  },
  @{ name = 'Fehler bei geplantem Lauf';       desc = 'Ein per Zeitplan (unbeaufsichtigt) gestarteter Lauf schlug fehl — niemand schaut zu.';                                   events = @('ExecutionFailed');    filter = (Cond 'triggeredBy' 'matches' '(?i)schedul');             cd = 5;  minOcc = 1; win = 0  },
  @{ name = 'Fehler bei Webhook-Integration';  desc = 'Ein per Webhook getriggerter Lauf schlug fehl — externe Integration hakt.';                                              events = @('ExecutionFailed');    filter = (Cond 'triggeredBy' 'matches' '(?i)webhook');             cd = 5;  minOcc = 1; win = 0  },
  @{ name = 'Zugriff verweigert / Permission'; desc = 'Ein Lauf scheiterte an fehlenden Rechten (Access denied / Unauthorized / Zugriff verweigert).';                          events = @('ExecutionFailed');    filter = (Cond 'errorMessage' 'matches' '(?i)(access is denied|unauthorized|permission denied|zugriff verweigert)'); cd = 10; minOcc = 1; win = 0 },
  @{ name = 'Netzwerk-/Verbindungsfehler';     desc = 'Ein Lauf scheiterte an Konnektivitaet (Connection refused / unreachable / cannot connect / WinRM).';                     events = @('ExecutionFailed');    filter = (Cond 'errorMessage' 'matches' '(?i)(connection refused|unreachable|no route|cannot connect|network path|winrm)'); cd = 10; minOcc = 1; win = 0 }
)

$created = 0
foreach ($r in $rules) {
  $body = @{
    name = $r.name; description = $r.desc; isEnabled = $false; eventTypes = $r.events;
    filterExpressionJson = $r.filter; scopeKind = 'Global';
    cooldownMinutes = $r.cd; minOccurrences = $r.minOcc; occurrenceWindowMinutes = $r.win;
    routes = @(@{ id = $null; channel = 'Email'; target = $AlertEmail; secret = $null; order = 0; conditionExpressionJson = $null });
    targets = @(); dedupKeyTemplate = $null
  } | ConvertTo-Json -Depth 8
  try {
    Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/alerting/rules" -ContentType 'application/json' -Body $body | Out-Null
    $flap = if ($r.minOcc -gt 1) { " >=$($r.minOcc)/$($r.win)min" } else { '' }
    Write-Host "  + $($r.name)  [$($r.events -join ',')$flap]"
    $created++
  } catch {
    Write-Warning "  ! $($r.name): $($_.Exception.Message)"
  }
}
Write-Host "Created $created/17 disabled custom alerting rules (route: $AlertEmail). Enable them under /alerts -> Custom rules."
