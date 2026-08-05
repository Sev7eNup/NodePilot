# NodePilot Server Setup (`NodePilot-Server-Setup-<version>.exe`)

GUI-Installer für die **Server-Installation** (Windows-Dienst). Er ersetzt den ZIP-Weg nicht — er
ist ein zweiter Weg zur selben Installation. `deploy/Install-NodePilot.ps1` bleibt unverändert
nutzbar und ist weiterhin die Referenz; das Setup ruft genau dieses Skript auf.

> Für **eine einzelne Maschine ohne Netzwerkzugriff** ist die Desktop-App der schnellere Weg —
> siehe [`../desktop/README.md`](../desktop/README.md). Sie bindet ausschließlich Loopback.

## Was er abnimmt — und was nicht

| | |
|---|---|
| **Nimmt ab** | Fünf Release-Assets herunterladen, Prüfsumme vergleichen, Signer-Thumbprint out-of-band abgleichen, `.cer` nach `LocalMachine\Root` importieren, neun Parameter fehlerfrei tippen, den Kestrel-Thumbprint aus der Zertifikats-MMC heraussuchen. Ein Asset, ein Doppelklick. |
| **Nimmt ab (opt-in)** | ASP.NET-Core-Runtime installieren, SQL-Login und Datenbank anlegen, selbstsigniertes Kestrel-Zertifikat erzeugen, Publisher-Zertifikat vertrauen. |
| **Nimmt nicht ab** | gMSA anlegen (AD-Aufgabe), PostgreSQL-Rolle anlegen (kein PG-Client im Payload), TLS für die Datenbank, Kerberos-Delegation, AV-Ausschlüsse. |

Die **Readiness-Seite** prüft alles davon *bevor* etwas verändert wird — neun Zeilen: .NET-Runtime,
Kestrel-Zertifikat, **HTTP/HTTPS-Ports**, gMSA, Dienstidentität, Domänenmitgliedschaft,
DB-Erreichbarkeit, DB-Version, DB-Login. Jede Zeile trägt rechts ein
Statuszeichen — Haken, Kreuz, Ausrufezeichen oder Gedankenstrich — und ist zusätzlich eingefärbt.
Das Zeichen ist nicht Dekoration: Farbe allein sagt niemandem etwas, der dieses Grün nicht von
diesem Rot unterscheidet, und in einem Screenshot in einem Ticket schon gar nicht. Rote
Pflicht-Zeilen blockieren „Weiter" — der Installer würde ohnehin abbrechen, und ein Wizard, der
einen in ein garantiertes Scheitern hineinführt, ist schlechter als einer, der stoppt.

Ein Klick auf eine Zeile zeigt die zugehörige Anleitung darunter. Das ist ein **Label**, kein
Eingabefeld: als `TNewMemo` blieb dafür nach acht Prüfzeilen genau eine Zeile Höhe übrig, mitsamt
Scrollleiste — das sah aus wie ein kaputtes Textfeld. Der Preis ist, dass der Text nicht mehr
markierbar ist; Inno-Pascal hat keine Clipboard-API, deshalb bleibt „In Datei speichern…" der Weg,
die Anleitung aus dem Wizard herauszubekommen.

Die Zeilen werden erst positioniert, wenn ihr Text steht. Vorher reservierte jede der acht
Zeilen 16 px für eine Auto-Fix-Checkbox, die fast nie sichtbar ist — 128 px einer 309 px hohen
Fläche für nichts.

## Fortschrittsanzeige

Während der Installation zeigt der Wizard Phase und Balken. Vorher stand er auf „Preparing to
Install" und zeigte **nichts** — gemessen 136 s bei einem erfolgreichen Lauf, 187 s bei einem, der
in den Health-Probe-Timeout läuft. Lange genug, dass Windows das Fenster ausgraut und „Keine
Rückmeldung" danebenschreibt; genau so wurde es auch gelesen.

Ursache war `Exec` mit `ewWaitUntilTerminated`: synchron, blockiert Innos UI-Thread vollständig.
**Nur die Installation** läuft deshalb jetzt detached (`ewNoWait`); Probe, Provision, Certificates
und Cleanup bleiben synchron, die sind in Sekunden durch.

Vier Punkte, die daran nicht offensichtlich sind:

- **Der Exit-Code kommt aus `result.ini`, nicht von `Exec`.** Mit `ewNoWait` gibt es keinen — der
  Prozess läuft ja noch. Der Adapter schreibt die Datei in einem `finally`, sie existiert also auch
  auf den Rollback-Pfaden. Geprüft wird nicht ihre Existenz, sondern ob `summary.exitCode` darin
  steht: `WriteAllLines` ist nicht atomar, die Datei kann da und halb geschrieben sein.
- **Inno hat keinen Message-Pump.** `AppProcessMessages`, `ProcessMessages` und `Application` sind
  in 6.7.3 allesamt unbekannte Bezeichner (nachgemessen). Die Schleife ruft deshalb pro Tick
  `ProgressPage.SetProgress` — das ist der Mechanismus, den Inno für lange Operationen vorsieht.
- **Der Fortschritt entsteht aus der Ausgabe der Installer-Skripte**, nicht aus neuen Meldungen in
  ihnen. `Install-NodePilot.ps1` (10 Phasen) und `Update-NodePilot.ps1` (4) sind unverändert; der
  Adapter übersetzt ihre `Write-Step`-Zeilen im Vorbeigehen in `percent|text`. Zugeordnet wird per
  **Präfix**, weil mehrere Überschriften einen Wert einbetten (`Stopping service '$ServiceName'`).
  Das ist sicher, weil `Write-Step` bündig schreibt und `Write-Info` einrückt — eine Detailzeile
  beginnt mit Leerzeichen und kann keinen Phasennamen präfixieren.
- **Der Drift-Contract läuft in beide Richtungen.** Jeder Tabelleneintrag muss im Skript existieren
  *und* jede `Write-Step` des Skripts muss von einem Eintrag abgedeckt sein. Die zweite Richtung
  fehlte zuerst, und genau das ist durchgerutscht: Der Updater meldet vier Phasen, die Tabelle
  kannte zwei; der Installer zehn, die Tabelle neun. Der Balken stand dadurch über die halbe
  Update-Laufzeit still, ohne dass ein Test rot wurde.
- **Kein Abbrechen.** Ein halb installiertes System ist schlimmer als drei Minuten warten.

Der Balken **steht** während „Starting service" — diese Phase wartet bis zu 180 s auf
`/healthz/ready`. Der Text sagt das dazu. Ein künstlich weiterlaufender Balken würde Fortschritt
behaupten, den niemand misst.

Ein Timeout von 45 Minuten begrenzt die Schleife. Er greift nur, wenn der Adapter hart abgeschossen
wurde und `result.ini` nie erscheint — sonst würde der Wizard ewig warten.

## Port-Prüfung

Die Ports werden **vor** der Installation auf Bindbarkeit geprüft — gemessen an dem, was ohne diese
Prüfung passiert: Auf einem ConfigMgr-Standortserver reserviert HTTP.SYS die Ports 80 und 443, also
scheiterte Kestrel beim Start mit `SocketException 10013`. Sichtbar war davon nichts. Der Installer
hatte da bereits alles kopiert, den Dienst registriert, wartete 180 Sekunden auf `/healthz/ready`,
rollte dann zurück und meldete „did not report /healthz/ready" — drei Minuten für eine Aussage, mit
der niemand etwas anfangen kann.

Zwei Dinge unterscheidet die Prüfung, die eine naive Version nicht unterscheiden würde:

- **`10013` heißt nicht „belegt".** Windows liefert das für eine HTTP.SYS-Reservierung oder einen
  ausgeschlossenen Portbereich — es gibt **keinen Listener**, den man finden könnte. Eine Meldung
  „Port in Benutzung" schickt den Operator hinter einen Prozess her, den es nicht gibt. Die
  Anleitung nennt deshalb `netsh interface ipv4 show excludedportrange protocol=tcp`.
- **Der eigene Dienst zählt nicht als Konflikt.** Wird NodePilot über sich selbst installiert, hält
  der zu ersetzende Dienst den Port. Das als Fehler zu melden hieße, jemanden für eine korrekte
  Erstinstallation zu bestrafen.

Gebunden wird auf `IPAddress.Any` — dieselbe Adresse wie Kestrel (`AnyIPListenOptions.BindAsync`).
Ein Test gegen `127.0.0.1` würde einen Port durchwinken, der auf der Wildcard-Adresse reserviert ist.
Gebunden und sofort wieder freigegeben: eine Sonde, keine Änderung, sonst wäre sie hinter dem
„Check again"-Knopf nicht zulässig.

Schlägt die Installation trotzdem fehl, steht die **Ursache jetzt im Dialog**: der Adapter holt die
letzte `.NET Runtime`-Ausnahme des Laufs aus dem Application-Log und hängt sie an die Meldung
(`SocketException (10013): …`). Vorher stand dort nur der Symptomsatz, und die Ursache lag in einer
Logdatei, die niemand öffnet.

## Zertifikatsauswahl (TLS-Seite)

Unter dem Feld *Certificate thumbprint* steht eine Auswahlliste der Zertifikate aus
`Cert:\LocalMachine\My`. Eine Auswahl schreibt den Thumbprint **in das Feld darüber** — das Feld
bleibt der einzige Wert, den Answer-File, Validierung und der Rückschreibpfad des selbstsignierten
Zertifikats lesen. Deshalb musste für die Liste an keiner dieser Stellen etwas angepasst werden.

Der Grund für die Liste ist der Weg, den sie ersetzt: den Thumbprint eines bereits installierten
Zertifikats bekommt man sonst nur über die Zertifikats-MMC, deren Kopierknopf ein **unsichtbares
U+200E** voranstellt. Genau dafür wirft `Install-NodePilot.ps1` alle Nicht-Hex-Zeichen weg, bevor er
die Länge misst — 40 Zeichen, sieht richtig aus, wird trotzdem abgelehnt.

Vier Entscheidungen, die nicht offensichtlich sind:

- **Eigener Adapter-Modus, nicht die Probe.** `-Mode Certificates` liest nur den Zertifikatsspeicher:
  kein Answer-File, kein Session-Verzeichnis, keine Datenbankverbindung. Die Probe läuft erst auf der
  Readiness-Seite, also eine Seite *nach* der, auf der der Thumbprint eingetippt wird — und sie darf
  Sekunden auf ein Netzwerk-Timeout warten.
- **Nie blockierend.** Lässt sich die Liste nicht lesen, erscheint eine Zeile die das sagt, und der
  Thumbprint wird wie zuvor getippt. Die Readiness-Seite prüft ihn ohnehin. Ein Komfort-Feature, das
  eine funktionierende Installation stoppt, wäre ein schlechter Tausch.
- **Zertifikate ohne privaten Schlüssel werden angezeigt**, mit `NO PRIVATE KEY` markiert, statt
  gefiltert zu werden. „Es liegt doch im Store, warum steht es nicht da?" hat eine häufige Antwort —
  es wurde ein `.cer` importiert, wo ein `.pfx` gemeint war — und eine gefilterte Liste macht daraus
  ein Rätsel.
- **Jede Zeile trägt den Thumbprint**, nicht nur Subject und Ablauf. Auf CM1 liegen zwei
  Zertifikate mit demselben Subject **und** demselben Ablaufdatum („NodePilot Lab HTTPS" und
  „NodePilot Lab SQL TLS", 39 Sekunden auseinander ausgestellt): ohne den Thumbprint waren das zwei
  identische Zeilen, und die falsche hätte Kestrel kommentarlos das Datenbank-Zertifikat gegeben.
  Nebeneffekt: der Wert lässt sich gegen einen übergebenen Thumbprint prüfen, statt ihn zu glauben.
- **Sortiert nach Ablauf, spätestes zuerst.** Ein erneuertes Zertifikat liegt neben dem, das es
  ersetzt, unter demselben Subject; das Datum ist das Erste, was die beiden unterscheidet.

**Layout.** Die Liste steht auf derselben Seite wie die fünf Eingabefelder — dafür wird die Seite neu
umbrochen. Inno rechnet 54 px pro Label+Feld-Paar, die Controls brauchen real ~43 px; fünf Paare
belegten damit 270 der 309 px Fläche und die Liste wurde **unterhalb der sichtbaren Kante**
gezeichnet. Eine Input-Seite scrollt nicht und zeigt nicht an, dass da noch etwas ist. Der Umbruch
misst die Controls, statt Konstanten zu setzen, und eine Klemme erzwingt zusätzlich, dass die Liste
innerhalb der Fläche endet. Die Alternative wäre eine zweite Seite gewesen — fünf Werte, die zu
einer Entscheidung gehören, auf zwei Bildschirme verteilt.

**Was die Auswahl nicht prüft** — und die Readiness-Seite auch nicht: ob das Subject bzw. die SAN zum
*Public Host Name* passt, ob die Kette den Clients vertrauenswürdig ist, und ob das Zertifikat noch
gültig ist. Der Ablauf steht nur als Datum in der Zeile; ein **abgelaufenes Zertifikat installiert
sauber durch** und fällt erst im Browser auf. Für ein PKI-Zertifikat aus der eigenen CA ist das der
übliche Fall — es muss vorher im Maschinenspeicher liegen, mehr verlangt das Setup nicht:

```powershell
Import-PfxCertificate -FilePath cert.pfx -CertStoreLocation Cert:\LocalMachine\My `
  -Password (Read-Host -AsSecureString)
```

Ohne `MachineKeySet|PersistKeySet` findet `Grant-CertPrivateKeyAccess` später die Schlüsseldatei
unter `ProgramData\Microsoft\Crypto` nicht und bricht ab.

## Architektur

Der Pascal-Layer ist bewusst **dünn**: Seiten, Payload, `Exec`, INI lesen. Keine Installationslogik.

```
Wizard-Seiten  ->  answers.json  ->  Invoke-NodePilotSetup.ps1  ->  Install-NodePilot.ps1
(NodePilotServer.iss)  (ACL-geschützt)        (Adapter)            (unverändert)
```

Warum eine Datei statt einer Kommandozeile — drei Gründe, jeder für sich ausreichend:

1. `-PostgresPassword` ist ein `[SecureString]` und kann über `powershell.exe -File` **gar nicht**
   übergeben werden.
2. `/VERYSILENT /SUPPRESSMSGBOXES /ANSWERFILE=` fällt damit für SCCM/GPO ab, ohne zweiten Codepfad.
3. Inno-Pascal hat keine Unit-Test-Story. Was in PowerShell liegt, ist testbar
   ([`../Test-SetupAdapter.ps1`](../Test-SetupAdapter.ps1), 55 Assertions).

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

## Abschlussseite

Der Adapter schreibt nach einem erfolgreichen Lauf eine `[result]`-Sektion in seine INI; die
letzte Wizard-Seite zeigt sie. Enthalten ist alles, was für den ersten Zugriff nötig ist:

- **Adresse** (`https://<host>:<port>/`). Beim Update aus der bereits installierten
  `appsettings.Production.json` abgeleitet — ein Update erfragt keine Netzwerkdaten.
- **Setup-Token** für die erste Anmeldung, solange noch kein Konto existiert. Ist die Datei
  besitzer-exklusiv und unlesbar, wird stattdessen ihr Pfad und der `robocopy /B`-Trick genannt;
  fehlt sie ganz, hat die Datenbank bereits Konten — auch das steht dort, statt zu schweigen.
- **External-Trigger-API-Key.** Der einzige Ort, an dem er je erscheint: erzeugt wird er vom
  Adapter, `Install-NodePilot.ps1` druckt ihn auf eine Konsole, die unter `Exec(…, SW_HIDE)` nicht
  existiert, und `install-report.txt` lässt ihn bewusst weg.
- **Zertifikats-Thumbprint** mit dem Hinweis, ein selbstsigniertes auf den Clients zu importieren.
- **Dienstname, Programm- und Datenverzeichnis.**

Hier ist es ein `TNewMemo`, nicht wie auf der Readiness-Seite ein Label: die Seite ist sonst leer,
also ist Platz für ein ordentlich dimensioniertes, und ein 64-Zeichen-API-Key, den man nicht
markieren kann, müsste abgetippt werden. „Save this summary…" legt denselben Text auf den Desktop
— **mit den Secrets darin**, worauf der Bestätigungsdialog hinweist.

Gebaut wird die Zusammenfassung in `PrepareToInstall`, nicht in `CurPageChanged`: `DeinitializeSetup`
räumt das Session-Verzeichnis, die INI wäre beim Anzeigen also längst weg. Und ausschließlich auf
dem Erfolgspfad — ein zurückgerollter Lauf darf keine Werte präsentieren, als hätte er funktioniert.

## Deinstallation

Erreichbar an zwei Stellen: über „Apps & Features" wie bei jedem Windows-Programm, **und als dritte
Option auf der Modus-Seite**, wenn man das Setup auf einem Rechner startet, auf dem NodePilot
bereits installiert ist. Die zweite existiert, weil niemand, der gerade das Setup doppelgeklickt
hat, anschließend in der Systemsteuerung sucht. Die Modus-Seite fragt dort nichts selbst, sondern
übergibt an denselben Uninstaller — eine Entscheidung, eine Rückfrage.

Bei einer per ZIP installierten Instanz gibt es keinen `unins000.exe`. Die Option nennt dann den
Pfad zu `Uninstall-NodePilot.ps1` statt ins Leere zu greifen.

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
  die Verträge oben. Was ein Compilerlauf abdeckt — Syntax und jeder verwendete Bezeichner —
  bekommt man ohne Installer-Bau mit
  `ISCC /Qp /O- /DStageDir=<stage> /DOutputDir=<out> NodePilotServer.iss`; dass dabei wirklich der
  `[Code]`-Abschnitt übersetzt wird, lässt sich mit einem absichtlich falschen Bezeichner in einer
  Kopie nachweisen. Läuft **nicht** in CI (kein ISCC auf dem Runner).
- **Positionen berechneter Steuerelemente.** Dass die Zertifikatsliste *innerhalb* der Fläche
  landet, erzwingt die Klemme in `CompactNetworkPage` und ein Contract darauf. Wie die Seite dann
  aussieht — ob die Abstände stimmen oder es gedrängt wirkt — sieht man erst im laufenden Wizard.
  Genau so ist die Liste beim ersten Mal abgeschnitten ausgeliefert worden.
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
| 5 | `/VERYSILENT /SUPPRESSMSGBOXES /ANSWERFILE` | Exit 0, Dienst läuft |
| 6 | Runtime fehlt, Angebot abgelehnt | „Weiter" bleibt gesperrt |
| 7 | Rote Readiness-Zeile | „Weiter" gesperrt, Anleitung sichtbar |
| 8 | Abbruch mitten im Wizard | kein Session-Verzeichnis bleibt zurück |
| 9 | Deinstallation ohne Schalter | alles weg, Daten und Datenbank bleiben |
| 10 | Deinstallation `/PURGEDATA=1` | zusätzlich Datenverzeichnis weg, Datenbank bleibt |
| 11 | Modus-Seite → „Remove" | Uninstaller übernimmt, Setup schließt ohne Abbruch-Rückfrage |
| 12 | Neustart nach Installation | Dienst kommt ohne Zutun hoch, auch wenn die DB später bereit ist |
| 13 | Abschlussseite nach Neuinstallation | URL, Setup-Token, API-Key, Thumbprint, Pfade sichtbar und markierbar |
| 14 | Abschlussseite nach Update | URL aus der installierten Config, kein Token, kein neuer API-Key |
| 15 | Update über laufenden Dienst | wartet den Prozess ab statt abzubrechen |
| 16 | TLS-Seite, Zertifikat aus der Liste gewählt | Thumbprint steht im Feld darüber, Readiness-Zeile grün |
| 17 | TLS-Seite, leerer Zertifikatsspeicher | Hinweiszeile statt Auswahl, „Weiter" bleibt möglich |
| 18 | HTTP-Port 80 auf einem Host mit IIS | Readiness-Zeile rot mit „reserved by Windows", „Weiter" gesperrt |
| 19 | HTTP-Port 0 | Zeile grün, „HTTP disabled" |
| 20 | Installation interaktiv | Balken und Phasentext laufen, Fenster bleibt bedienbar, kein „Keine Rückmeldung" |
| 21 | Installation unbeaufsichtigt | keine Oberfläche, Exit-Code unverändert |

Stand: 1, 3, 5, 9 und 10 sind im Hyper-V-Lab gegen echtes AD, echte gMSA und SQL Server 2022 CU
gelaufen. 2, 4, 6, 7, 8 und 11 bis 21 nicht.

Zusatz 2026-08-04: Der unbeaufsichtigte Pfad wurde in **beide** Richtungen gegen CM1 gefahren.
`httpPort: 80` bricht nach 7 s mit Exit 7 ab — Dienst, Binaries und Config nachweislich unverändert,
`healthz` durchgehend 200 —, `httpPort: 0` installiert mit Exit 0 durch. Die Port-Zeile der
Readiness-**Seite** (18/19) ist damit noch nicht geklickt, nur der Check dahinter.
