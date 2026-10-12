# Runde 7 – finaler Integrationsgegenlauf UI, Docs und Designer

2026-10-12, aktueller Arbeitsbaum `audit/practical-impact-2026-10-12`, nach Root-Freigabe
des letzten Backendfixes #142. Separater Quellengegenlauf nach Runde 6, mit beiden Skills
und unverändertem praktischen Severity-Scope. Keine Produktänderung in dieser Runde.

## Zusammenhängend gegengeprüfte Verträge

| Kette | Tatsächlicher Gegencheck |
| --- | --- |
| Identitätswechsel → UI-State → API/SSE | ProtectedRoute remountet nach Auth-Epoche/User; queryClient registriert Cacheclear. api/client verwirft überholte Antworten vor JSON-/Blob-Nutzung, AI readEventStream prüft Generation nach Read und vor Dispatch. LiveOps prüft zusätzlich Dispose vor Event/Timer/Join. Zusammen mit den in R6 geprüften Imports/Storeclearern kein neuer belegter Übertritt in die Nachfolgesession. |
| Workflownavigation → Listen-/Detailrechte | WorkflowBrowser und WorkflowsPage verwenden paged/names für ihren jeweiligen Vertrag. WorkflowsController schränkt zuerst auf lesbare Ordner ein und wendet danach IDs/Folder/Suche/Count/Sort/Paging an. resolveWorkflowRef delegiert an gemeinsamen Client; ausschließlich 404 wird null. Eine UI-Auswahl ersetzt keine Serverfreigabe. |
| Aktueller Draft → Save → Publish | useWorkflowPersistence wartet vorhandenen Saveloop ab und bindet Publish an Workflow-ID plus Generation. WorkflowEditingController prüft Edit und Lock, validiert Definition und veröffentlicht mittels CAS einschließlich Version/Enable/Lockrelease. Runtimefelder bleiben durch Sanitizer aus dem Draft-Upload. Kein zweiter Save-/Publishbesitzer eingeführt. |
| Schrittwahl → Kontext → tatsächlicher Test | StepTestPanel ist nach Workflow/Step gekeyt, wartet auf Kontext und blockiert Fehler; PropertiesPanel reicht canWrite und aktuellen Configwert weiter. ScriptEditorDialog ruft onRun(code) auf, RunScriptConfig baut daraus ConfigOverride. Serverseitig verlangt TestStep Run und bei Override zusätzlich Edit/Lock; Kontextpfade verlangen Read und globale zugelassene Rollen. |
| Produktvertrag → Demo | demo/handlers/workflows paginiert nach Filter/Sort, verwendet dieselben Items/Total-Felder, IDs-Maximum und Seitengrößengrenze. Lock deaktiviert, Publish speichert/aktiviert/löst Lock, Execute verweigert deaktivierte Workflows. Browserlokale Simulation bleibt Adapter, keine Produktionsauthentisierung. |
| Docsnavigation → Sprach-/Unterpfadvertrag | docsBase bindet den Build-Metapfad einmalig an das echte geladene Dokument; docPath liefert sprachgebundene Directory-URLs. DocMarkdown/DocPage und Website-Router aus R6 bleiben kompatibel. Kein Zugriff auf Backend-/Sessiongeheimnisse aus diesen Buildquellen. |
| Übrige Seiten/DTO-Familien | R6-Matrix für Adminsettings, Custom Activities, Alerting, Maintenance, Backup/DB/Audit/Metrics, Maschinen/Benutzer/Globals/Credentials, Dashboard/Mobile sowie Core Contract/Paging gegen aktuelle gemeinsame API-/Identitätsseams eingeordnet. Seit dieser Prüfung keine Änderung dieser UI-Verträge festgestellt, die einen erneuten familieninternen Vollread rechtfertigt. |

## Ergebnis

Abschließende Dokudelta-Prüfung: Die Exitcode-Ergänzung in
`src/nodepilot-docs-ui/content/{de,en}/cli.md` ist gegen `ExitCodes.cs` und `BaseCommand.cs`
gegengelesen. Code 5 bezeichnet HTTP 503 beziehungsweise tatsächliches ConnectionRefused;
TLS-/Authfehler werden dadurch nicht als Startupfehler umklassifiziert. Beide Sprachen stellen
richtig klar, dass der Code keine gefahrlose Wiederholung schreibender Befehle zusichert.
Dies ist der Dokumentationsabschluss von #135, kein zusätzlicher Befund; keine Tests erforderlich.

Kein neuer konkret belegter Critical/High oder Medium mit realer Alltagseinwirkung in diesem
finalen Integrationsgegenlauf. Bestehende Module behalten klare Besitzer für Session, Draft,
ETag, Streaming und Pagination; ihr Entfernen würde Vertragswissen wieder auf Aufrufer verteilen.
Kein zusätzlicher Refactor aus reiner Ähnlichkeit oder Größe abgeleitet.

Keine Tests nur zur Wiederholung einer Rundennummer gestartet. Kein Browser, Livebackend,
Deployment oder externer Dienst benutzt. Das Ergebnis beruht auf den genannten aktuellen
Quellketten und ihren Vertragsgegenchecks, nicht auf behaupteter vollständiger Zeilenabdeckung
oder einer Garantie unbekannter Fehlerfreiheit. Der projektweite Abschluss bleibt bei Root.
