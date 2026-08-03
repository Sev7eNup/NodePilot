# NodePilot Server Setup (`NodePilot-Server-Setup-<version>.exe`)

GUI-Installer für die **Server-Installation** (Windows-Dienst). Er ersetzt den ZIP-Weg nicht — er
ist ein zweiter Weg zur selben Installation. `deploy/Install-NodePilot.ps1` bleibt unverändert
nutzbar und ist weiterhin die Referenz; das Setup ruft genau dieses Skript auf.

> Für **eine einzelne Maschine ohne Netzwerkzugriff** ist die Desktop-App der schnellere Weg —
> siehe [`../desktop/README.md`](../desktop/README.md). Sie bindet ausschließlich Loopback.

## Was er abnimmt — und was nicht

| | |
|---|---|
| **Nimmt ab** | Fünf Release-Assets herunterladen, Prüfsumme vergleichen, Signer-Thumbprint out-of-band abgleichen, `.cer` nach `LocalMachine\Root` importieren, neun Parameter fehlerfrei tippen. Ein Asset, ein Doppelklick. |
| **Nimmt ab (opt-in)** | ASP.NET-Core-Runtime installieren, SQL-Login und Datenbank anlegen, selbstsigniertes Kestrel-Zertifikat erzeugen, Publisher-Zertifikat vertrauen. |
| **Nimmt nicht ab** | gMSA anlegen (AD-Aufgabe), PostgreSQL-Rolle anlegen (kein PG-Client im Payload), TLS für die Datenbank, Kerberos-Delegation, AV-Ausschlüsse. |

Die **Readiness-Seite** prüft alles davon *bevor* etwas verändert wird und zeigt pro Zeile grün,
gelb oder rot mit kopierbarer Anleitung. Rote Pflicht-Zeilen blockieren „Weiter" — der Installer
würde ohnehin abbrechen, und ein Wizard, der einen in ein garantiertes Scheitern hineinführt, ist
schlechter als einer, der stoppt.

## Architektur

Der Pascal-Layer ist bewusst **dünn**: Seiten, Payload, `Exec`, INI lesen. Keine Installationslogik.

```
Wizard-Seiten  ->  answers.json  ->  Invoke-NodePilotSetup.ps1  ->  Install-NodePilot.ps1
(NodePilotServer.iss)  (ACL-geschützt)        (Adapter)            (unverändert)
```

Warum eine Datei statt einer Kommandozeile — drei Gründe, jeder für sich ausreichend:

1. `-PostgresPassword` ist ein `[SecureString]` und kann über `powershell.exe -File` **gar nicht**
   übergeben werden.
2. `/SILENT /ANSWERFILE=` fällt damit für SCCM/GPO ab, ohne zweiten Codepfad.
3. Inno-Pascal hat keine Unit-Test-Story. Was in PowerShell liegt, ist testbar
   ([`../Test-SetupAdapter.ps1`](../Test-SetupAdapter.ps1), 30 Assertions).

Ergebnisse kommen als **INI** zurück, nicht als JSON: Inno hat `GetIniString` eingebaut und für JSON
gar nichts — ein Parser in Pascal wären ~120 Zeilen, die kein Test erreicht.

## Unbeaufsichtigt (SCCM, GPO)

```powershell
NodePilot-Server-Setup-1.0.1.exe /VERYSILENT /SUPPRESSMSGBOXES /ANSWERFILE=C:\prod\answers.json
```

| Schalter | Wirkung |
|---|---|
| `/ANSWERFILE=<pfad>` | Antworten aus einer Datei statt aus den Seiten. Sie gewinnt über alles. |
| `/FULLREINSTALL` | Erzwingt Neuaufsetzen statt Update. **Erzeugt einen neuen External-Trigger-API-Key** — der alte ist nicht rekonstruierbar. |
| `/LOG=<pfad>` | Inno-Log. Der Adapter schreibt zusätzlich `%TEMP%\nodepilot-server-setup.log`. |

Die übergebene Answer-File wird **kopiert**, nicht in-place benutzt: die Kopie erbt die restriktive
DACL des Session-Verzeichnisses und wird am Ende geschreddert. Das Original bleibt unangetastet.

### Answer-File

`schemaVersion: 1`. Unbekannte Schlüssel und fehlende Pflichtschlüssel werden **namentlich**
abgelehnt — strenger als PowerShells Binding, weil ein Tippfehler in einer SCCM-Datei sonst mitten
in der Installation zuschlägt.

```json
{
  "schemaVersion": 1,
  "mode": "install",
  "installPath": "C:\\Program Files\\NodePilot",
  "dataPath": "C:\\ProgramData\\NodePilot",
  "serviceName": "NodePilot",
  "identity": { "type": "gmsa", "account": "CONTOSO\\svc-nodepilot$" },
  "database": {
    "provider": "sqlserver",
    "sqlServer": "sql01.contoso.local",
    "sqlDatabase": "NodePilot",
    "sqlCertificateHostName": ""
  },
  "network": {
    "publicHostname": "nodepilot.contoso.local",
    "httpsPort": 443, "httpPort": 80,
    "allowedHosts": "nodepilot.contoso.local", "knownProxyIps": []
  },
  "certificate": { "thumbprint": "A1B2...", "source": "existing" }
}
```

`identity.type` ist `localSystem` oder `gmsa` (dann ist `identity.account` Pflicht).
`database.provider` ist `sqlserver` (dann `sqlServer` + `sqlDatabase`) oder `postgres` (dann
`postgresHost`, `postgresDatabase`, `postgresUser`, `postgresPassword`, `postgresRootCertificate`).
Für `"mode": "update"` genügen `installPath` und `serviceName`; jeder weitere Schlüssel wird
abgelehnt, damit eine veraltete Datei nicht halb angewendet wird.

**Das Passwort steht im Klartext in der Datei.** Geschützt ist sie über die DACL ihres
Verzeichnisses (SYSTEM + Administratoren + installierender Benutzer, atomar beim Anlegen gesetzt).
Ein lokaler Administrator kann sie während der Installation lesen — dieselbe Lesergruppe, die auch
den dauerhaften Aufbewahrungsort des Secrets lesen kann
(`HKLM\SYSTEM\CurrentControlSet\Services\NodePilot\Environment`). Die Answer-File eröffnet **keine
neue Angreiferklasse**. Deine eigene Vorlage solltest du trotzdem so behandeln wie jede andere
Datei mit einem Produktionspasswort.

### Exit-Codes

| Code | Bedeutung |
|---|---|
| 0 | Erfolg |
| 3 | Vorbereitung fehlgeschlagen (Inno) |
| 7 | Installation fehlgeschlagen — Meldung steht im Log, der Installer hat bereits zurückgerollt |

Adapter-intern (im Log sichtbar): 2 = Readiness rot, 3 = Answer-File ungültig, 4 = Installation
gescheitert, 1 = Adapter-Absturz.

## Update

Ein erneuter Lauf erkennt die vorhandene Installation über `HKLM\SOFTWARE\NodePilot\Server` —
**auch eine, die per ZIP installiert wurde** — und fährt per Default die
`Update-NodePilot.ps1`-Semantik: nur Binaries, `appsettings.Production.json` bleibt, Datenbank und
Dienstidentität unangetastet, Rollback bei Fehler, Dienst läuft danach.

## Deinstallation

Entfernt **alles, was dieses Setup installiert hat**: Windows-Dienst, Dienst-Binaries,
Firewall-Regeln, Installations-Marker, Registry-Environment (inklusive des dort liegenden
Postgres-Passworts) und den Uninstall-Eintrag.

Genau eine Frage wird gestellt: **Datenverzeichnis behalten?** (`C:\ProgramData\NodePilot` — Logs,
JWT-Signaturschlüssel, Data-Protection-Keyring). Default ist **behalten**, überall: interaktiv,
`/SILENT` ohne Schalter, „Apps & Features" und beim Aufruf durch Inno selbst.

```powershell
"C:\Program Files\NodePilot\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES              # Daten behalten
"C:\Program Files\NodePilot\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /PURGEDATA=1 # Daten löschen
```

**Die Datenbank wird nie entfernt, und es gibt dafür keine Option.** Dieses Setup legt sie nicht an
— sie wurde separat bereitgestellt, hat oft ein eigenes Backup-, Replikations- und
Aufbewahrungsregime, und in einem Active/Passive-Cluster teilen sich **beide Knoten dieselbe
Datenbank**. Was man nie installiert hat, entfernt man nicht. Der Wizard sagt das in der Abfrage
ausdrücklich, statt zu schweigen, und ein Vertragstest verhindert, dass die Fähigkeit
zurückkommt.

Ebenfalls bewusst stehen bleiben: das „Log on as a service"-Recht des gMSA und die Lese-ACE auf dem
Private Key des TLS-Zertifikats — beide können mit einem anderen Dienst geteilt sein. Der
Uninstaller **benennt** sie am Ende namentlich.

## Bauen

```powershell
# Einzeln:
.\deploy\server\Build-ServerInstaller.ps1 `
    -ArtifactPath .\out\NodePilot-1.0.1.zip `
    -TrustedSignerThumbprint 277EAB317A581C88302CE92BE805938C86B4650D

# Als Teil des Release-Builds (empfohlen — signiert und in SHA256SUMS):
.\deploy\Build-Artifact.ps1 -SigningCertificateThumbprint $tp `
    -IncludeServerInstaller -InstallerSigningCertificateThumbprint $tp
```

Voraussetzungen: Inno Setup 6, ein **signiertes** Artefakt (das Setup verifiziert es zur
Installationszeit, ein `-AllowUnsignedDevelopmentArtifact`-Build wird übersprungen), und Netzwerk
beim ersten Lauf für den Runtime-Download.

Die ASP.NET-Core-Runtime wird zur Bauzeit geholt und dreifach geprüft: gegen den in Microsofts
Release-Metadaten publizierten **SHA512**, gegen den eingecheckten Pin in
[`runtime-payload.lock.json`](runtime-payload.lock.json), und per Authenticode auf „Microsoft
Corporation". Es ist die **Standalone-Runtime**, nie das Hosting Bundle — das verdrahtet IIS und
startet W3SVC neu, was auf geteilten Hosts unerwünscht ist. Nichts davon wird eingecheckt außer
dem Pin; der macht das Payload reproduzierbar und jede Änderung daran reviewpflichtig.

Größe: ~52 MB (Desktop-Installer: ~176 MB — kein Electron, kein gebündeltes PostgreSQL).

## Inno-Setup-Fallen, die hier gemessen wurden

Alle sieben sind auf echter Infrastruktur aufgetreten, nicht aus der Doku abgeleitet:

1. **`ssPostInstall` kann kein Scheitern melden.** Weder `RaiseException` noch `Abort` ändert dort
   den Exit-Code — ein gescheiterter Lauf meldet 0. Unter SCCM wäre das eine Verteilung, die Erfolg
   meldet und nichts installiert hat. Die Installation läuft deshalb in `PrepareToInstall`
   (Rückgabe einer Meldung, Exit 7).
2. **`[UninstallRun]` wertet `{code:…}` zur Installationszeit aus** und friert das Ergebnis in
   `unins000.dat` ein. Eine zur Deinstallationszeit getroffene Entscheidung kann darüber nie
   ankommen. Der Abschnitt existiert nicht; der Aufruf läuft aus `[Code]`.
3. **`[Run]` kann keine Exit-Codes prüfen.** Existiert ebenfalls nicht; ein Vertragstest verbietet
   beide Abschnitte.
4. **Inno dedupliziert identische Quelldateien.** Ein `dontcopy`-Eintrag und ein
   `DestDir`-Eintrag auf dieselbe Datei kollabieren zu einem; die `dontcopy`-Variante verschwindet
   still. Deshalb zwei getrennte Staging-Bäume (`payload\` und `deploy\`).
5. **`{app}` existiert während des Wizards noch nicht.** Readiness-Seite und `PrepareToInstall`
   laufen vor dem Kopieren — alles zur Laufzeit Benötigte ist `dontcopy` und liegt in `{tmp}`.
6. **Kein `SaveStringToUTF8File` in dieser Version**, nur `SaveStringsToUTF8File`
   (`TArrayOfString`). Die AnsiString-Variante würde ein Passwort mit Umlauten in der
   System-Codepage schreiben, was der Adapter dann ablehnt. Und `LoadStringFromFile` liefert
   AnsiString — der Session-Pfad liegt deshalb unter `%ProgramData%` (garantiert ASCII), und ein
   BOM wird beidseitig entfernt.
7. **Keine Zeile in `[Code]` darf mit `#` beginnen** — der ISPP-Präprozessor liest das als
   Direktive und bricht mit „Unknown preprocessor directive" ab. Trifft umgebrochene
   `#13#10`-Fortsetzungen.

Und eine außerhalb von Inno, aber gleich teuer: **`icacls /grant '<SID>:(OI)(CI)F'` auf eine
Blattdatei meldet Erfolg und fügt keine ACE hinzu.** `(OI)`/`(CI)` sind Container-Vererbungsflags
und werden dort verworfen. Ohne sie funktioniert es. Betrifft `-PurgeData`, das sonst an dem
owner-only `jwt-secret.key` scheitert.

## Testabdeckung — und was fehlt

**Automatisiert** (CI, beide PowerShell-Versionen):
[`../Test-SetupAdapter.ps1`](../Test-SetupAdapter.ps1) prüft das Answer-File-Verhalten
verhaltensbasiert (Torture-Round-Trip, Schema-Ablehnung mit Schlüsselnamen, Splat-Trennung pro
Provider, SecureString, INI-Escaping, die Zweischichtigkeit des Pre-Flights).
[`../Test-DeploymentTemplates.ps1`](../Test-DeploymentTemplates.ps1) pinnt statisch, was am
`.iss`, am Adapter, am Runtime-Fetch und am Build prüfbar ist — jeder Vertrag mutationsgeprüft.

**Nicht automatisiert, ehrlich benannt:**

- **Der Pascal-Code selbst.** Seitenfluss, `ShouldSkipPage`-Matrix, JSON-Escaper, INI-Lesen,
  Steuerelement-Zustände. Inno-Pascal hat dafür kein Werkzeug. Gegenmittel: minimaler Umfang plus
  die Verträge oben.
- **Die GUI wurde nie geklickt.** Alle Lab-Läufe waren unbeaufsichtigt. Der interaktive Pfad —
  Readiness-Ampel, Auto-Fix-Checkboxen, Zertifikatsauswahl — ist ungetestet.
- **Nur SQL Server + gMSA getestet.** Der PostgreSQL-Pfad und der LocalSystem-Pfad sind im Lab nie
  gelaufen.
- **Nur Windows Server 2025.** Server 2022 (die `MinVersion`) ist ungetestet.

### Manuelle Smoke-Matrix vor jedem Release

| # | Fall | Erwartung |
|---|---|---|
| 1 | Frisch, SQL Server + LocalSystem | Dienst läuft, `/healthz/ready` 200 |
| 2 | Frisch, PostgreSQL + gMSA | dito |
| 3 | Erneuter Lauf über Bestand | Update-Semantik, Config bleibt |
| 4 | `/FULLREINSTALL` | Bestätigungsdialog erscheint, neuer API-Key |
| 5 | `/SILENT /ANSWERFILE` | Exit 0, Dienst läuft |
| 6 | Runtime fehlt, Angebot abgelehnt | „Weiter" bleibt gesperrt |
| 7 | Rote Readiness-Zeile | „Weiter" gesperrt, Anleitung sichtbar |
| 8 | Abbruch mitten im Wizard | kein Session-Verzeichnis bleibt zurück |
| 9 | Deinstallation ohne Schalter | alles weg, Daten und Datenbank bleiben |
| 10 | Deinstallation `/PURGEDATA=1` | zusätzlich Datenverzeichnis weg, Datenbank bleibt |

Stand: 1, 3, 5, 9 und 10 sind im Hyper-V-Lab gegen echtes AD, echte gMSA und SQL Server 2022 CU
gelaufen. 2, 4, 6, 7, 8 nicht.
