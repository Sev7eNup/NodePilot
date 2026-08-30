# NodePilot Test-Suite

46 generierte Workflows unter [`scripts/test-suite/`](../scripts/test-suite/), die jede
Activity-Variante, jeden Trigger, jeden Edge-Operator und jedes Retry-Backoff **im Takt gegen
die laufende Engine** ausführen und ihr Ergebnis prüfen. Live-Installation im Ordner
`/Test_Workflows`.

Der Unterschied zu den drei Vorgänger-Generationen (`scripts/test-runbooks/`,
`scripts/muster-*.json`, `scripts/continuous-test-1min/`, alle abgelöst): die Suite ist
**generiert**, sie ist **selbstprüfend**, und ein Guard-Test in CI beweist, dass jede
behauptete Abdeckung auch wirklich erreichbar und geprüft ist.

## Zwei Verträge

Die Engine kennt keinen „behandelten Fehler": `WorkflowEngine` zählt am Ende alle
fehlgeschlagenen Steps und setzt den Lauf bei jedem Treffer auf `Failed` — eine
`.failed`-Kante routet weiter, rettet den Status aber nicht. Ein absichtlicher Fehler kann
deshalb nicht in einem Workflow stehen, der grün bleiben soll.

| Vertrag | Namensraum | Erwarteter Status | Wer urteilt |
|---|---|---|---|
| **positiv** | `[TestSuite] <typ>` | `Succeeded` | der Workflow selbst: ein `assert`-Knoten wirft bei Abweichung |
| **negativ** | `[TestSuite-Neg] <bereich>` | `Failed`, mit **genau** den deklarierten Fehl-Steps | `Verify-TestSuite.ps1` gegen das Manifest |
| **invasiv** | `[TestSuite-Inv] <typ>` | `Succeeded` | wie positiv, aber nur auf Hosts, die es freigeschaltet haben |

Der Verifier prüft beim Negativ-Vertrag die **Menge der fehlgeschlagenen StepIds**, nicht nur
den Status. Ein Lauf, der aus einem anderen Grund scheitert, gilt als Defekt der Suite.

## `suite-manifest.json` ist die Abdeckungsquelle

Nicht die Workflow-JSONs und nicht diese Datei, sondern
[`scripts/test-suite/suite-manifest.json`](../scripts/test-suite/suite-manifest.json).
Es ist Eingabe des Guard-Tests **und** des Verifiers. Jeder Fall nennt Dimension, Wert,
Profil, erwarteten Ausgang, Workflow, Knoten und wer ihn prüft.

**Vier Profile:**

| Profil | Bedeutung |
|---|---|
| `continuous` | läuft überall, keine externe Voraussetzung |
| `integration` | braucht ein zweites System (SMTP-Senke, Datenbank, LLM-Endpunkt, Proxy) oder Produktions-Härtung; fehlt es, wird der Workflow **installiert aber deaktiviert** statt rot |
| `invasive` | verändert den Host (Dienst anlegen, Task registrieren); opt-in über `NP_TESTSUITE_INVASIVE` |
| `excluded` | läuft nie — jeder Eintrag trägt `reason` und, wo vorhanden, `coveredBy` auf den Unit-Test, der stattdessen abdeckt |

Bewusst ausgeschlossen sind unter anderem: `delay > 86400` (klemmt auf 24 h und würde einen
Runner-Slot blockieren), vier der fünf `powerManagement`-Aktionen (nehmen den Host herunter),
`scheduledTask runLevel: highest` (Ergebnis hängt vom Privilegienniveau ab, also nicht
deterministisch), `runScript engine=pwsh` (PowerShell 7 ist nicht garantiert),
`restApi`-Redirects und die 16-MiB-Kappung.

## Konventionen

| Aspekt | Regel |
|---|---|
| **Generiert** | Quelle sind die `spec_*.py`-Module; die JSONs sind Artefakte. Eine Handänderung wird beim nächsten `build_suite.py` überschrieben. |
| **Naming** | `[TestSuite] <typ>` / `[TestSuite-Neg] <bereich>` / `[TestSuite-Inv] <typ>` — Sortier-Anker und Filter-Hook. |
| **Folder** | `/Test_Workflows`. |
| **Target Machine** | `targetMachineId: "localhost"` — In-Proc-Bypass, keine Credentials, kein WinRM. |
| **Assertion** | `Varianten → cleanup → assert(runScript) → returnData`. Der `assert`-Knoten ist Graph-Nachfahre aller Varianten und liest deren Ergebnisse direkt; `runScript`-Erfolg ist fehlerbasiert, ein `throw` macht den Lauf rot. |
| **Cleanup vor Assertion** | damit eine rote Assertion keine Reste hinterlässt. Unbedenklich, weil die Assertion den Databus liest, nicht die Platte. |
| **Eigenes `outputVariable`** | pro Variantenknoten; sonst kollidieren gleichnamige Params im Junction-Merge. |
| **Nur Ahnen-Referenzen** | ein Verweis auf einen Parallelzweig löst nie auf und ist bei `runScript` ein harter Fehler. |
| **Parallelität** | `MaxConcurrentExecutions = 1` je Workflow, gesetzt über `PUT /{id}/concurrency-limit`. |

### Sandbox

| Ressource | Regel |
|---|---|
| Lauf-Sandbox | `C:\Temp\NP-TestSuite\runs\<cid>\`, `HKCU:\SOFTWARE\NP-TestSuite\<cid>`, Dienst `NPTestSvc_<cid8>`, Task `\NodePilot-TestSuite\<cid8>` — am Ende jedes Laufs weg |
| **Dauer-Fixtures** | `C:\Temp\NP-TestSuite\runtime\{watch,db,acks}\` — **vom Cleanup ausgenommen**. Der File-Watcher-Pfad und die Sentinel-Datenbank müssen den laufenden Trigger-Quellen erhalten bleiben. |
| Janitor | erster Knoten jedes Laufs räumt Reste älter als 1 h, verwaiste `NPTestSvc_*` und Tasks unter `\NodePilot-TestSuite\` |
| Residuen-Prüfung | als `runScript`-Knoten **im API-Prozess** — das `HKCU` des Dienstkontos ist nicht das des Administrators |

Der Korrelations-ID-Ansatz (`generateText mode=guid` als zweiter Knoten) ist der Grund, warum
zwei überlappende Läufe nie auf demselben Namen kollidieren.

## Trigger: das Ack-Protokoll

Die fünf passiven Trigger haben keinen eigenen Takt und lassen sich auch nicht sinnvoll von
Hand starten — ein manuell gestarteter Lauf trägt keinen einzigen der Trigger-Parameter, die
der Workflow beweisen soll. Ein authentifizierter Rückruf in die API scheidet aus:
`/api/executions` verlangt `[Authorize]`, ein `restApi`-Step hat keine dauerhafte Session, und
ein Admin-Token gehört nicht in Workflow-JSON.

Stattdessen Korrelations-ID plus Quittungsdateien:

```
[TestSuite] trigger drivers   (alle 10 Min)
  └─ cid erzeugen
  └─ Datei runtime/watch/<cid>.txt schreiben        → fileWatcherTrigger
  └─ Sentinel in runtime/db/sentinel.sqlite auf <cid> setzen → databaseTrigger
  └─ POST auf /api/webhooks/<id>/suite mit {"cid":…} → webhookTrigger
  └─ startWorkflow auf den manual-Trigger-Workflow   → manualTrigger
  └─ 60 s warten
  └─ runtime/acks/<typ>/<cid> einsammeln
```

Jeder ausgelöste Workflow liest seine Korrelations-ID aus den eigenen Trigger-Parametern und
legt die Quittung ab. Beim `databaseTrigger` **ist** die ID der Sentinel-Wert — die Quelle
reicht nichts anderes weiter.

**Eine verpasste Runde wird toleriert.** `DatabaseTriggerSource` re-baselined die erste
Beobachtung nach jedem Start bewusst ohne zu feuern; nach einem API-Neustart fehlt deshalb
genau eine Quittung. Erst die zweite Runde in Folge lässt den Driver werfen (Zähler in
`runtime/acks/consecutive-misses.txt`).

Das Webhook-Secret liegt **nicht** im Repo: die generierte Datei trägt einen Platzhalter, den
`Install-TestSuite.ps1` beim Installieren durch den Wert der globalen Variablen
`NP_TESTSUITE_WEBHOOK_SECRET` ersetzt (beim ersten Lauf aus einem CSPRNG erzeugt).

## Takt

Ein `scheduleTrigger` pro Workflow, 7-Feld-Quartz-Cron, Minuten-Offsets gestaffelt.

| Stufe | Cron | Inhalt |
|---|---|---|
| A — 5 Min | `0 0/5` … `0 4/5 * * * ? *` | leichte Engine-Typen, Dateisystem, ControlFlow, Edge-Conditions, Variable-Resolution |
| B — 15 Min | `0 5/15`, `0 10/15 * * * ? *` | zipOperation, sql, powerManagement, die vier Negativ-Workflows |
| C — 30 Min | `0 7/30`, `0 22/30 * * * ? *` | integration und invasive |
| D — 10 Min | `0 3/10 * * * ? *` | trigger drivers |

`build_suite.py` bricht ab, wenn ein Workflow mehr als die **Hälfte** seines Intervalls
veranschlagt — darüber staut sich der nächste Lauf hinter `MaxConcurrentExecutions=1` als
`DeferredByConcurrencyLimit` auf, statt laut zu scheitern.

## Betrieb

```powershell
# Generieren (nach jeder Spec-Änderung)
python scripts/test-suite/build_suite.py

# Installieren - idempotent, legt fehlende Globals an
./scripts/test-suite/Install-TestSuite.ps1 -Password '<admin>'
./scripts/test-suite/Install-TestSuite.ps1 -Password '<admin>' -Profiles continuous,integration,invasive

# Urteilen
./scripts/test-suite/Verify-TestSuite.ps1 -Password '<admin>' -Once            # jetzt starten
./scripts/test-suite/Verify-TestSuite.ps1 -Password '<admin>' -WindowMinutes 60  # Abnahme

# Einzelner Workflow als Assertion
np workflow run '[TestSuite] textFileEdit' --wait     # Exit 0 = alle Assertions gehalten
```

**Installer-Reihenfolge** (die drei Fallen, die alle Vorgänger getroffen haben):

1. Ein per `POST /api/workflows` erzeugter Workflow ist **bereits vom Ersteller ausgecheckt** —
   ein folgendes `/lock` liefert 409. Neu → `POST` + `/publish`; vorhanden → `/lock` + `/publish`.
2. `00-child-echo` muss **vor** seinen Eltern existieren und **enabled** sein: `forEach` und
   `startWorkflow` lösen es by-name auf und scheitern an einem deaktivierten Kind.
3. `MaxConcurrentExecutions` steht nicht im Publish-Body und braucht seinen eigenen Endpunkt.

Der Import-Endpunkt ist bewusst **nicht** der Installationsweg: er kennt kein Update-in-place
und erzeugt bei Namenskollision `(Imported 2)`.

`-RemoveLegacy` entfernt die abgelösten `Test — *`, `[Dauertest 1m] *` und `Muster — *`
Workflows aus der Instanz. Nie automatisch.

## Guard-Test

[`TestSuiteCoverageTests`](../tests/NodePilot.Engine.Tests/Activities/TestSuiteCoverageTests.cs)
in `NodePilot.Engine.Tests`. Er prüft mehr als „der Wert kommt irgendwo vor" — genau daran ist
die alte Suite zerfallen:

1. jeder Typ aus `activity-config-reference.json` hat mindestens einen Fall;
2. jeder Fall zeigt auf einen Knoten, der existiert, **nicht deaktiviert** ist und **von einem
   aktiven Trigger erreichbar** ist (echter Graph-Walk, keine deaktivierte Kante unterwegs);
3. der `assert`-Knoten referenziert wirklich die `outputVariable` des Falls — oder die eines
   ausdrücklich benannten Zeugen (`assertedVia`), etwa das `exists` nach einem `create`;
4. ausgeschlossene Fälle tragen einen Grund, und ein angegebenes `coveredBy` existiert;
5. genau ein Trigger je Workflow, Cron aus der Stufenliste, Laufzeitbudget eingehalten,
   Namensschema, keine hängenden Kanten, Fan-in nur auf `junction`, Positionen auf dem 20-px-Raster.

Fälle, die ausdrücklich `Skipped` erwarten (deaktivierte Kante, deaktivierter Knoten, nicht
erfüllte `.failed`-Bedingung), sind von Punkt 2 ausgenommen — dort ist die Unerreichbarkeit
genau das Prüfziel.

## Beim Bauen der Suite gefunden und behoben

Die Suite hat zwei Engine-Defekte zutage gefördert, beide im PowerShell-Wrapper:

- **`$LASTEXITCODE` überlebte die Wiederverwendung eines Pool-Runspaces.** Gemessen an
  `[TestSuite] runScript`: `v5` und `v6` führen kein natives Kommando aus und meldeten trotzdem
  `exitCode: 3` aus `v4`s `cmd /c exit 3` — quer über Executions hinweg sogar in einem anderen
  Workflow. `successExitCodes` bewertete damit einen fremden Wert.
- **Injizierte Upstream-Parameter kamen als eigene Outputs wieder heraus** und schaukelten sich
  entlang der Kette auf (`v4` speicherte 24 Keys, drei davon aus dem Step selbst).

Beides ist gefixt: der Wrapper setzt `$LASTEXITCODE` und `$Error` vor dem Skript zurück und
trennt Injektion und User-Skript in zwei geschachtelte Scopes, sodass `Get-Variable -Scope Local`
genau das Zugewiesene liefert. Gehütet von `WrapperScopeAndExitCodeTests` und
`PowerShellReservedVariablesParityTests`.

## Bekannte Grenzen

- **`integration`- und `invasive`-Profile werden auf einer Dev-Instanz nie ausgeführt.**
  `appsettings.Development.json` lockert `RejectTraversal`, `DisallowShellExecute`,
  `SqlActivity:RequireConnectionRef` und `Trigger:Database:RequireConnectionRef` bewusst — die
  zugehörigen Ablehnungen sind auf einem Dev-Host nicht beobachtbar und liegen deshalb in
  `[TestSuite-Neg] production guards`.
- **`emailNotification`** braucht eine SMTP-Senke (z. B. smtp4dev), **`llmQuery`** braucht
  `Llm:Enabled` und ein aktives Profil, die **Event-Log-Quelle** `NodePilot-TestSuite` muss
  einmalig elevated registriert werden, und die beiden invasiven Workflows brauchen ein
  privilegiertes Dienstkonto.
- **`serviceManagement` start/stop/restart** laufen nicht gegen einen von der Suite gewählten
  Dienst. Ein cmd.exe-Fixture antwortet dem SCM nie (Fehler 1053); echtes Zyklen braucht einen
  Dienst, den der Host-Betreiber über `NP_TESTSUITE_SERVICE_TARGET` selbst benennt.

## Referenzen

- Workflow-JSON-Format und Activity-Typen: [CLAUDE.md](../CLAUDE.md)
- Layout-Styleguide: [docs/workflow-styleguide.md](workflow-styleguide.md)
- Activity-Config-Keys: [docs/claude-reference.md](claude-reference.md)
