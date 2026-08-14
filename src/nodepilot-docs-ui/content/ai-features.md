# AI-Features

NodePilot bindet ein OpenAI-kompatibles Sprachmodell ein. Unterstützt werden Cloud-Dienste und lokale Endpunkte wie Ollama, LM Studio, vLLM, LocalAI oder llama.cpp.

Die AI-Funktionen sind standardmäßig deaktiviert. Die zugehörigen Schaltflächen (Script-Editor, Workflow-Designer, AI-Chat) erscheinen erst, wenn in den Admin-Einstellungen ein LLM-Profil konfiguriert und aktiviert wurde. Generierte Inhalte werden nie automatisch veröffentlicht oder ausgeführt.

## Einsatzbereiche

| Bereich | Zweck | Kann Änderungen erzeugen? |
|---|---|---|
| **Script-Editor** | PowerShell für eine `runScript`-Activity erstellen oder überarbeiten | Ja, nach manueller Übernahme in den Editor |
| **Workflow-Designer** | neue Workflows erstellen sowie geöffnete Workflows erklären, prüfen und ändern | Ja, nach Prüfung und Bestätigung |
| **AI-Chat** | Fragen zu NodePilot, Dokumentation und freigegebenen Betriebsdaten beantworten | Nein, ausschließlich lesend |

## Script-Editor

**Ort:** `runScript`-Activity öffnen, Script-Editor maximieren und die AI-Funktion über das Sparkles-Symbol starten. Die Schaltfläche steht nur den Rollen Admin und Operator zur Verfügung.

**Geeignet für:**

- neues PowerShell-Script aus einer kurzen Aufgabenbeschreibung
- Ergänzung eines bestehenden Scripts
- Fehlerkorrektur oder Vereinfachung
- Anpassung an verfügbare Workflow-Variablen

Ein vorhandenes Script und verfügbare Variablen werden als Kontext berücksichtigt. Das Ergebnis kann am Cursor eingefügt oder als vollständiger Ersatz übernommen werden.

Die Übernahme ändert nur den Inhalt im Editor. Vor dem Speichern und Ausführen ist eine Prüfung auf Befehle, Pfade, Berechtigungen und verwendete Variablen erforderlich.

## Workflow-Designer

Im Workflow-Bereich stehen zwei Funktionen zur Verfügung.

### Neuen Workflow erzeugen

**Ort:** Workflow-Übersicht, Aktion **KI generieren**.

Aus einer Beschreibung erstellt NodePilot einen vollständigen Workflow-Entwurf mit Triggern, Activities und Verbindungen. Vor dem Anlegen erscheinen eine Vorschau, die erzeugte Definition und die Anzahl der Nodes und Edges. Erst die Bestätigung legt den Workflow an.

Geeignet für:

- einen ersten ausführbaren Entwurf
- typische lineare oder verzweigte Abläufe
- eine Ausgangsbasis für die weitere Bearbeitung im Designer

Maschinen, Zugangsdaten, Zielpfade und fachliche Bedingungen müssen anschließend geprüft und vervollständigt werden.

Generierung und Assistent kennen alle Activity-Typen — auch die `llmQuery`-Activity, Schleifen und Verzweigungen. Zusätzlich kennen sie die auf dieser Installation aktivierten Custom Nodes und schlagen sie vor, statt deren Funktion aus einzelnen Skript-Schritten nachzubauen.

### Geöffneten Workflow bearbeiten

**Ort:** Workflow-Designer, Schaltfläche **KI-Assistent**.

Der Assistent kennt den aktuell geöffneten Workflow. Mögliche Aufgaben sind:

- Aufbau und Ablauf erklären
- mögliche Fehlerstellen beschreiben
- Ausführungshistorie und fehlgeschlagene Schritte analysieren
- Error-Handling oder zusätzliche Schritte vorschlagen
- ausgewählte Nodes über `@` oder die aktuelle Canvas-Auswahl gezielt einbeziehen
- Layout aufräumen

Änderungen erscheinen zuerst als Vorschlag. Einzelne Nodes und Edges können ausgewählt, übernommen oder verworfen werden. Eine übernommene Änderung kann unmittelbar rückgängig gemacht werden.

Ändert sich der Canvas nach der Erstellung eines Vorschlags, wird der veraltete Vorschlag nicht mehr übernommen. Dadurch werden zwischenzeitliche Bearbeitungen geschützt.

Der workflowbezogene Chat unterstützt mehrere benannte Threads, erneute Generierung, Markdown-Export und eine Ansicht der bisherigen AI-Aktivität.

Chat und Properties-Panel teilen sich den rechten Bereich des Designers: Der geöffnete Assistent überlagert die Properties. Sobald im Canvas ein Node oder eine Verbindung angeklickt wird, tritt der Chat zurück und die zugehörigen Properties erscheinen wieder. Eine Mehrfachauswahl lässt den Chat stehen — sie ist sein Auswahl-Kontext.

## Globaler AI-Chat

**Ort:** Navigation, Seite **AI Chat**.

Der globale AI-Chat ist nicht an einen geöffneten Workflow gebunden. Er dient als lesender Assistent für Fragen wie:

- Einrichtung eines Triggers oder Deployments
- Erklärung vorhandener Workflows
- Suche nach fehlgeschlagenen oder geplanten Ausführungen
- Informationen zu Maschinen und Betrieb
- Fragen zum Quellcode, sofern diese Quelle freigegeben ist

Der Chat kann keine Workflow-Änderungen vorschlagen oder übernehmen. Antworten richten sich nach den administrativ aktivierten Wissensquellen:

| Wissensquelle | Inhalt | Zugriff |
|---|---|---|
| **Dokumentation** | Inhalte der NodePilot-Dokumentation | alle authentifizierten Rollen |
| **Workflows und Betrieb** | Workflow-Definition und statische Analyse (Admin und Operator, zusätzlich Folder-RBAC); geplante Ausführungszeitpunkte für alle Rollen | siehe Inhalt |
| **Quellcode** | bereitgestellter NodePilot-Quellcode | Admin und Operator |
| **Datenbank** | lesende Fragen zu Betriebsdaten | ausschließlich globale Admins |

Die Datenbankquelle führt ausschließlich lesende Abfragen aus. Schreiboperationen werden blockiert. Geschützte Spalten und erkannte Secrets werden nicht an das Modell ausgegeben.

Die Einstiegsvorschläge auf der leeren Chat-Seite richten sich nach den verfügbaren Wissensquellen. Steht die Datenbankquelle zur Verfügung, werden Betriebsauswertungen vorgeschlagen — etwa die letzten fehlgeschlagenen Läufe, hängende Ausführungen oder nicht erreichbare Maschinen. Andernfalls erscheinen Fragen zur Dokumentation und zum Zeitplan, die auch ohne diese Quelle beantwortet werden können.

## Abgrenzung

| Aufgabe | Passende Funktion |
|---|---|
| PowerShell für einen einzelnen Schritt erstellen | Script-Editor |
| neuen Workflow aus einer Beschreibung anlegen | KI-Generierung in der Workflow-Übersicht |
| aktuellen Workflow erklären oder verändern | KI-Assistent im Workflow-Designer |
| allgemeine Fragen zu NodePilot oder zum Betrieb stellen | globaler AI-Chat |
| während eines Workflow-Laufs ein Modell aufrufen | `llmQuery`-Activity |

## `llmQuery`-Activity

`llmQuery` ist keine Bedienhilfe, sondern eine Activity innerhalb eines Workflows. Während der Ausführung sendet die Activity einen Prompt an das konfigurierte Modell und gibt den Antworttext an nachfolgende Schritte weiter.

Konfigurierbar sind unter anderem:

- Prompt und optionaler System-Prompt
- Modell und Endpunkt
- maximale Antwortlänge und Temperatur
- Text- oder JSON-Ausgabe
- Timeout

Standardmäßig verwendet die Activity das aktive LLM-Profil. Abweichende Einstellungen können am Node gesetzt werden. `Llm:Enabled=false` deaktiviert auch diese Activity, ebenso ein fehlendes aktives Profil.

Weitere Felder und Ausgaben enthält die [`llmQuery`-Referenz](activities-reference).

## Berechtigungen und Sicherheit

- Script- und Workflow-Generierung erfordern die Rolle Admin oder Operator.
- Lesende Fragen im workflowbezogenen und globalen Chat sind für authentifizierte Rollen möglich.
- Workflow-Vorschläge dürfen nur mit Bearbeitungsrecht und aktivem Bearbeitungs-Lock übernommen werden.
- Quellcodewissen im globalen Chat ist auf Admin und Operator beschränkt; Datenbankwissen mit Raw-SQL steht ausschließlich globalen Admins zur Verfügung. Folder-Rechte erhöhen einen Operator nicht in diese Capability. Workflow-Definitionen und statische Workflow-Analysen bleiben Admin und Operator mit Folder-RBAC vorbehalten; ein Viewer erhält aus der Betriebsquelle nur die geplanten Ausführungszeitpunkte.
- Folder-RBAC begrenzt den Zugriff auf Workflow-Daten.
- Secrets werden vor Modellanfragen redigiert.
- Generierte Scripts und Workflow-Änderungen erfordern immer eine fachliche Prüfung.
- AI-Aktionen und übernommene Vorschläge werden im Audit-Log erfasst.

## LLM konfigurieren

NodePilot speichert beliebig viele **LLM-Profile**. Ein Profil beschreibt genau eine Verbindung — Endpunkt, Modell, Schlüssel und Grenzwerte. Genau ein Profil ist aktiv und wird von allen AI-Funktionen verwendet; der Wechsel zwischen Profilen ist ein Speichervorgang in den Einstellungen und erfordert kein erneutes Eintragen der Verbindungsdaten.

```json
{
  "Llm": {
    "Enabled": false,
    "ActiveProfileId": "openai",
    "Profiles": {
      "openai": {
        "Name": "OpenAI Cloud",
        "BaseUrl": "https://api.openai.com/v1",
        "ApiKey": null,
        "Model": "gpt-4o-mini",
        "MaxTokens": 4096,
        "TimeoutSeconds": 90,
        "EnableToolCalling": false,
        "ToolCallMaxDepth": 6
      }
    }
  }
}
```

| Einstellung | Bedeutung |
|---|---|
| `Enabled` | aktiviert oder deaktiviert sämtliche AI-Funktionen |
| `ActiveProfileId` | Kennung des Profils, das alle AI-Funktionen verwenden; muss auf ein vorhandenes Profil zeigen |
| `Profiles` | die gespeicherten Verbindungen, nach unveränderlicher Profil-Kennung abgelegt |

Je Profil:

| Einstellung | Bedeutung |
|---|---|
| `Name` | Anzeigename; frei änderbar, die Kennung bleibt bestehen |
| `BaseUrl` | HTTPS-Adresse eines OpenAI-kompatiblen Endpunkts; HTTP ist nur für exakte Loopback-Ziele (`localhost`, `127.0.0.0/8`, `::1`) erlaubt. Der Pfad bestimmt das Anfrageformat (siehe unten). |
| `ApiKey` | API-Schlüssel; für lokale Modelle häufig nicht erforderlich |
| `Model` | verwendeter Modellname |
| `MaxTokens` | maximale Länge einer Modellantwort (256 bis 1.000.000) |
| `TimeoutSeconds` | wie lange das Modell für seine Antwort brauchen darf — nicht die Wartezeit auf die Verbindung, die hat eigene, kurze Fristen |
| `EnableToolCalling` | erlaubt den Chats, freigegebene lesende Analyse- und Wissensquellen zu verwenden |
| `ToolCallMaxDepth` | maximale Anzahl aufeinanderfolgender Tool-Aufrufe pro Frage |

### Ausgehender Proxy

In Unternehmensnetzen ist ausgehender Verkehr oft nur über einen Proxy erlaubt. Die Einstellungen
dafür liegen unter `Llm:Proxy` und gelten für **alle** AI-Aufrufe — beide Chats, die
Script- und Workflow-Generierung, die `llmQuery`-Aktivität und die Verbindungsprüfung in den
Einstellungen. Es ist bewusst ein Block für die gesamte Installation und nicht einer je Profil:
der gemischte Fall — Cloud-Modell über den Proxy, lokales Modell direkt — wird über die
Ausnahmeliste abgebildet.

Ein Proxy ist dabei die Antwort auf „ausgehender Verkehr **darf** dieses Netz nur über den Proxy
verlassen" — nicht auf „der Endpunkt ist nicht erreichbar". Liegt der Endpunkt in einem anderen
Netzsegment und fehlt die Freischaltung in der Firewall, ändert ein Proxy daran nichts. Prüfen Sie
deshalb zuerst, welche Stufe scheitert: der Abschnitt
[Wenn der Endpunkt nicht erreichbar ist](#wenn-der-endpunkt-nicht-erreichbar-ist) beantwortet das
in Sekunden.

| Einstellung | Bedeutung |
|---|---|
| `Mode` | `Off` verbindet direkt (Voreinstellung), `System` übernimmt den Proxy des Dienstkontos samt dessen Ausnahmeregeln, `Custom` verwendet die Adresse unten |
| `Address` | Adresse des Proxys, z. B. `http://proxy.firma.local:8080`; bei `Custom` erforderlich |
| `BypassList` | Hosts, die direkt erreicht werden; Platzhalter erlaubt, etwa `localhost` oder `*.firma.local` |
| `Username` | Benutzername für Proxys mit einfacher Anmeldung |
| `Password` | zugehöriges Kennwort; besser über die Umgebungsvariable `Llm__Proxy__Password` setzen |
| `UseDefaultCredentials` | meldet sich mit den Windows-Anmeldedaten des Dienstkontos am Proxy an — der Normalfall bei domänenintegrierten Proxys |

Zu beachten: Läuft der Verkehr über einen Proxy, löst dieser die Zieladresse auf. Die zusätzliche
Prüfung, die NodePilot sonst unmittelbar vor dem Verbindungsaufbau vornimmt, greift dann nur noch
für den Proxy selbst; die Base-URL wird weiterhin beim Speichern und beim Start geprüft.

Änderungen am Proxy wirken ohne Dienstneustart. Einzige Ausnahme ist `System`: Änderungen an den
Windows-Proxy-Einstellungen selbst werden erst nach einem Neustart des Dienstes übernommen.

### Wenn der Endpunkt nicht erreichbar ist

Den Endpunkt zu erreichen und auf eine Antwort zu warten sind zwei getrennte Dinge mit getrennten
Fristen. `TimeoutSeconds` gilt nur für die Antwort des Modells; der Verbindungsaufbau scheitert
unabhängig davon nach wenigen Sekunden. Ein großzügiges Zeitlimit für ein langsames Modell führt
also nicht dazu, dass man bei einem unerreichbaren Endpunkt minutenlang wartet.

Die Fehlermeldung benennt die Stufe, an der es gescheitert ist:

| Meldung beginnt mit | Bedeutung |
|---|---|
| `LLM endpoint DNS:` | Der Name konnte nicht aufgelöst werden — falscher Name, falsches Suffix, oder der Namensdienst antwortet nicht. |
| `LLM endpoint TCP:` | Der Rechner war nicht erreichbar. *Abgelehnt* heißt: der Host antwortet, aber auf diesem Port hört nichts. *Keine Antwort* heißt in aller Regel Firewall oder Netzsegment. |
| `LLM endpoint TLS:` | Die Verbindung stand, aber die Verschlüsselung kam nicht zustande — häufig ein verlangtes Client-Zertifikat oder ein Zertifikat, dem der Server nicht vertraut. |
| `accepted the request but sent no answer` | Alles in Ordnung, das Modell hat nur zu lange gebraucht. Hier ist ein höheres `TimeoutSeconds` die richtige Antwort. |

Ein Zertifikatshinweis, der oft Zeit kostet: NodePilot prüft gegen den Zertifikatsspeicher der
**Maschine**, nicht den des angemeldeten Benutzers. Ein internes Zertifikat, das der Browser auf
dem Arbeitsplatz akzeptiert, muss auf dem NodePilot-Server unter den vertrauenswürdigen
Stammzertifizierungsstellen des Computers liegen.

### Anfrageformat (ergibt sich aus der Base-URL)

OpenAI betreibt zwei Anfrageformate nebeneinander: das klassische **Chat Completions** und die
neuere **Responses-API**. Einzelne Modelle sind ausschließlich über die Responses-API erreichbar.
NodePilot beherrscht beide und erkennt am Pfad der Base-URL, welches gemeint ist — ein eigener
Schalter dafür ist bewusst nicht nötig:

| Base-URL endet auf | Verwendetes Format | Aufgerufene Adresse |
|---|---|---|
| `/responses` | Responses-API | genau diese Adresse |
| `/chat/completions` | Chat Completions | genau diese Adresse |
| alles andere (z. B. `…/v1`) | Chat Completions | Base-URL + `/chat/completions` |

Die Erkennung ignoriert Groß-/Kleinschreibung und abschließende Schrägstriche. Lokale Runtimes wie
Ollama, LM Studio, vLLM, LocalAI oder llama.cpp verstehen ausschließlich Chat Completions.

Bei der Responses-API sendet NodePilot immer die Anweisung, die Anfrage **nicht** beim Anbieter zu
speichern. Ohne diese Anweisung würde OpenAI dort standardmäßig jede Anfrage 30 Tage aufbewahren,
während Chat Completions nichts speichert.

Profile werden am besten unter **Einstellungen → System → Integrationen → LLM** angelegt. Dort angelegte Profile lassen sich vollständig verwalten. Ein Profil, das zusätzlich in einer Basis-Konfigurationsdatei oder in Umgebungsvariablen definiert ist, kann in der Oberfläche zwar bearbeitet, aber nicht gelöscht werden — es würde beim nächsten Neuladen der Konfiguration wieder erscheinen. Solche Profile sind in der Oberfläche entsprechend gekennzeichnet.

Der API-Schlüssel sollte über die Umgebungsvariable `Llm__Profiles__<Kennung>__ApiKey` oder einen Secret-Provider gesetzt werden. Ein Klartextwert in der Konfigurationsdatei erzeugt eine Sicherheitswarnung.

Tool-Calling ist eine Eigenschaft des Modells und wird deshalb je Profil eingestellt. Es setzt ein Modell mit zuverlässiger Function-Calling-Unterstützung voraus und ist erforderlich, damit der globale AI-Chat die aktivierten Wissensquellen abfragen kann.

Die LLM-Verbindung kann je Profil in den administrativen Einstellungen getestet werden. Änderungen an der `Llm`-Sektion werden ohne Dienstneustart wirksam. Ist die AI aktiviert, aber kein Profil ausgewählt, antworten alle AI-Endpunkte mit `503 LLM_NO_ACTIVE_PROFILE`; der Dienst startet trotzdem normal.

## Globalen AI-Chat konfigurieren

Der globale AI-Chat besitzt einen eigenen Schalter und eigene Wissensquellen:

```json
{
  "AiKnowledge": {
    "Enabled": false,
    "DocsEnabled": true,
    "OperationalEnabled": true,
    "SourceCodeEnabled": false,
    "DbEnabled": false
  }
}
```

Für einen funktionsfähigen globalen AI-Chat müssen folgende Einstellungen aktiv sein:

```text
Llm:Enabled = true
Llm:ActiveProfileId = <Kennung eines vorhandenen Profils>
Llm:Profiles:<Kennung>:EnableToolCalling = true
AiKnowledge:Enabled = true
```

Die einzelnen Quellen können unter **Einstellungen → AI-Wissen** unabhängig aktiviert werden. Dokumentation sowie Workflows und Betrieb sind standardmäßig als Quellen vorgesehen. Quellcode und Datenbank sind aus Sicherheitsgründen standardmäßig deaktiviert.

Optional lassen sich eigene Wurzelverzeichnisse für Dokumentation und Quellcode sowie Grenzen für Dateigröße und Trefferzahl setzen. Ohne eigene Pfade verwendet NodePilot die mit der Installation ausgelieferten Wissensverzeichnisse.
