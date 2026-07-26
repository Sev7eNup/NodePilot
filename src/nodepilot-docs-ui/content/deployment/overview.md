# Betriebsarten im Überblick

NodePilot lässt sich auf drei Arten betreiben. Sie unterscheiden sich nicht im Funktionsumfang der Workflow-Engine, sondern darin, **wer darauf zugreifen kann**, **wie viel man selbst installieren muss** und **welche Rechte** die Ausführung hat.

| | **Dev** | **Server-Deployment** | **Desktop-App** |
|---|---|---|---|
| Zweck | Entwickeln am Produkt | Produktivbetrieb im Team | Einzelplatz, sofort startklar |
| Installation | manuell (Postgres, Backend, Frontend einzeln) | signiertes Artefakt + `deploy/`-Skripte | ein `.exe`-Installer |
| Voraussetzungen | .NET-SDK, Node, Postgres | .NET-Hosting-Bundle, **externe DB**, TLS-Zertifikat, ggf. AD/gMSA | **keine** |
| Datenbank | lokales Dev-Cluster | extern (SQL Server 2022 / PostgreSQL 16+) | mitgeliefertes PostgreSQL 16 |
| Erreichbar für | nur dich | **alle im Netz** | **nur diese eine Maschine** |
| Start | `dotnet run` + `npm run dev` | Windows-Dienst (Autostart) | Windows-Dienste + Electron-Fenster |
| Doku | [Installation](../getting-started/installation) | [Produktions-Rollout](./production) | [Desktop-App](./desktop) |

## Wann welche Betriebsart?

- **Dev** — nur zum Entwickeln. Hot-Reload im Frontend, Backend auf Port 5000, SPA auf 5173. Kein Dienst, kein Autostart.
- **Server-Deployment** — sobald **mehrere Personen** damit arbeiten, Workflows **von außen** angestoßen werden (Webhooks, Fremdsysteme) oder Ausfallsicherheit zählt.
- **Desktop-App** — wenn **eine Person auf einer Maschine** automatisieren will und man weder eine Datenbank bereitstellen noch PKI verwalten möchte. Beides *gibt* es trotzdem: gebündeltes PostgreSQL 16 und ein self-signed Loopback-Zertifikat, beide vom Installer erzeugt und nie von Hand gepflegt. Der Preis dafür: kein systemweiter Trust, ein normaler Browser **darf** auf der URL warnen — unterstützter Zugang ist die Electron-Shell (siehe [Desktop-App](./desktop)).

## Server vs. Desktop — was im Alltag wirklich anders ist

Beide sind Produktivbetrieb mit denselben Härtungs-Defaults. Die Desktop-Variante setzt jedoch `Deployment:Mode=Desktop`, und daraus folgen Einschränkungen, die man vorher kennen sollte.

### Erreichbarkeit — der wichtigste Unterschied

> **Merksatz:** Alles, was NodePilot **selbst anstößt**, funktioniert. Alles, was NodePilot **von außen anstoßen soll**, nicht.

Im Desktop-Modus bindet Kestrel **ausschließlich Loopback** (`ListenLocalhost` statt `ListenAnyIP`). Das ist fest verdrahtet, nicht per Konfiguration abschaltbar, und der Installer legt keine Firewall-Regel an.

**Wichtig — betroffen ist der komplette Listener, nicht einzelne Endpunkte.** Es ist *nicht* so, dass die Trigger-Route gesperrt wäre und der Rest der API weiterhin aus dem Netz erreichbar bliebe: Es lauscht schlicht nichts auf der Netzwerkkarte. Gleichermaßen nur lokal erreichbar sind damit:

- die Oberfläche (SPA)
- **`/api/*` — jeder REST-Endpunkt**, nicht nur Trigger
- `/hubs/execution` (SignalR)
- `/api/webhooks/*`
- `/healthz`

Zusätzlich stehen `AllowedHosts: "localhost;127.0.0.1"` und Host-Filtering davor. Dass sich keine Kollegen anmelden können, folgt also nicht aus Auth oder Lizenz, sondern aus genau dieser einen Ursache.

#### Was das für Trigger bedeutet

| Trigger | Desktop | Warum |
|---|---|---|
| `scheduleTrigger` | ✅ | läuft im Dienst, braucht niemanden von außen |
| `fileWatcherTrigger` | ✅ | lokales Dateisystem |
| `databaseTrigger` | ✅ | NodePilot pollt die DB selbst |
| `eventLogTrigger` | ✅ | lokales Windows-Eventlog |
| `manualTrigger` | ✅ | aus der App heraus |
| `webhookTrigger` | ❌ | konfigurierbar, aber kein Fremdsystem kann zustellen |
| `POST /api/trigger/{name}` | ❌ | nicht erreichbar **und** per leerem `ExternalTrigger:ApiKey` deaktiviert |

#### Was ausgehend unverändert funktioniert

Die Richtungsbeschränkung gilt nur für eingehende Verbindungen. NodePilot kann aus der Desktop-Variante heraus die ganze Umgebung steuern — hier gibt es **keinen** Unterschied zum Server-Deployment:

- **Remote-Maschinen per WinRM** automatisieren (mit hinterlegten Credentials pro Maschine)
- **REST-APIs aufrufen** (`restApi`, `waitForCondition`)
- **Datenbanken abfragen** (`sql`)
- **E-Mails und Alerting-Webhooks versenden**

Wer Workflows von außen anstoßen lassen will oder Team-Zugriff braucht, braucht das Server-Deployment.

### Weitere Unterschiede

| Thema | Server | Desktop |
|---|---|---|
| **Anmeldung** | LDAP, Windows-SSO, OIDC, SCIM möglich; lokale Passwörter nur als Break-Glass | **nur lokales Login** (kein Verzeichnisdienst vorausgesetzt) |
| **Dienstkonto** | gMSA (least privilege) oder LocalSystem | fest **LocalSystem** |
| **Lokale `runScript`** | unter dem gewählten Dienstkonto | **als SYSTEM** — bewusste v1-Entscheidung |
| **Remote-WinRM** | Kerberos ohne gespeicherte Credentials möglich (Delegation nötig) | **hinterlegte Credentials pro Maschine** nötig |
| **TLS** | echtes Zertifikat, keine Warnung | selbstsigniert, nur in Electron per Pinning vertraut → **Browser warnt** |
| **Hochverfügbarkeit** | Active/Passive-Cluster möglich | **nicht möglich** |
| **Parallelität** | auf Server-Last ausgelegt (z. B. 600 gleichzeitige Steps) | bewusst klein (**32** Steps) |
| **DB-Backup beim Update** | **keins** — separat einrichten | **automatischer `pg_dump`** vor jedem Update |
| **Deinstallation** | DB liegt extern und bleibt in jedem Fall | DB ist Teil der Installation; bleibt erhalten, außer man löscht `ProgramData` bewusst |

### Vorteile der Desktop-App

- **Nichts vorzubereiten**: kein .NET, keine Datenbank, kein Zertifikat, kein AD — offline installierbar.
- **Selbstheilend beim Update**: automatischer Datenbank-Dump vor jedem Update, Rollback inklusive.
- **Gehärteter Client**: die Electron-Shell pinnt das Zertifikat, blockiert Navigation nach außen, Downloads und Berechtigungsanfragen und reicht dem SPA-Fenster keinerlei System-Schnittstelle durch.
- **Läuft im Hintergrund weiter**: Fenster schließen beendet nichts — Zeitpläne feuern weiter, weil API und Datenbank Dienste sind.

### Nachteile der Desktop-App

Es sind genau fünf — die Workflow-Engine selbst ist funktional identisch (gleiche Activities, gleiche Datenbank, gleiche Härtung):

1. **Nur Loopback** — kein Team-Zugriff, keine eingehenden Webhooks, keine externe Trigger-API.
2. **Nur lokales Login** — kein LDAP, kein Windows-SSO, kein OIDC/SCIM.
3. **Lokale Skripte laufen als SYSTEM** — der einzige Punkt, der die *Sicherheitslage* verändert und nicht bloß den Funktionsumfang.
4. **Kein Cluster** — keine Hochverfügbarkeit, kein Failover.
5. **Kleineres Tuning** — 32 statt 600 parallele Steps.

Dazu zwei Punkte, die eher Bedienung als Funktion betreffen: **Browserzugriff warnt** (die Electron-App ist der vorgesehene Weg), und **Daten sind maschinengebunden** (siehe unten).
- **Daten hängen an der Maschine**: Credentials sind per DPAPI maschinengebunden. Ein Umzug auf einen anderen Rechner geht nur über das [System-Backup](../import-export), nicht durch Kopieren des Datenverzeichnisses.

## Wechsel zwischen den Betriebsarten

Es gibt keinen Migrationspfad „in place" — die Betriebsarten unterscheiden sich in Dienstkonto, Datenbank und Zertifikat. Der unterstützte Weg ist das **System-Configuration-Backup**: im Quellsystem exportieren, im Zielsystem einspielen. Secrets werden dabei mit dem Zielprovider neu verschlüsselt, funktionieren also auch auf anderer Hardware. **Nicht enthalten** sind Ausführungshistorie, Audit-Log und Statistiken — dafür braucht es ein echtes Datenbank-Backup.
