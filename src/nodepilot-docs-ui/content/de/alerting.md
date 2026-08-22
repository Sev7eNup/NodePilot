# Alerting

Alerting sendet Benachrichtigungen zu Workflow-Ereignissen und Systemzuständen. Ohne aktive Policy oder aktive Regel erfolgt keine Zustellung.

Die Seite **Alerting** enthält zwei Bereiche:

| Bereich | Verwendung |
|---|---|
| **System-Alarme** | Überwachung bekannter Systemwerte wie Backlog, Erreichbarkeit oder Credential-Ablauf |
| **Benutzerdefinierte Regeln** | Benachrichtigung bei ausgewählten Workflow- und Betriebsereignissen |

Für neue Überwachungen sind System-Alarme die einfachere Wahl. Benutzerdefinierte Regeln eignen sich für eigene Kombinationen aus Ereignistyp, Geltungsbereich und Filtern.

## Benachrichtigungskanäle

Eine Policy oder Regel kann mehrere Kanäle enthalten.

| Kanal | Ziel | Voraussetzung |
|---|---|---|
| **E-Mail** | einzelne Empfängeradresse | eingerichtete SMTP-Verbindung |
| **Webhook** | HTTP(S)-Endpunkt | erreichbare und zulässige Zieladresse; HTTPS wird empfohlen |

Die SMTP-Verbindung wird unter **Einstellungen → Integrationen** mit Host, Port, Absender, TLS und optionalen Zugangsdaten konfiguriert. Dort steht auch ein Verbindungstest zur Verfügung.

Ein Webhook erhält eine JSON-Nachricht mit Ereignistyp, Schweregrad, Workflow, Status, Fehler und Zeitpunkt. Ein optionales Secret signiert die Nachricht per HMAC-SHA256 im Header `X-NodePilot-Signature`.

## Schnelle Einrichtung

1. Unter **Alerting** den passenden Bereich öffnen.
2. Eine Systemquelle auswählen oder eine benutzerdefinierte Regel anlegen.
3. Bedingung, Geltungsbereich und mindestens einen Kanal festlegen.
4. Mit **Preview** beziehungsweise **Aktuelle Werte prüfen** die Auswahl kontrollieren.
5. Speichern und eine **Testbenachrichtigung** senden.
6. Policy oder Regel aktivieren.

Die Vorschau versendet keine Nachricht. Eine Testbenachrichtigung prüft die gespeicherten Kanäle tatsächlich.

## System-Alarme

System-Alarme überwachen von NodePilot bereitgestellte Messwerte. Für eine Quelle können mehrere Policies mit unterschiedlichen Schwellen, Empfängern oder Geltungsbereichen angelegt werden.

### Verfügbare Quellen

| Kategorie | Quelle | Zweck |
|---|---|---|
| Ausführung | Ausführungs-Ergebnis | erfolgreiche, fehlgeschlagene oder abgebrochene Läufe |
| Ausführung | Hängende Ausführung | ungewöhnlich lange laufende Ausführungen |
| Ausführung | Workflow-Gesundheit | Fehlerrate und Laufzeitentwicklung eines Workflows |
| Warteschlange | Execution-Backlog | Summe aus wartenden und laufenden Ausführungen |
| Warteschlange | Warteschlangen-Tiefe | Anzahl ausschließlich wartender Ausführungen |
| Warteschlange | Abbruch-Rate | Anzahl abgebrochener Ausführungen in einem Zeitfenster |
| Systemzustand | Maschine nicht erreichbar | fehlgeschlagener gespeicherter Verbindungstest |
| Systemzustand | Dienst-Heartbeat veraltet | ausbleibender Status eines Hintergrunddienstes |
| Systemzustand | Alarm-Zustellung fehlgeschlagen | wiederholte Fehler beim E-Mail- oder Webhook-Versand |
| Systemzustand | Trigger nicht registriert | Trigger, der nicht aktiv werden kann, etwa wegen eines unerreichbaren Verzeichnisses |
| Zeitplan | Zeitplan verpasst | erwarteter geplanter Start ohne passende Ausführung |
| Zeitplan | Kein aktueller Workflow-Erfolg | geplanter Workflow ohne aktuellen erfolgreichen Lauf |
| Credentials | Credential läuft ab | bevorstehendes oder bereits erreichtes Ablaufdatum |
| Sicherheit | Audit-Ereignis | Einträge des Audit-Logs wie fehlgeschlagene Logins, Sperrungen, Break-Glass-Anmeldungen, Rollenwechsel oder Credential-Löschungen — filterbar nach Code, Ergebnis, Benutzer, IP und dem Details-JSON |

Eine Quelle kann als **Nicht verfügbar** erscheinen, wenn die benötigten Daten fehlen. Beispiele:

- Maschinen ohne bisherigen Verbindungstest werden nicht als nicht erreichbar bewertet.
- Credentials ohne gepflegtes Ablaufdatum werden nicht überwacht.
- Workflow-bezogene Quellen benötigen vorhandene Ausführungs- oder Zeitplandaten.
- „Trigger nicht registriert" ist nur verfügbar, solange tatsächlich ein Trigger betroffen ist. Im Hochverfügbarkeits-Betrieb kennt nur der aktive Knoten diesen Zustand; auf dem passiven Knoten erscheint die Quelle als nicht verfügbar, obwohl der aktive Knoten korrekt alarmiert.

### Policy konfigurieren

| Einstellung | Bedeutung |
|---|---|
| **Vorlage** | trägt eine sinnvolle Ausgangskonfiguration ein |
| **Bedingung** | legt fest, bei welchem Wert alarmiert wird |
| **Quellen-Parameter** | bestimmt beispielsweise das betrachtete Zeitfenster |
| **Dauer bis Alarm** | Bedingung muss für diese Zeit durchgehend erfüllt sein |
| **Schweregrad** | `Info`, `Warning` oder `Critical` |
| **Geltungsbereich** | global, Ordner oder einzelne Workflows; abhängig von der Quelle |
| **Cooldown** | Mindestabstand zwischen wiederholten Meldungen |
| **Routen** | E-Mail- und Webhook-Ziele |

**Aktuelle Werte prüfen** zeigt, welche vorhandenen Werte die Policy momentan erfüllen. Eine Vorlage füllt den Editor nur aus und aktiviert die Policy nicht automatisch.

## Benutzerdefinierte Regeln

Benutzerdefinierte Regeln reagieren auf Ereignisse. Eine Regel besteht aus Ereignistypen, optionalen Filtern, einem Geltungsbereich und mindestens einem Kanal.

### Ereignistypen

| Gruppe | Ereignisse |
|---|---|
| Ausführungen | fehlgeschlagen, erfolgreich, abgebrochen, läuft lange, wartet lange |
| Zugangsdaten | Credential-Fehler, Credential läuft ab |
| Betrieb | Service veraltet, Maschine nicht erreichbar, Backlog hoch, Pending-Backlog hoch, Abbruch-Rate hoch |
| Zeitpläne | Zeitplan verpasst, kein aktueller Workflow-Erfolg |
| System | System-Alarm |

Für einen manuellen Abbruch kann das Feld **Abgebrochen von** gefiltert werden. Der Wert `user` begrenzt die Regel auf einzeln durch eine Person abgebrochene Ausführungen.

### Regel konfigurieren

| Einstellung | Bedeutung |
|---|---|
| **Ereignistypen** | Ereignisse, auf die die Regel reagiert |
| **Geltungsbereich** | alle Workflows, ausgewählte Ordner oder ausgewählte Workflows |
| **Filter** | zusätzliche Bedingungen, beispielsweise Status, Workflow-Name, Dauer oder Zielmaschine |
| **Gruppieren nach** | fasst gleichartige Ereignisse für die Wiederholungssteuerung zusammen |
| **Kanäle** | E-Mail- oder Webhook-Ziele |
| **Kanalbedingung** | versendet einen bestimmten Kanal nur bei passender Zusatzbedingung |
| **Cooldown** | verhindert zu häufige Wiederholungen derselben Meldung |
| **Min. Vorkommen und Zeitfenster** | alarmiert erst, wenn ein Ereignis innerhalb des Zeitfensters mehrfach auftritt |

Ein leerer Filter lässt jedes gewählte Ereignis im festgelegten Geltungsbereich zu. Eine leere Gruppierung verwendet die Standardgruppierung des Ereignisses.

Beispiel: Eine Regel für **Ausführung fehlgeschlagen** kann global gelten, E-Mail nur bei `Critical` senden und einen Webhook ausschließlich für einen bestimmten Ordner auslösen.

## Vorschau und Test

Die beiden Prüfungen haben unterschiedliche Aufgaben:

| Prüfung | Ergebnis |
|---|---|
| **Preview** | prüft Regel, Filter, Gruppierung und Kanalbedingungen anhand eines Beispielereignisses |
| **Aktuelle Werte prüfen** | wertet eine System-Policy gegen momentan verfügbare Messwerte aus |
| **Testbenachrichtigung** | sendet eine echte Nachricht an alle gespeicherten Kanäle |

Eine neue Konfiguration sollte zunächst deaktiviert gespeichert, getestet und anschließend aktiviert werden.

## Zustellungsverlauf

Die Aktion **Zustellungen** öffnet den Verlauf der Versandversuche. Angezeigt werden:

- Zeitpunkt und Regel
- Kanal und Ziel
- Status `Ausstehend`, `Gesendet` oder `Fehlgeschlagen`
- Nummer des Versandversuchs
- Fehlermeldung

Fehlgeschlagene Zustellungen werden erneut versucht und nach fünf erfolglosen Versuchen als fehlgeschlagen markiert. Die Aufbewahrungsdauer richtet sich nach der Notification-Retention; standardmäßig werden abgeschlossene Einträge 90 Tage gespeichert.

## Berechtigungen und Sicherheit

- Admin und Operator dürfen Regeln und Zustellungen lesen.
- Nur Admins dürfen Policies und Regeln anlegen, ändern, löschen, testen oder aktivieren.
- Webhook-Secrets werden verschlüsselt gespeichert und nicht wieder angezeigt.
- Webhook-Ziele unterliegen den konfigurierten Regeln für ausgehende Verbindungen.
- Änderungen und Testauslösungen werden im Audit-Log erfasst.

Alerting versendet derzeit keine automatische Entwarnung, wenn ein Zustand wieder normal ist.
