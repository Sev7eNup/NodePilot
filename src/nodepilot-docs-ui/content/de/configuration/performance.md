# Performance-Dimensionierung

NodePilot leitet seine Parallelitäts-Grenzen beim Start aus der erkannten Hardware ab. `Performance:ManualTuning` schaltet zwischen dieser automatischen Dimensionierung und den wörtlich konfigurierten Werten um.

| Key | Default | Wirkung |
|---|---|---|
| `Performance:ManualTuning` | `false` | `false` = Werte aus erkannter CPU + RAM ableiten. `true` = `Engine:Runspace:*`, `Engine:MaxConcurrentSteps`, `Threading:*` und `ExecutionDispatch:*` unverändert aus der Config nehmen. **Restart-pflichtig.** |

Der Schalter ist bewusst nicht hot-reloadbar: Runspace-Pool und Dispatch-Worker-Pool entstehen einmalig beim Boot. Der Plan wird deshalb genau einmal gebaut (`PerformancePlanFactory`) und von allen Consumern gelesen — sonst liefe der hot-reloadbare ThreadPool im einen Modus, während boot-feste Consumer noch im anderen stehen.

## Was der Schalter umfasst — und was nicht

Automatisch dimensioniert werden:

`Engine:Runspace:MinRunspaces` · `Engine:Runspace:MaxRunspaces` · `Engine:MaxConcurrentSteps` · `Threading:MinWorkerThreads` · `Threading:MinIoCompletionThreads` · `ExecutionDispatch:WorkerCount`

**Ausgenommen ist `Engine:MaxConcurrentExecutions:*`** (`Global` / `PerUser`). Das ist ein Sicherheits-Cap gegen pathologische Fälle — Trigger-Schleifen, Sub-Workflow-Kaskaden —, kein Durchsatz-Hebel. Wer einen Wert setzt, meint ihn; ihn aus der Hardware abzuleiten würde genau die Schranke entschärfen, die konfiguriert wurde. Der Cap gilt daher in **beiden** Modi.

Steht `ManualTuning` auf `false`, sind die Zahlen der genannten Sektionen in `appsettings.json` ein **inertes Preset**: sie bleiben lesbar stehen, wirken aber nicht.

## Was tatsächlich in Kraft ist

Weil die Config-Datei im Automatik-Modus nicht die Wahrheit sagt, gibt es dafür einen eigenen Endpoint. Er liefert je Wert zusätzlich den **Constraint**, der ihn erzeugt hat:

```
GET /api/admin/settings/effective-sizing
```

```powershell
np settings effective-sizing
```

| Constraint | Bedeutung |
|---|---|
| `Cpu` | Die CPU-Formel war der kleinste Kandidat |
| `Ram` | Das Speicher-Teilbudget war der kleinste Kandidat |
| `Floor` | Ergebnis lag unter dem minimal sinnvollen Wert |
| `Ceiling` | Ergebnis lag über dem gemessen abgesicherten Bereich |
| `Manual` | Wörtlich aus der Config (`ManualTuning: true`) |

Die Antwort nennt außerdem den gebooteten Modus **und** den gespeicherten. Weichen beide ab, wurde der Schalter nach dem Start umgelegt und wirkt erst nach einem Neustart.

## Modell

**CPU-Dimension.** `MaxRunspaces` = Cores × 4, `MaxConcurrentSteps` = Cores × 32, `Threading:*` = max(200, Cores × 16), `ExecutionDispatch:WorkerCount` = Cores × 3.

**Speicher-Dimension.** Vom erkannten Speicher beansprucht NodePilot **60 %** im Server-Modus und **25 %** bei `Deployment:Mode=Desktop`, weil das Desktop-Paket sich die Maschine mit Postgres, der Electron-Shell und den Anwendungen des Nutzers teilt. Von diesem Anteil wird ein fixer Grundbedarf von 512 MB abgezogen (Runtime, EF-Modell, Caches, Telemetry — gemessener Idle-Footprint 383–444 MB, aufgerundet); was bleibt, ist das App-Budget. Es wird als **ein Haushalt** aufgeteilt — Runspaces 50 %, Steps 25 %, der Rest ist bewusste Reserve für GC-Spitzen. Jeden Wert einzeln gegen das volle Budget zu rechnen würde denselben Speicher mehrfach ausgeben. Wartender Dispatch liegt in der Datenbank-Outbox und hat deshalb keine In-Memory-Queue-Kapazität mehr.

Der **kleinere** der beiden Kandidaten gewinnt, danach greifen Floor und Ceiling. Die Speicher-Dimension kann einen Plan damit nur verkleinern, nie vergrößern.

**Skriptprozesse haben im Experimental mode eine eigene Grenze.** Ein lokales Skript läuft in einem eigenen PowerShell-Prozess, außerhalb des Budgets oben. NodePilot begrenzt die Zahl dieser Prozesse auf **vier pro Kern** und auf das, was **40 %** des erkannten Speichers bei 80 MB pro Prozess fassen (15 % bei `Deployment:Mode=Desktop`); der kleinere Wert gewinnt, mindestens 8 und nie mehr als `MaxConcurrentSteps`. Mehr Prozesse pro Kern machen jedes Skript langsamer, ohne dass mehr fertig werden. `Engine:MaxScriptProcesses` legt die Grenze von Hand fest und gilt **mit und ohne** `Performance:ManualTuning`; die ausgelieferte `0` behält den abgeleiteten Wert. Schlägt die Speichererkennung fehl, zählen nur die Kerne. Was an der Grenze passiert, steht unten unter *Wenn die Grenze für Skriptprozesse erreicht ist*.

**Erkennung fehlgeschlagen.** Meldet die Plattform weniger als 1 GB, gilt das als fehlgeschlagene Messung und nicht als kleine Maschine — kein unterstützter Host läuft unter 1 GB. Die Dimensionierung fällt dann auf die reine CPU-Formel zurück; `effective-sizing` weist den Speicher als nicht erkannt aus.

**Grenzen.** Floors halten eine 2-Core-/4-GB-Maschine lauffähig; Ceilings stoppen die Extrapolation am Rand des gemessenen Bereichs.

| Wert | Floor | Ceiling |
|---|---|---|
| `MinRunspaces` | 1 | 8 |
| `MaxRunspaces` | 8 | 64 |
| `MaxConcurrentSteps` | 32 | 600 |
| `MaxScriptProcesses` | 8 | `MaxConcurrentSteps` |
| `Threading:*` | 64 | 768 |
| `ExecutionDispatch:WorkerCount` | 20 | 200 |

`MinRunspaces` bleibt im Automatik-Modus immer **1**: `RunspacePool.Open()` materialisiert das Minimum sofort, und eifriges Vorwärmen ist ein gemessener Anti-Pattern (28 % Regression). Der Pool wächst unter echter Last ohnehin.

## Experimental mode

`Engine:ExperimentalMode` (Admin-Einstellungen unter Performance, standardmäßig aus, wirkt nach einem Neustart) ändert, wie lokale Skripte laufen. Aus laufen sie wie bis Version 1.5.4. Ein ändern sich zwei Dinge zusammen:

- **Dieselbe Kommandozeile für jedes Skript.** NodePilot startet für jedes lokale Skript einen PowerShell-Prozess. Bis 1.5.4 enthält das Startkommando den Pfad der Skriptdatei, jeder Start hat also eine eigene Kommandozeile. Virenscanner prüfen jede neue Kommandozeile einzeln, und die Starts stauen sich hintereinander. Im Experimental mode ist die Kommandozeile immer gleich, und der Pfad steht in der Umgebungsvariable `NODEPILOT_SCRIPT_PATH`.
- **Eine Grenze für Skriptprozesse.** Weil sich die Starts nicht mehr stauen, ist die Zahl der gleichzeitig vorhandenen PowerShell-Prozesse begrenzt; siehe *Modell* oben und *Wenn die Grenze für Skriptprozesse erreicht ist* unten.

Gemessen auf einem Server mit 8 Kernen und 8 GB, auf dem auch die Datenbank läuft, mit Microsoft Defender und automatischer Dimensionierung:

| | Aus (wie 1.5.4) | Experimental mode |
|---|---|---|
| 50 kurze Skripte gleichzeitig | 31 s | 5 s |
| 50 parallele Läufe eines Workflows mit 118 Steps | 136 s | 37 s |
| 500 parallele Läufe dieses Workflows | 1259 s | 342 s |
| CPU-Last während der 500 Läufe, Mittel | 24 % | 78 % |
| Ausführungsliste während der 500 Läufe, Median / 95 % der Anfragen | 0,06 s / 0,08 s | 0,12 s / 1,2 s |
| Eingebauter Datei- oder Registry-Step während der 500 Läufe, Median | 0,09 s | 3 s |
| Skript-Step während der 500 Läufe, Median | 15,5 s | 1,9 s |

Was das bedeutet:

- **Skripte werden drei- bis sechsmal so schnell fertig,** sobald mehrere gleichzeitig anstehen. Auf einem Server ohne Internetzugang ist der Unterschied größer, weil die Abfrage des Virenscanners dann rund zwei Sekunden pro Start dauert.
- **Der Modus nutzt die CPU, die 1.5.4 ungenutzt lässt.** Während ein Ansturm von Skripten läuft, ist die CPU voll, und die Web-Oberfläche, die eingebauten Aktivitäten und eine Datenbank auf derselben Maschine antworten langsamer. In diesen Tests ist deswegen keine Anfrage und kein Step fehlgeschlagen. Mit wenigen parallelen Skripten ist die CPU nicht voll, und es ändert sich nichts.
- **Regeln von Sicherheitsprodukten können anders greifen.** Eine Regel, die im dekodierten Startkommando nach dem Pfad der Skriptdatei sucht, findet ihn nicht mehr, und ein Produkt, das Kommandozeilen einzeln bewertet, bewertet eine Zeile für alle Skripte.
- **Der Schalter ist für eine Übergangszeit gedacht** und entfällt in einer späteren Version.

**Die Web-Oberfläche während eines Ansturms flott halten.** `Engine:MaxScriptProcesses` auf die Zahl der Kerne minus zwei setzen. Im Test mit 500 Läufen antwortete die Ausführungsliste dann bei 95 % der Anfragen innerhalb von 0,4 s, und die Läufe dauerten nicht länger (290 s). Der Preis: Skripte, die überwiegend warten, etwa auf einen Remote-Aufruf oder ein anderes System, belegen währenddessen einen der wenigen Plätze. 100 Skripte, die je 30 Sekunden warteten, brauchten 517 s statt 153 s. Wo Skripte überwiegend warten, den abgeleiteten Wert lassen.

## Wenn die Grenze für Skriptprozesse erreicht ist

Dieser Abschnitt gilt nur im Experimental mode.

NodePilot startet für jedes lokale Skript einen PowerShell-Prozess: für einen `runScript`-Step mit Engine `auto`, `powershell` oder `pwsh`, eine Custom Activity, eine Skript-Prüfung von `waitForCondition`, `startProgram` mit `useShellExecute` und einen lokalen Werkzeugaufruf eines KI-Agenten. Laufen so viele dieser Prozesse, wie die Grenze zulässt, wartet das nächste Skript, bis einer endet. Am Warten selbst scheitert nichts. Steps dauern länger, und das hat diese Folgen:

| Was | Verhalten, während ein Skript auf einen Prozess wartet |
|---|---|
| Skript-Step (`runScript`, Custom Activity) | Steht auf `Running` und hat noch keine Ausgabe. Sein eigenes `timeoutSeconds` beginnt erst mit dem Start seines Prozesses; der Step kann also länger dauern als dieser Wert. |
| `waitForCondition` im Skript-Modus | **Kann länger laufen als sein `timeoutSeconds`.** Die Wartezeit vor der ersten Prüfung zählt nicht zum Timeout; der Step dauert um so viel länger, wie er auf einen Prozess gewartet hat. Ab der ersten Prüfung läuft das Timeout. Bekommt eine spätere Prüfung bis zum Ablauf keinen Prozess, endet der Step mit Timeout. Die getypten Modi (`pathExists`, `serviceRunning`, `portOpen`, `httpOk`) brauchen keinen Prozess und sind nicht betroffen. |
| Umschließende Timeouts | Laufen während des Wartens weiter: das Timeout des Laufs, eines `startWorkflow`-Aufrufs, der auf seinen Kind-Workflow wartet, und `timeoutSecondsPerItem` von `forEach`. Ein Lauf mit vielen wartenden Skripten kann eines davon erreichen. |
| Andere Steps | **Ein wartender Skript-Step behält seinen Platz in `Engine:MaxConcurrentSteps`.** Warten viele Skripte, können auch Steps ohne Skriptprozess später starten, auch in anderen Workflows, weil die Plätze belegt sind. Das ist Absicht: Die wartenden Steps halten die Last auf die Datenbank und den In-Process-PowerShell-Pool zurück. Die Plätze freizugeben wurde gemessen und führte bei einem Ansturm zu Timeouts der Datenbankverbindungen. |

**Woran man es erkennt.** Das Server-Log enthält für jedes Warten die Zeile `every script process is in use; the script waits for a free one`. `np settings effective-sizing` und die Admin-Einstellungen unter Performance zeigen die geltende Grenze.

**Was man tun kann.** Hat der Server CPU und Speicher frei, `Engine:MaxScriptProcesses` anheben. Skripte, die überwiegend auf etwas anderes warten, etwa auf einen Remote-Aufruf, brauchen wenig CPU; für sie passt ein höherer Wert. Ist der Server an seiner Grenze, die Last zeitlich verteilen, den Workflows, die den Ansturm auslösen, ein Limit für gleichzeitige Läufe geben oder dem Server mehr Kerne und Speicher geben. Ein Zeitlimit, das auch unter Last halten muss, gehört an den Lauf, nicht an den einzelnen Step.

## Wann sich der Schalter lohnt

Die Automatik liefert einen **sicheren, monoton skalierenden Default mit begrenztem Ressourcenrisiko** — kein universelles Optimum. Das Optimum hängt zusätzlich an Workflow-Anzahl, Activity-Mix, Step-Laufzeit, Remote-Latenz und DB-Provider; nichts davon ist beim Boot bekannt. Die Automatik zielt deshalb auf leichte bis mittlere Last.

Das gemessene Hochlast-Profil — 768 Runspaces für 500 parallele Workflows auf 20 Cores — bleibt bewusst dem manuellen Modus vorbehalten, damit die Automatik es nie stillschweigend herunterstuft. Wer dieses Profil fährt, setzt `Performance:ManualTuning: true` und übernimmt die Werte aus der Lastprofil-Tabelle in [`docs/performance-improvements.md`](https://github.com/Sev7eNup/NodePilot/blob/main/docs/performance-improvements.md).

## Grenzen von Hand anheben

Manuelles Tuning lässt mehr Arbeit gleichzeitig laufen. Schneller wird der Server dadurch nicht. Gemessen mit 500 parallelen Läufen eines Workflows mit 118 Steps auf einem Server mit 8 Kernen und 8 GB, auf dem auch die Datenbank läuft, im Experimental mode:

| | Automatische Dimensionierung (256 Steps, 24 Läufe gleichzeitig) | Von Hand (600 Steps, 600 Läufe gleichzeitig) |
|---|---|---|
| Zeit, bis alle 500 Läufe beendet waren | 344 bis 370 s | 330 bis 354 s |
| Steps, die in ihr 30-Sekunden-Timeout liefen | keine | 568 |
| Speicher des Dienstes in der Spitze | 1,6 GB | 4,1 GB, ohne Step-Timeouts 9,0 GB |
| Speicher des Dienstes fünf Minuten danach | 0,8 bis 1,0 GB | 2,6 bis 5,1 GB |
| Kleinster Speicher, der dem SQL Server blieb | 1,7 GB | 0,3 GB |

Mit ausgeschaltetem Experimental mode ist das Bild dasselbe, nur langsamer: 1354 bis 1380 s mit automatischer Dimensionierung und 1323 bis 1384 s von Hand, bei einer Spitze von 1,2 GB gegenüber 4,3 bis 4,5 GB.

Was das zeigt:

- **Mehr Steps, als die CPU schafft, werden nicht früher fertig.** Beide Einstellungen brauchten etwa gleich lang.
- **Das Timeout eines Steps zählt die Zeit auf der Uhr.** Steps, die im Dienst selbst laufen (Datei-, Registry-, Dienst- und WMI-Aktivitäten), brauchen normalerweise Millisekunden. Teilen sich bis zu 600 Steps 8 Kerne, dauern sie so lange, dass 568 von ihnen in ihr `timeoutSeconds` liefen, obwohl mit ihnen alles in Ordnung war.
- **Jeder offene Lauf hält die Ausgaben seiner Steps im Speicher, bis er endet.** `ExecutionDispatch:WorkerCount` bestimmt, wie viele Läufe gleichzeitig offen sind. Der Speicherbedarf ist etwa die Zahl der offenen Läufe mal die Größe ihrer Ausgaben; ein Step mit großer Ausgabe, etwa einer Verzeichnisliste oder einem Abfrageergebnis, zählt für jeden offenen Lauf einmal. Ohne Step-Timeouts wollte der Dienst mehr Speicher, als der Server hat.
- **Der Speicher kommt verzögert zurück.** Fünf Minuten nach der Last hielt der Dienst noch 2,6 bis 5,1 GB.
- **Ein Datenbankserver auf derselben Maschine wird verdrängt.** Während der Dienst wuchs, nahm Windows dem SQL Server den Speicher weg, von 2,9 auf 0,3 GB. Im Lauf ohne Step-Timeouts hielt NodePilot daraufhin 320-mal seine Arbeit an, weil die Datenbank nicht rechtzeitig antwortete, und sechs Läufe endeten vorzeitig.

**Die Warnung.** Ist `Engine:MaxConcurrentSteps` von Hand auf mehr als das Doppelte des Werts gesetzt, den die automatische Dimensionierung verwenden würde, schreibt NodePilot beim Start eine Warnung ins Server-Log, und die Admin-Einstellungen zeigen sie am Feld. Die Messungen oben entstanden beim 2,3-Fachen des automatischen Werts.

**Was man tun kann.**

- Bei der automatischen Dimensionierung bleiben, solange keine Messung zeigt, dass die Grenzen den Server bremsen.
- Zuerst auf die CPU schauen. Steht sie unter der Last auf 100 %, bringt eine höhere Grenze nichts. Von höheren Grenzen profitieren Workflows, die überwiegend warten: auf entfernte Maschinen, Verzögerungen oder Bedingungen.
- Die Werte schrittweise anheben und die Zeit für dieselbe Last vergleichen.
- Speicher für die offenen Läufe einplanen und einem Datenbankserver auf derselben Maschine ein festes Minimum oder eine eigene Maschine geben.
- Ein Zeitlimit, das auch unter Last halten muss, gehört an den Lauf, nicht an den einzelnen Step.
