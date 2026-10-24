# NodePilot KI-Features

NodePilot unterstützt die Erstellung von Skripten und Workflows sowie die Analyse
vorhandener Abläufe. Die KI ist standardmäßig deaktiviert und benötigt ein aktives
LLM-Profil. Im Entwicklungsprofil ist `Llm:Enabled` bereits eingeschaltet.

## Funktionen

| Funktion | Verwendung |
|---|---|
| Skriptgenerierung | Im Skripteditor PowerShell aus einer Beschreibung erzeugen und am Cursor einfügen oder das Skript ersetzen. Die Übermittlung des vorhandenen Skripts erfordert pro Aufruf eine ausdrückliche Freigabe. |
| Workflow-Generierung | Auf der Workflow-Übersicht einen Ablauf beschreiben, die Vorschau prüfen und daraus einen Workflow erstellen. |
| Workflow-Assistent | Im Designer den geöffneten Workflow erklären, Fehler untersuchen und Änderungen vorschlagen lassen. Vorschläge lassen sich einzeln übernehmen, verfeinern oder rückgängig machen. |
| Wissens-Assistent | Unter `/ai-chat` oder im Chat-Widget Fragen zu Dokumentation, Konfiguration und Betrieb stellen. Im Designer steht stattdessen der Workflow-Assistent bereit. |
| `llmQuery` | Innerhalb eines Workflows eine Modellanfrage ausführen. |
| `aiAgent` und `aiAgentTeam` | Aufgaben mit ausgewählten Werkzeugen bearbeiten lassen. Einrichtung und Grenzen stehen unter [KI-Agenten](ai-agents.md). |

Alle Rollen dürfen den Workflow-Assistenten zum Erklären verwenden. Generierung und
Übernahme von Vorschlägen sind Admins und Operatoren vorbehalten. Änderungen landen
zunächst im Canvas und durchlaufen anschließend den üblichen Ablauf mit Edit-Lock,
Speichern und Veröffentlichen. Veraltete Vorschläge werden blockiert.

## Konfiguration

Profile werden unter **Settings / System / Integrations / LLM** angelegt und getestet.
`Llm:Enabled=true` aktiviert die Funktionen, `Llm:ActiveProfileId` wählt das verwendete
Profil. Fehlt diese Auswahl oder verweist sie auf kein vorhandenes Profil, antwortet
die KI mit `503 LLM_NO_ACTIVE_PROFILE`. Der Dienst startet weiterhin.

Die Verbindungen liegen unter `Llm:Profiles:<id>`. Frische Installationen enthalten
keine Profile. In der Oberfläche angelegte Profile bleiben vollständig verwaltbar,
während Profile aus der Basis-Konfiguration dort bearbeitet, aber nicht gelöscht werden können.

| Profilschlüssel | Standard | Bedeutung |
|---|---|---|
| `Name` | Leer | Anzeigename. Die Profil-Id bleibt beim Umbenennen erhalten. |
| `BaseUrl` | OpenAI Cloud | Adresse des OpenAI-kompatiblen Endpunkts. |
| `ApiKey` | `null` | Zugangsschlüssel, sofern der Anbieter ihn verlangt. |
| `Model` | `gpt-5.5` | Modell für Generierung und Chats. |
| `MaxTokens` | `128000` | Ausgabelimit, gültig von `256` bis `1000000`. Es muss zum Modell passen und bezeichnet nicht dessen Kontextfenster. |
| `TimeoutSeconds` | `90` | Zeitbudget für die Modellantwort. Der Verbindungsaufbau hat eigene Fristen. |
| `EnableToolCalling` | `false` | Erlaubt den Chats lesende Werkzeuge. Das Modell muss Function Calling unterstützen. |
| `ToolCallMaxDepth` | `6` | Höchstens 1 bis 10 Modellrunden pro Chat-Turn. Die letzte Runde liefert eine Textantwort ohne weitere Werkzeuge. |
| `ShareScriptsWithAssistant` | `true` | Gibt Skripte, SQL und Programmargumente für den Workflow-Assistenten frei. |

API-Schlüssel lassen sich über `Llm__Profiles__<id>__ApiKey` bereitstellen.
Klartextschlüssel in Settings-Dateien lösen beim Start eine Hardening-Warnung aus.
Profil- und Proxyänderungen in der Admin-Oberfläche greifen beim nächsten Aufruf.

### Endpunkt und Verbindungsformat

`BaseUrl` erfordert HTTPS. HTTP ist nur für literale Loopback-Ziele erlaubt, die den
ausgehenden Proxy umgehen. Beispiele für lokale Endpunkte sind
`http://localhost:11434/v1` und `http://localhost:1234/v1`.

| Ende der `BaseUrl` | Verwendung |
|---|---|
| `/responses` | Responses-Anfragen an genau diese URL. |
| `/chat/completions` | Chat-Completions-Anfragen an genau diese URL. |
| Anderer Pfad, etwa `/v1` | Chat Completions mit angehängtem `/chat/completions`. |

URLs mit Query-String werden nicht unterstützt. Alle Profiladressen werden beim
Speichern und Start auf gesperrte Metadata-Ziele geprüft. Bei Responses-Anfragen
setzt NodePilot `store: false`.

### Proxy

`Llm:Proxy` gilt installationsweit für die LLM-Aufrufe. `Mode` wählt `Off` als
Standard, `System` für die Proxykonfiguration des Dienstkontos oder `Custom`.
Bei `Custom` ist `Address` erforderlich. `BypassList` nimmt Hostnamen und Shell-Globs
auf, während bei `System` die Ausnahmen des Betriebssystems gelten.

Für die Anmeldung stehen `Username` und `Password` oder `UseDefaultCredentials=true`
mit der Windows-Identität des Dienstkontos bereit. Das Passwort kann über
`Llm__Proxy__Password` gesetzt werden. Änderungen an den Windows-Proxy-Einstellungen
selbst erfordern einen Dienstneustart. Ein Proxy ersetzt keine fehlende Netzfreigabe.

## Datenfreigabe und Rechte

Der Workflow-Assistent maskiert benannte Secrets vor dem Modellaufruf. Bei aktiviertem
`ShareScriptsWithAssistant` erhält der Anbieter jedoch Skripte, SQL und Argumente,
soweit die Secret-Erkennung sie nicht maskiert. Diese Erkennung ist heuristisch.
Ein Wechsel von Profil, Endpunkt oder Freigabeschalter nimmt den bisherigen
Chatverlauf aus dem nächsten Aufruf.

Die Skriptgenerierung übermittelt das aktuelle Skript nur mit der gesonderten Freigabe
`IncludeCurrentScript=true`. Upstream-Variablen erhält sie als Schema ohne deren Werte.
Generierte Skripte werden vor einer Ausführung im Editor angezeigt. Vorschläge des
Workflow-Assistenten erhalten beim Zusammenführen vorhandene Secrets und Layouts.
Änderungen an maskierten Bedingungen müssen gegebenenfalls manuell erfolgen.

KI-Endpunkte sind auf 20 Anfragen pro Minute und IP begrenzt. Audit-Einträge erfassen
die jeweilige Aktion und Metadaten, aber keinen Prompt-Text. Weitere Vertrauensgrenzen
beschreibt das [Threat Model](threat-model.md).

## Lesende Werkzeuge und Wissensquellen

Mit `EnableToolCalling=true` kann der Workflow-Assistent den Ablauf analysieren,
Activity-Typen nachschlagen und Ausführungsdetails zur Fehlersuche lesen.
Ausführungshistorie ist nur bei gespeicherten Workflows mit entsprechendem
Folder-Read-Recht verfügbar. Ergebnisse werden maskiert und in ihrem Umfang begrenzt.

Der globale Wissens-Assistent benötigt zusätzlich `AiKnowledge:Enabled`.
Die Quellen werden in der Sektion `AiKnowledge` verwaltet:

| Quelle | Einstellung | Zugriff |
|---|---|---|
| Dokumentation | `DocsEnabled`, standardmäßig an | Dokumentationssuche. |
| Workflows und Betrieb | `OperationalEnabled`, standardmäßig an | Innerhalb der Ordnerberechtigungen. |
| Quellcode | `SourceCodeEnabled`, standardmäßig aus | Admin und Operator. |
| Datenbank / text2sql | `DbEnabled`, standardmäßig aus | Ausschließlich globale Admins, zusätzlich `EnableToolCalling=true`. |
| Systemkonfiguration | Für berechtigte Rollen verfügbar | Admin und Operator. |

Bei text2sql übersetzt das Modell eine Frage in SQL, das NodePilot lesend prüft und
ausführt. Geschützte Spalten sind gesperrt, Ergebnisse maskiert und auf 200 Zeilen
begrenzt. Auch Bestandslisten zu Workflows, Läufen oder Maschinen verwenden diese
DB-Quelle. Ordnerrechte erweitern den Zugriff nicht auf SQL. Der Wissens-Assistent
ändert keine Workflows und startet keine Reparaturen.

Der gemeinsame SQL-Guard für Wissensassistent und Admin-Konsole lehnt im Lesemodus
PostgreSQL-Escape-Strings (`E'…'`/`e'…'`) vor der Ausführung ausdrücklich ab.
Standard-SQL-Literale mit verdoppelten Apostrophen bleiben unterstützt. Die abweichende
[PostgreSQL-Escape-Syntax](https://www.postgresql.org/docs/current/sql-syntax-lexical.html#SQL-SYNTAX-STRINGS-ESCAPE)
wird vom verwendeten Lexer nicht interpretiert.

## Fehlerbehandlung

| HTTP | Code | Ursache |
|---|---|---|
| 503 | `LLM_DISABLED` | KI deaktiviert. |
| 503 | `LLM_NO_ACTIVE_PROFILE` | Kein gültiges aktives Profil. |
| 503 | `LLM_UNREACHABLE` | Endpunkt nicht erreichbar. |
| 503 | `LLM_TIMEOUT` | Zeitbudget der Modellantwort überschritten. |
| 503 | `LLM_UNAUTHORIZED` | Zugangsschlüssel fehlt oder wird abgelehnt. |
| 503 | `LLM_RATE_LIMITED` | Anbieter drosselt Anfragen. |
| 502 | `LLM_MALFORMED_RESPONSE` | Unbrauchbares Antwortformat. |
| 502 | `LLM_UPSTREAM_ERROR` | Fehler des Anbieters oder der Übertragung. |
| 429 | Kein eigener Code | Lokales Anfragelimit erreicht. |

Verbindungsfehler nennen die betroffene Stufe. DNS und TCP haben jeweils 15 Sekunden,
der TLS-Handshake 30 Sekunden Zeit. `TimeoutSeconds` betrifft die anschließende
Modellantwort. Zertifikate werden gegen den Maschinen-Trust-Store geprüft, weshalb
eine interne CA auf dem NodePilot-Host unter `LocalMachine\Root` vorhanden sein muss.
Details zu Adressen und Verbindungsdauer protokolliert `NodePilot.Ai.LlmConnect` auf `Debug`.
