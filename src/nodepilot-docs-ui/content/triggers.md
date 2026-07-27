# Trigger

Ein Trigger legt fest, wodurch ein Workflow startet. Ein Workflow kann mehrere Trigger enthalten. Jeder Trigger startet den Workflow unabhängig von den anderen Triggern.

## Trigger hinzufügen

1. Einen Trigger aus der Node-Bibliothek auf den Canvas ziehen.
2. Den Trigger auswählen und im Eigenschaften-Panel konfigurieren.
3. Den Trigger mit der ersten Activity verbinden.
4. Den Workflow speichern, veröffentlichen und aktivieren.

Automatische Trigger werden nur für veröffentlichte und aktivierte Workflows überwacht.

## Trigger-Typen

| Trigger | Verwendung |
|---|---|
| **Manuell** | Start über Web-UI, Desktop-App oder API |
| **Zeitplan** | Start zu festgelegten Zeiten |
| **Webhook** | Start durch eine HTTP-Anfrage |
| **Dateiüberwachung** | Start bei einer Änderung in einem Verzeichnis |
| **Datenbank** | Start, wenn sich das Ergebnis einer SQL-Abfrage ändert |
| **Windows-Ereignisprotokoll** | Start bei einem passenden Windows-Ereignis |

## Manueller Trigger

Der manuelle Trigger eignet sich für Workflows, die gezielt gestartet werden.

Konfigurierbar sind:

- Titel und Beschreibung des Startdialogs
- Eingabeparameter vom Typ Text, Zahl, Ja/Nein oder Auswahl
- Pflichtangabe und Standardwert je Parameter

Beim Start zeigt NodePilot die definierten Eingabefelder an. Ein Parameter namens `customerId` steht im Workflow als `{{manual.customerId}}` zur Verfügung.

## Zeitplan

Der Zeitplan-Trigger startet einen Workflow anhand eines Cron-Ausdrucks. Vorlagen stehen für häufige Zeitpläne zur Verfügung:

| Zeitplan | Cron-Ausdruck |
|---|---|
| alle 5 Minuten | `0 */5 * * * ?` |
| jede Stunde | `0 0 * * * ?` |
| täglich um 06:00 Uhr | `0 0 6 * * ?` |
| Montag bis Freitag um 08:00 Uhr | `0 0 8 ? * MON-FRI` |

Die Vorschau im Eigenschaften-Panel zeigt die nächsten Ausführungszeiten und weist auf einen ungültigen Ausdruck hin.

Ausgabedaten:

- `firedAt`: Zeitpunkt der aktuellen Auslösung
- `nextFireAt`: nächste geplante Auslösung

## Webhook

Ein Webhook startet einen Workflow durch eine Anfrage an:

```text
<NodePilot-Adresse>/api/webhooks/<Workflow>/<Pfad>
```

HTTP-Methode und Pfad müssen mit der Trigger-Konfiguration übereinstimmen. In der Web-UI stehen `POST`, `PUT` und `GET` zur Auswahl.

### Zugriff absichern

Zwei Verfahren stehen zur Verfügung:

| Verfahren | Verwendung |
|---|---|
| **Shared Secret** | Das konfigurierte Secret wird im Header `X-Webhook-Secret` gesendet. |
| **NodePilot HMAC v2** | Signierte Anfragen mit Zeitstempel und eindeutiger Delivery-ID; geeignet für Integrationen mit Replay-Schutz. |

HMAC v2 benötigt ein sicher erzeugtes Secret mit mindestens 32 UTF-8-Bytes. Der Sender muss außerdem `X-NodePilot-Timestamp`, `X-NodePilot-Delivery-Id` und die konfigurierte Signatur mitsenden.

Native HMAC-Signaturen von GitHub, GitLab oder Alertmanager sind nicht direkt mit NodePilot HMAC v2 kompatibel. Dafür ist ein Adapter erforderlich, der die Anbieter-Signatur prüft und anschließend eine NodePilot-Anfrage erzeugt.

Weitere Sicherheitseinstellungen enthält [Härtung](./security/hardening).

### Werte aus dem Body übernehmen

Feld-Mappings übernehmen einzelne Werte aus einem JSON-Body. Jedes Mapping besteht aus einem Namen und einem JSONPath.

Beispiel:

| Name | JSONPath | Verwendung im Workflow |
|---|---|---|
| `ticketId` | `$.ticket.id` | `{{manual.ticketId}}` |

Wenn der Body kein JSON enthält oder der Pfad nicht gefunden wird, bleibt der gemappte Wert leer.

Weitere verfügbare Webhook-Daten:

- `webhookBody`
- `webhookMethod`
- `webhookPath`
- Query-Parameter als `webhookQuery_<Name>`
- freigegebene Header im Shared-Secret-Modus als `webhookHeader_<Name>`

## Dateiüberwachung

Die Dateiüberwachung reagiert auf Dateien in einem Verzeichnis.

Konfigurierbar sind:

- absoluter Verzeichnispfad
- Dateifilter, zum Beispiel `*.csv`
- Ereignis: erstellt, geändert, gelöscht, umbenannt oder alle Änderungen
- Einbeziehung von Unterverzeichnissen

Der Pfad bezieht sich auf das Dateisystem des Rechners, auf dem NodePilot ausgeführt wird. Das Verzeichnis muss vorhanden sein, erreichbar sein und innerhalb der serverseitig erlaubten Pfade liegen.

Ausgabedaten:

- `fileAction`: Art der Änderung
- `filePath`: vollständiger Dateipfad
- `fileName`: Dateiname

## Datenbank

Der Datenbank-Trigger prüft regelmäßig eine SQL-Abfrage. Der Wert der ersten Spalte der ersten Zeile dient als Vergleichswert. Der erste Abruf legt den Ausgangswert fest; jede spätere Änderung startet den Workflow.

Konfigurierbar sind:

- Name einer hinterlegten Datenbankverbindung
- Prüfintervall
- SQL-Abfrage

Die Abfrage läuft vor dem Workflow und kann deshalb keine Workflow-Variablen wie `{{...}}` verwenden. Zugangsdaten gehören in die Serverkonfiguration, nicht in die Workflow-Definition.

Ausgabedaten:

- `dbSentinel`: neuer Vergleichswert
- `dbPrevious`: vorheriger Vergleichswert

## Windows-Ereignisprotokoll

Dieser Trigger startet den Workflow bei einem passenden Eintrag im Windows-Ereignisprotokoll.

Konfigurierbar sind:

- Protokoll, zum Beispiel `Application` oder `System`
- Ereignistyp
- optionale Quelle
- optionale Ereignis-ID
- Suchzeitraum

`Application` und `System` sind standardmäßig erlaubt. Weitere Protokolle, insbesondere `Security`, müssen administrativ freigegeben werden. Der Trigger ist nur auf Windows verfügbar.

Ausgabedaten:

- `eventSource`
- `eventEntryType`
- `eventId`
- `eventMessage`
- `eventTimeWritten`

## Trigger-Daten verwenden

Trigger-Daten stehen nach dem Trigger-Node für verbundene Activities zur Verfügung:

```text
{{manual.<Name>}}
```

Alternativ ist der Zugriff über die Ausgabevariable des Trigger-Nodes möglich:

```text
{{<Ausgabevariable>.param.<Name>}}
```

Beispiel für eine Dateiüberwachung mit der Ausgabevariable `watch`:

```text
{{manual.filePath}}
{{watch.param.filePath}}
```

Einen Namespace `{{trigger.*}}` gibt es nicht.

## Workflow extern über die API starten

Ein veröffentlichter und aktivierter Workflow kann unabhängig von einem Webhook-Node über die External-Trigger-API gestartet werden:

```bash
curl -X POST "https://nodepilot.example/api/trigger/Deploy" \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: <API-Key>" \
  -H "Idempotency-Key: deploy-2026-07-27-001" \
  -d '{"parameters":{"version":"2.1.0"}}'
```

Voraussetzungen:

- `ExternalTrigger:ApiKey` ist administrativ konfiguriert und mindestens 32 UTF-8-Bytes lang.
- `X-Api-Key` enthält diesen Schlüssel.
- `Idempotency-Key` ist optional. Wiederholte Anfragen mit demselben Schlüssel starten innerhalb von 24 Stunden keine zweite Ausführung.

Weitere Informationen enthält [Workflow-Steuerung](./api/workflow-control).
