# NodePilot Server Setup (`NodePilot-Server-Setup-<version>.exe`)

GUI-Installer für die **Server-Installation** (Windows-Dienst). Er ersetzt den ZIP-Weg nicht — er
ist ein zweiter Weg zur selben Installation. `deploy/Install-NodePilot.ps1` bleibt unverändert
nutzbar und ist weiterhin die Referenz; das Setup ruft genau dieses Skript auf.

> Für **eine einzelne Maschine ohne Netzwerkzugriff** ist die Desktop-App der schnellere Weg —
> siehe [`../desktop/README.md`](../desktop/README.md). Sie bindet ausschließlich Loopback.

## Quick start

Der Rest dieser Datei erklärt, **warum** die Dinge so sind. Das hier ist, **was man tut**.

### Vorher besorgen — zwei Dinge

**1. Kestrel-Zertifikat** auf dem Zielserver in den Maschinenspeicher:

```powershell
Import-PfxCertificate -FilePath cert.pfx -CertStoreLocation Cert:\LocalMachine\My `
  -Password (Read-Host -AsSecureString)
```

`MachineKeySet|PersistKeySet` ist bei diesem Aufruf Default und ist Pflicht — ohne persistierten
Maschinenschlüssel findet `Grant-CertPrivateKeyAccess` die Schlüsseldatei später nicht und die
Installation bricht ab. Gültig und auf den Public-Hostnamen ausgestellt: ein abgelaufenes Zertifikat
ist eine rote Pflicht-Zeile, ein abweichender Name nur eine Warnung.

Wer **noch keins hat**, lässt das Thumbprint-Feld leer: die Prüfseite meldet dann „No certificate
selected" und bietet an, ein selbstsigniertes zu erzeugen — für Labor und Pilot, nicht für
Produktion. Unbeaufsichtigt ist das derselbe Fall: leeres `certificate.thumbprint` plus
`"provisioning": { "generateSelfSignedCertificate": true }`.

**2. Datenbank-Server**, erreichbar, dessen **TLS der NodePilot-Host verifizieren kann** — bei
self-signed also den öffentlichen Teil nach `LocalMachine\Root` auf dem NodePilot-Server. SQL Server
muss **2022 CU1** (`16.0.4003.1`) oder neuer sein. Für PostgreSQL zusätzlich die **Root-CA als PEM**
bereitlegen.

**Datenbank, Login und Rechte legt das Setup an** — bei SQL Server, wenn das ausführende Konto
`sysadmin` ist; bei PostgreSQL, wenn Superuser-Zugangsdaten eingetragen werden. Das ist keine
Vorarbeit mehr, siehe [Auto-Fixes](#auto-fixes).

Nur beim gMSA-Pfad: Konto in AD anlegen, für den Host freigeben, `Install-ADServiceAccount` auf dem
Zielserver. Den Zugriff auf den privaten Schlüssel des Zertifikats erledigt der Installer.

### Der Wizard

| # | Seite | Eingabe |
|---|---|---|
| 1 | Modus | Neuinstallation (bzw. Update / Entfernen) |
| 2 | Zielordner | Installationsverzeichnis |
| 3 | Dienst-Identität | LocalSystem oder gMSA |
| 4 | Konto | nur bei gMSA: `DOMAIN\name$` |
| 5 | Datenbank | SQL Server 2022 CU1+ oder PostgreSQL 16+ |
| 6a | SQL Server | Server, Datenbank, Zertifikats-Hostname (leer = wird aus dem Server abgeleitet) |
| 6b | PostgreSQL | Host/Port/Datenbank, dann User, Passwort, Root-Zertifikat — optional Superuser + Passwort, damit Rolle und Datenbank angelegt werden können |
| 7 | Netzwerk und TLS | Public-Hostname, HTTPS-Port, HTTP-Port (**`0`** = kein Redirect), Allowed Hosts, Thumbprint — die Liste darunter füllt das Feld, **leer** heißt „habe ich noch nicht" |
| 8 | Prerequisites | zehn Prüfzeilen; rote Pflicht-Zeilen sperren „Weiter". Wo eine Checkbox erscheint: anhaken, „Weiter" führt den Fix aus und **prüft neu** |
| 9 | Installation | läuft mit Fortschritt und Phasentext, 2–3 Minuten |
| 10 | Abschluss | URL, Zugangsdaten bzw. Setup-Token, Pfade, Zertifikat — und der **External-Trigger-API-Key, der nur hier steht** |

Erster Login: das Setup-Token in das Feld, das die Anmeldemaske beim ersten Versuch einblendet.

### Unbeaufsichtigt

```
NodePilot-Server-Setup-<version>.exe /VERYSILENT /SUPPRESSMSGBOXES /ANSWERFILE=answers.json
```

Die elf Pflichtschlüssel stehen unter [Answer-File](#answer-file). Dazu praktisch immer:

```json
"provisioning": { "createDatabaseAndLogin": true },
"bootstrap":    { "adminUsername": "npadmin" }
```

Der erste legt Datenbank und Login an (bei PostgreSQL zusätzlich `provisioning.postgresSuperUser` /
`.postgresSuperPassword`), der zweite verhindert, dass der Lauf mit einem Token endet, das niemand
eintippt — das erzeugte Kennwort landet ACL-geschützt in `<dataPath>\bootstrap-admin.json`. Wer
stattdessen eine Referenzinstanz klonen will, nimmt `seed.backupPath`; siehe
[Schlüsselfertiger Rollout](#schlüsselfertiger-rollout-unbeaufsichtigt-ohne-token-eingabe).

### Zwei Stolpersteine

- **Ports 80 und 443 gehören auf einem Host mit IIS der HTTP.SYS** und sind für Kestrel nicht
  bindbar. Andere Ports wählen oder den HTTP-Port auf `0` setzen. Die Readiness-Seite sagt es
  vorher — mit „reserved by Windows", nicht mit „in use by System (PID 4)".
- **AV-Ausschlüsse** vorher einreichen: [`../../docs/av-exclusions.md`](../../docs/av-exclusions.md)
  ist als Übergabedokument für eine AV-Abteilung geschrieben.

Die Deinstallation fasst die **Datenbank nie** an; das Datenverzeichnis nur mit `/PURGEDATA=1`.

## Was er abnimmt — und was nicht

| | |
|---|---|
| **Nimmt ab** | Fünf Release-Assets herunterladen, Prüfsumme vergleichen, Signer-Thumbprint out-of-band abgleichen, `.cer` nach `LocalMachine\Root` importieren, neun Parameter fehlerfrei tippen, den Kestrel-Thumbprint aus der Zertifikats-MMC heraussuchen. Ein Asset, ein Doppelklick. |
| **Nimmt ab (opt-in)** | ASP.NET-Core-Runtime installieren, SQL-Login und Datenbank anlegen, **PostgreSQL-Rolle und -Datenbank anlegen**, selbstsigniertes Kestrel-Zertifikat erzeugen, Publisher-Zertifikat vertrauen. |
| **Nimmt nicht ab** | gMSA anlegen (AD-Aufgabe), TLS für die Datenbank, Kerberos-Delegation, AV-Ausschlüsse. |

Die **Readiness-Seite** prüft alles davon *bevor* etwas verändert wird — neun Zeilen: .NET-Runtime,
Kestrel-Zertifikat, **HTTP/HTTPS-Ports**, gMSA, Dienstidentität, Domänenmitgliedschaft,
DB-Erreichbarkeit, DB-Version, **DB-Zugriff der Dienst-Identität**. Jede Zeile trägt rechts ein
Statuszeichen — Haken, Kreuz, Ausrufezeichen oder Gedankenstrich — und ist zusätzlich eingefärbt.
Das Zeichen ist nicht Dekoration: Farbe allein sagt niemandem etwas, der dieses Grün nicht von
diesem Rot unterscheidet, und in einem Screenshot in einem Ticket schon gar nicht. Rote
Pflicht-Zeilen blockieren „Weiter" — der Installer würde ohnehin abbrechen, und ein Wizard, der
einen in ein garantiertes Scheitern hineinführt, ist schlechter als einer, der stoppt.

Ein Klick auf eine Zeile zeigt die zugehörige Anleitung darunter — ein **schreibgeschütztes,
scrollendes `TNewMemo`**. Das war einmal ein Label, weil ein Memo neben acht fest reservierten
Prüfzeilen nur eine Zeile hoch wurde und wie ein kaputtes Eingabefeld aussah; seit die Zeilen
dynamisch liegen, gilt der Grund nicht mehr, und eine Anleitung ist nicht nach fünf Zeilen zu Ende:
ein DB-Fix ist ein `CREATE LOGIN` / `CREATE USER` / `ALTER ROLE`-Block. Nebeneffekt, der die
Umstellung ohnehin gerechtfertigt hätte: der Text ist wieder markierbar. „In Datei speichern…"
bleibt trotzdem, weil Inno-Pascal keine Clipboard-API hat.

Die Zeilen werden erst positioniert, wenn ihr Text steht. Vorher reservierte jede der acht
Zeilen 16 px für eine Auto-Fix-Checkbox, die fast nie sichtbar ist — 128 px einer 309 px hohen
Fläche für nichts.

### Auto-Fixes

Rote Zeilen, die der Adapter selbst reparieren kann, tragen eine Checkbox. Anhaken + „Weiter"
führt den Fix aus und **prüft danach neu** — ein Fix gilt nie als gelungen, nur weil er gelaufen
ist. Der Haken ist mit dem Versuch verbraucht: er wird danach gelöscht, sonst hinge ein
dauerhaft scheiternder Fix (typisch: keine Rechte am SQL Server) in einer Schleife aus „Weiter →
gleiche rote Zeile".

**Herausgeber-Vertrauen** ist die Zeile, die am ehesten rot ist und am wenigsten damit zu tun hat,
was jemand konfiguriert hat: `Install-NodePilot.ps1` prüft die Signatur des mitgebrachten Artefakts
**samt Zertifikatskette**, und `CN=NodePilot Release Signing` ist selbstsigniert. Auf einem Host,
der ihn nicht kennt, scheitert das — und zwar erst mitten in der Installation, mit Rollback. Die
Zeile nimmt das vorweg und bietet den Import nach `LocalMachine\Root` an: angeboten, **nicht**
vorangehakt, und der Thumbprint steht in der Meldung, damit er vor dem Haken gegen die
Release-Notes gehalten werden kann. Passt das Zertifikat im Payload nicht zu dem Thumbprint, gegen
den das Setup gebaut wurde, verschwindet das Angebot ersatzlos — ein Knopf, der eine fremde CA
maschinenweit vertrauenswürdig macht, wäre schlimmer als eine verweigerte Installation.

Ein leeres Thumbprint-Feld ist der Weg zu genau einem dieser Fixes: die Zertifikatszeile meldet
dann „No certificate selected" statt eines nicht gefundenen Thumbprints und bietet die Erzeugung
eines selbstsignierten an — **nicht** vorangehakt, denn ein Laborzertifikat entsteht auf Ansage,
nicht durch Drücken von „Weiter". Bei einem **abgelaufenen** Zertifikat wird die Erzeugung bewusst
nicht angeboten (siehe unten).

Eine Zeile kommt **vorangehakt**: der DB-Zugriff der Dienst-Identität. Der Pre-Flight testet die
Erreichbarkeit mit der Identität des installierenden Admins — zur Laufzeit meldet sich aber der
Dienst an, unter dem Computer-Konto (LocalSystem) bzw. dem gMSA. Diese Zeile fragt genau das ab
(Login vorhanden? Benutzer in der Ziel-DB? `db_owner`?) und legt es bei Bedarf an. Das ist Teil
des Installierens, kein Eingriff in fremde Infrastruktur — anders als `CREATE DATABASE`, das
opt-in bleibt. Sichtbar und abwählbar ist der Haken trotzdem.

Der Fix läuft über `Provision-NodePilotDatabase.ps1`: erst Rechte-Gate (`sysadmin` oder
`CREATE ANY DATABASE`), dann existenzgeprüft Login → Datenbank → Benutzer → `db_owner`. Fehlen
die Rechte, wird **nichts** verändert und der Wizard zeigt die Anweisungen für den DBA.

### PostgreSQL

Dieselbe Zeile, andere Mechanik — und ein Unterschied, den man kennen muss: bei SQL Server ist die
Berechtigung gratis, weil `Trusted_Connection` die Windows-Identität des installierenden Admins
*ist*. PostgreSQL kennt das nicht. Für `CREATE ROLE`/`CREATE DATABASE` braucht das Setup deshalb
**Superuser-Zugangsdaten**, die es sonst nirgends bekommt: zwei zusätzliche Felder auf der
Credentials-Seite (leer lassen → kein Fix-Angebot), unbeaufsichtigt
`provisioning.postgresSuperUser` / `.postgresSuperPassword`. Der Dienst sieht sie nie; sie leben
nur in der ACL-geschützten Session der laufenden Installation.

Der Client (`psql`) liegt im Payload — sieben Dateien, 8,4 MB, gemessen aus der Import-Tabelle von
`psql.exe`, also ohne ICU und ohne die pgAdmin-Bibliotheken. Er wird **erst extrahiert, wenn er
gebraucht wird**; eine Installation auf SQL Server fasst ihn nie an. Gebaut wird er nur mit
`-PgBinariesPath`; ohne den Schalter entsteht derselbe Installer wie zuvor, und die Postgres-Zeile
sagt dann ausdrücklich, dass die Anmeldung ungeprüft blieb.

Dadurch ist die Zeile überhaupt erst aussagekräftig: vorher war sie ein reiner TCP-Probe, der bei
fehlender Rolle, fehlender Datenbank oder falschem Passwort **grün** blieb — der Fehler tauchte
180 Sekunden später beim Health-Probe auf und rollte die Installation zurück. Jetzt meldet sich der
Check als NodePilot-Rolle an, in derselben TLS-Form wie die Laufzeit (`sslmode=verify-full` gegen
das angegebene Root-Zertifikat).

**Was fehlt, wird im Katalog nachgesehen, nicht aus der Fehlermeldung gelesen.** psql-Meldungen sind
lokalisiert — ein deutscher Server antwortet „Rolle »nodepilot« existiert nicht" —, ein
englisch gebauter Matcher klassifiziert also auf einem Host richtig und auf dem nächsten alles als
„abgelehnt". Bei fehlgeschlagener Anmeldung fragt der Check deshalb mit den Superuser-Daten
`pg_roles` und `pg_database`. Ohne Superuser-Daten sagt er, dass er es nicht unterscheiden kann,
und gibt die Server-Meldung wörtlich weiter — statt zu raten.

Der Fix (`Provision-NodePilotPostgres.ps1`) folgt derselben Regel wie die SQL-Server-Seite: erst
Rechte-Gate (`rolsuper` oder `rolcreaterole` **und** `rolcreatedb`), dann existenzgeprüft Rolle →
Datenbank, zum Schluss eine Probeanmeldung als die Rolle selbst. Zwei Dinge tut er bewusst **nicht**:
das Passwort einer vorhandenen Rolle zurücksetzen (ein nicht passendes Passwort ist ein Tippfehler in
der Answer-File — ihn zu „heilen" würde ihn verstecken und alles andere aussperren, was diese Rolle
benutzt) und den Eigentümer einer vorhandenen Datenbank ändern. Beides wird gemeldet, nicht
korrigiert.

## Schlüsselfertiger Rollout (unbeaufsichtigt, ohne Token-Eingabe)

Ein unbeaufsichtigter Lauf endet sonst mit einer Instanz, die niemand benutzen kann: das
Setup-Token müsste ein Mensch in die Anmeldemaske tippen. Es gibt zwei Wege daran vorbei, die sich
gegenseitig ausschließen — welcher greift, entscheidet allein, ob die Instanz danach Benutzer hat.

### Variante 1: Zufalls-Admin

Mit der optionalen `bootstrap`-Gruppe löst das Setup das Token selbst ein.

```json
"bootstrap": {
  "adminUsername": "npadmin",
  "credentialOutputPath": "C:\\ProgramData\\NodePilot\\bootstrap-admin.json"
}
```

`credentialOutputPath` ist optional; ohne Angabe liegt die Datei unter
`<DataPath>\bootstrap-admin.json`. Inhalt: Benutzername, Kennwort, Adresse, Zeitstempel.

**Das Kennwort wird pro Maschine zufällig erzeugt, nicht vorgegeben.** Ein fester Wert wäre über
alle Maschinen gleich, hätte einen bekannten Wert und würde gefunden statt geraten — auf einem
Produkt, das PowerShell auf allen verwalteten Maschinen ausführt und im Server-Modus auf allen
Interfaces lauscht. Die Answer-File kennt deshalb **kein** `adminPassword`.

### Variante 2: Bestand aus einem Backup einspielen

Die reichere Variante. Eine Referenzmaschine normal installieren, einrichten, `np backup export` —
das Ergebnis ist der Seed für alle weiteren Maschinen:

```json
"seed": {
  "backupPath": "\\\\share\\golden.npbackup",
  "passphrase": "…"
}
```

Der Installer kopiert die Datei nach `<DataPath>\seed.npbackup` (restriktive ACL, gleicher Writer
wie die Konfiguration) und legt die Passphrase in den `Environment`-Wert des Dienstschlüssels —
**nicht** in die `appsettings.Production.json`, genau wie die Postgres-Verbindungszeichenfolge. Beim
ersten Start spielt `ProvisioningSeeder` sie ein, **bevor** irgendetwas die Benutzertabelle liest,
und löscht die Datei danach.

Damit kommt die Maschine mit Benutzern, Workflows, Maschinen, Credentials **und Settings** hoch. Weil
das Restore einen Break-Glass-Admin verlangt, wenn es in eine leere Datenbank läuft, ist danach auch
`EnterpriseRecoveryInvariant` erfüllt — LDAP/SSO lässt sich also einschalten. (Die Auth-Sektion ist
laut Hot-Reload-Matrix restart-pflichtig; nach dem Seed einmal neu starten.)

Zwei Regeln, die den Seed sicher machen, dauerhaft konfiguriert zu lassen:

- **Nur in eine leere Instanz.** Existieren Benutzer, passiert nichts — der Seed ist Erstbefüllung,
  nie Migration. Eine Maschine im Betrieb behält alles, was sie hat, egal was die Konfiguration sagt.
- **Fail closed.** Falsche Passphrase, fehlende oder kaputte Datei → der Dienst startet **nicht**.
  Die Alternative wäre eine leere Instanz mit offenem Bootstrap-Fenster, die der Betreiber für
  provisioniert hält.

Was dabei passiert:

| Lage | Ergebnis |
|---|---|
| `seed`-Gruppe gesetzt, Instanz leer | Bestand wird eingespielt. Es gibt kein Token, `bootstrap.status=AlreadyProvisioned`, **keine** Zugangsdatei. |
| Benutzer existieren bereits (Seed oder Neuinstallation über bestehende DB) | Es gibt kein Token, nichts einzulösen. `bootstrap.status=AlreadyProvisioned`, **keine** Zugangsdatei. |
| Kein Benutzer, `bootstrap.adminUsername` gesetzt | Konto wird angelegt, Zugangsdaten werden abgelegt. `bootstrap.status=Created`. |
| Kein Benutzer, keine `bootstrap`-Gruppe | Wie bisher: Token auf der Abschlussseite, manuelle Erstanmeldung. |

**Die Zugangsdatei ist eine lebende Zugangsberechtigung.** Sie entsteht mit ACL vor Inhalt
(SYSTEM + Administratoren, keine Vererbung) über dieselbe Mechanik wie das signierte Artefakt-Staging
— sie erbt also nie kurzzeitig von `DataPath`. Gelöscht wird sie **nicht** automatisch: ein Rollout,
der sie noch nicht abgeholt hat, stünde sonst ohne Konto da. Abholen, löschen, Kennwort rotieren ist
der Schritt des Betreibers.

Zwei Eigenschaften, die nicht offensichtlich sind:

- **Ein fehlgeschlagener Bootstrap kippt die Installation nicht.** Der Dienst läuft und ist gesund,
  wenn der Login versucht wird. Exit-Code bleibt 0, `bootstrap.status=Failed` trägt die Antwort des
  Servers wörtlich. Eine funktionierende Installation als Fehlschlag zu melden hieße, SCCM zu einem
  Wiederholungslauf zu bewegen — und ein erneuter Install ist deutlich zerstörerischer als ein
  fehlendes Konto.
- **Der Name wird festgenagelt.** Ist `bootstrap.adminUsername` gesetzt, schreibt der Installer
  `NodePilot:BootstrapAdminUsername` in die Konfiguration. Selbst ein zwischen Dienststart und
  Adapter-Login abgefangenes Token kann dann nur genau dieses Konto anlegen.

**LDAP/SSO ersetzt das nicht.** Die JIT-Provisionierung ist ausdrücklich gesperrt, solange kein
lokaler Break-Glass-Admin existiert (`external_jit_blocked_until_breakglass_admin_exists`), und
`EnterpriseRecoveryInvariant` bricht den Boot ab, wenn SSO ohne einen solchen aktiv ist. Das so
erzeugte Konto trägt `IsBreakGlass` und erfüllt genau diese Bedingung — SSO lässt sich danach
einschalten.

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

**Ein leeres Feld ist erlaubt** und heißt „ich habe noch keins". Die Seite prüft die Länge nur,
wenn überhaupt etwas dasteht; entschieden wird auf der Prüfseite, die die Zertifikatszeile rot
setzt und die Erzeugung anbietet. Vorher verlangte die Seite bedingungslos 40 Zeichen — und sagte
in derselben Meldung, man solle das Feld so lassen, wie es ist. Auf einer Maschine ganz ohne
Zertifikat kam man an das Angebot also nur heran, indem man 40 Hex-Zeichen erfand.

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

**Was die Readiness-Seite prüft:** Vorhandensein im Maschinenspeicher, privater Schlüssel,
**Gültigkeitszeitraum** und **Namensabgleich**. Ein abgelaufenes (oder noch nicht gültiges)
Zertifikat ist eine rote Pflicht-Zeile und stoppt die Installation — vorher stand der Ablauf nur
als Datum in der grünen Zeile, die Installation lief durch, und der Erste, der davon erfuhr, war ein
Benutzer mit einer Browser-Warnung. Ein Auto-Fix gibt es hier bewusst **nicht**: „dein
PKI-Zertifikat ist abgelaufen" mit „hier, nimm ein Lab-Zertifikat" zu beantworten wäre schlimmer als
anzuhalten.

Der Namensabgleich läuft gegen die SAN-Liste (Wildcards decken genau ein Label, RFC 6125; ohne SAN
zählt der CN) und ist **nur eine Warnung** — hinter einem Reverse-Proxy oder unter einem Alias ist
ein abweichender Name legitim, und „Weiter" bleibt möglich. Gelesen wird `DnsNameList` aus dem
PowerShell-Zertifikats-Provider, nicht `Extensions.Format()`: dessen Ausgabe ist lokalisiert
(`DNS Name=` vs. `DNS-Name=`), ein Parser darauf funktioniert auf einem englischen Host und findet
auf einem deutschen stillschweigend nichts.

**Was weiterhin niemand prüft:** ob die Kette den Clients vertrauenswürdig ist. Für ein
PKI-Zertifikat aus der eigenen CA ist das der übliche Fall — es muss vorher im Maschinenspeicher
liegen, mehr verlangt das Setup nicht:

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
NodePilot-Server-Setup-1.1.2.exe /VERYSILENT /SUPPRESSMSGBOXES /ANSWERFILE=C:\prod\answers.json
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
  "certificate": { "thumbprint": "A1B2...", "source": "existing" },

  "bootstrap": { "adminUsername": "npadmin" },
  "seed": { "backupPath": "\\\\share\\golden.npbackup", "passphrase": "..." }
}
```

`identity.type` ist `localSystem` oder `gmsa` (dann ist `identity.account` Pflicht).
`database.provider` ist `sqlserver` (dann `sqlServer` + `sqlDatabase`) oder `postgres` (dann
`postgresHost`, `postgresDatabase`, `postgresUser`, `postgresPassword`, `postgresRootCertificate`).
`certificate.thumbprint` ist der einzige Pflichtschlüssel, der **leer** sein darf: leer heißt „noch
keins vorhanden" und verlangt dann `provisioning.generateSelfSignedCertificate`. Steht etwas drin,
müssen es 40 Hex-Zeichen sein — sonst bricht der Lauf hier ab statt später in der Kestrel-Config.
Für `"mode": "update"` genügen `installPath` und `serviceName`; jeder weitere Schlüssel wird
abgelehnt, damit eine veraltete Datei nicht halb angewendet wird.

**Optionale Schlüssel im Überblick:**

| Schlüssel | Wirkung |
|---|---|
| `serviceDisplayName` | Anzeigename des Dienstes |
| `database.sqlCertificateHostName` | leer lassen → Installer leitet ihn aus `sqlServer` ab |
| `network.allowedHosts`, `network.knownProxyIps` | Host-Filter und vertrauenswürdige Proxy-IPs. `localhost` hängt der Installer immer an — seine eigene Health-Probe geht dorthin |
| `certificate.source` | rein dokumentarisch |
| `provisioning.installDotnetRuntime`, `.createDatabaseAndLogin`, `.generateSelfSignedCertificate`, `.trustArtifactSigner` | dieselben Auto-Fixes wie auf der Readiness-Seite, **auch im Silent-Modus** — dort ist die Answer-File die einzige Stelle, an der sie angefordert werden können. Laufen vor der Installation, nicht danach. |
| `provisioning.postgresSuperUser`, `.postgresSuperPassword` | nur für den PostgreSQL-Fix. `CREATE ROLE`/`CREATE DATABASE` brauchen eine Berechtigung, die es bei SQL Server gratis über die Windows-Identität gibt. Der Dienst sieht sie nie |
| `bootstrap.adminUsername` | legt den ersten Admin an, Kennwort zufällig (siehe [Schlüsselfertiger Rollout](#schlüsselfertiger-rollout-unbeaufsichtigt-ohne-token-eingabe)) |
| `bootstrap.credentialOutputPath` | wohin die Zugangsdaten geschrieben werden. Default `<dataPath>\bootstrap-admin.json` |
| `seed.backupPath` | `.npbackup`, das beim ersten Start eingespielt wird |
| `seed.passphrase` | dessen Passphrase. Landet **nie** in der `appsettings.Production.json`, sondern im `Environment`-Wert des Dienstschlüssels |
| `skips.databaseCheck`, `skips.gmsaCheck` | überspringen die jeweilige Preflight-Prüfung |

`bootstrap` und `seed` schließen einander nicht aus, aber nur einer greift: bringt der Seed Benutzer
mit, gibt es kein Token, und `bootstrap` läuft ins Leere. Ohne beide bleibt es beim Token auf der
Abschlussseite.

**Für einen Rollout auf eine frische Datenbank gehört `provisioning.createDatabaseAndLogin` in die
Answer-File.** Ein Schlüssel, beide Provider — welches Skript läuft, folgt aus `database.provider`
und nicht aus einem zweiten Flag, das dem ersten widersprechen könnte. Auf dem Postgres-Pfad
brauchen zusätzlich `provisioning.postgresSuperUser` / `.postgresSuperPassword` gesetzt zu sein,
sonst bleibt die Rolle unangetastet und der Lauf sagt es im Log.

Bei SQL Server deckt der Schlüssel beides ab, was ein unbeaufsichtigter Lauf sonst offen lässt:
Datenbank und Login anlegen, und der Dienst-Identität (Computer-Konto bzw. gMSA) `db_owner` geben.
Ohne ihn startet der Dienst und antwortet auf `/healthz/ready` mit 503, weil er sich an der
Datenbank nicht anmelden kann. Existenzgeprüft — auf einer Maschine, wo alles schon da ist,
verändert der Lauf nichts. Ausgeführt wird er mit den Rechten des Kontos, das das Setup startet;
ohne `sysadmin` bzw. `CREATE ANY DATABASE` bleibt alles unverändert und der Grund steht im Log.
Interaktiv braucht es den Schlüssel nicht — dort hakt die Readiness-Seite die Zeile selbst an.

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
    -ArtifactPath .\out\NodePilot-1.1.2.zip `
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
| 22 | Unbeaufsichtigt mit `bootstrap.adminUsername` | Exit 0, Zugangsdatei da (ACL nur SYSTEM + Administratoren), Anmeldung damit ohne Token, `admin-setup.token` weg |
| 23 | Unbeaufsichtigt ohne `bootstrap`-Gruppe | wie bisher: Token auf der Abschlussseite, kein Konto |
| 24 | Unbeaufsichtigt mit `seed`-Gruppe, leere Instanz | Anmeldung mit einem Benutzer **aus dem Backup**, kein Token, keine Zugangsdatei, Seed-Datei gelöscht |
| 25 | Derselbe Seed gegen eine befüllte Instanz | nichts passiert, keine Duplikate, Seed-Datei bleibt liegen |
| 26 | Falsche Seed-Passphrase | Dienst startet **nicht**, Meldung nennt die Passphrase, kein Teilbestand in der DB |
| 27 | SQL Server ohne Login für die Dienst-Identität | Zeile rot **und vorangehakt**, „Weiter" legt Login + Benutzer + `db_owner` an, danach grün |
| 28 | Derselbe Lauf mit bereits vorhandenem Login | Zeile grün ohne Checkbox, nichts wird verändert |
| 29 | Fix ohne `sysadmin` | nichts verändert, Meldung nennt den Grund, Haken danach gelöscht (keine Schleife) |
| 30 | Unbeaufsichtigt mit `provisioning.createDatabaseAndLogin` | Exit 0, Datenbank + Login angelegt, `/healthz/ready` 200 |
| 31 | Abgelaufenes Zertifikat gewählt | Zeile rot mit Ablaufdatum, „Weiter" gesperrt, kein Auto-Fix angeboten |
| 32 | Zertifikat mit fremdem SAN | Zeile **gelb**, nennt beide Namen, „Weiter" bleibt möglich |
| 33 | Postgres ohne Rolle/Datenbank, Superuser angegeben | Zeile rot mit Checkbox, „Weiter" legt beides an, Neuprüfung grün |
| 34 | Dasselbe ohne Superuser-Felder | Zeile rot **ohne** Checkbox, Server-Meldung wörtlich, Snippet sichtbar |
| 35 | Postgres mit falschem Rollen-Passwort | Zeile rot, nennt „beide vorhanden", kein Fix-Angebot, Passwort bleibt unverändert |
| 36 | Installer ohne `-PgBinariesPath` gebaut, Postgres gewählt | Zeile **gelb**: erreichbar, Anmeldung ungeprüft |
| 37 | Neuinstallation mit gMSA über eine LocalSystem-Installation | Exit 0, Dienst läuft als gMSA, `jwt-secret.key` gehört jetzt dem gMSA |
| 38 | Fehlschlag **nach** dem ACL-Schritt | Rollback stellt Dienst **und** Verzeichnis-ACL wieder her, die vorherige Installation läuft weiter |
| 39 | Thumbprint-Feld leer gelassen | „Weiter" führt auf die Prüfseite, Zeile rot mit „No certificate selected" + **nicht** vorangehaktem Angebot; Haken + „Weiter" erzeugt eins, schreibt den Thumbprint ins Feld zurück, Neuprüfung grün |
| 40 | Feld mit 12 Zeichen gefüllt | Meldung „40 hexadecimal characters", Seite bleibt stehen |
| 41 | Host, der den Herausgeber nicht kennt | Zeile „Artifact publisher trusted" rot mit Thumbprint in der Meldung und **nicht** vorangehaktem Angebot; Haken + „Weiter" importiert nach `LocalMachine\Root`, Neuprüfung grün, Installation läuft durch |
| 42 | Derselbe Host nach dem Import | Zeile grün ohne Checkbox, nichts wird verändert |

Stand: 1, 3, 5, 9, 10, 22, 23, 30, 37 und 38 sind im Hyper-V-Lab gegen echtes AD, echte gMSA und
SQL Server 2022 CU gelaufen. 2, 4, 6, 7, 8, 11 bis 21, 24 bis 26, 27 bis 29, 31 bis 36 sowie 39 bis 42 nicht —
wobei die **Logik** hinter 33 bis 35 gegen einen echten PostgreSQL 16 mit TLS gefahren wurde (siehe
unten); was dort fehlt, ist die Seite.

Zusatz 2026-08-06 (zweiter Befund): Auf einem frischen Host waren **alle** Zeilen grün und die
Installation brach danach mit Exit 4 und Rollback ab — `CheckSignature` scheiterte an der Kette des
selbstsignierten Herausgebers. Die Prüfseite kannte diese Anforderung schlicht nicht (neun IDs,
keine davon `signer`), und der Fix, der seit jeher im Adapter liegt, war im Wizard hart auf `false`
verdrahtet. Dazu ein zweiter, unabhängiger Fehler: `Invoke-ProvisionSigner` suchte die `.cer` unter
`signer\` — ein Ordner, den der Build nie anlegt, weil `[Files]` mit `dontcopy` **ohne**
`recursesubdirs` alles flach nach `{tmp}` legt. Der Auto-Fix hätte also auch dann nichts gefunden,
wenn man ihn über die Answer-File angefordert hätte. Beides behoben — und beim ersten Lauf mit der
neuen Zeile schlug prompt der Layout-Vorbehalt zu: Die zehnte Zeile war fünf Zeilen hoch, damit
rutschte ihre eigene Checkbox hinter die Buttons. Ein Fix, den man sieht, erklärt bekommt und nicht
anhaken kann. Zwei Korrekturen: die Meldung ist auf zwei Zeilen gekürzt (die Kettenbegründung des
Betriebssystems steht jetzt im scrollbaren Anleitungsfeld), und `LayoutReadiness` zählt die
sichtbaren Fix-Boxen vorab und garantiert jeder einen klickbaren Streifen über den Buttons. Die
Zeilen 41/42 unten decken den Fall ab und sind **noch nicht** geklickt.

Zusatz 2026-08-06: Zeile 39 ist im Feld aufgeschlagen — leeres Feld, und der Probe-Lauf starb mit
„Answer file is missing required key 'certificate.thumbprint'", weil die Vertragsprüfung Pflicht mit
nicht-leer gleichsetzte. Behoben; danach mit einer echten Answer-File (leerer Thumbprint) gegen
`-Mode Probe` nachgestellt: Exit 2 (`ExitProbeFailed`, die erwartete Antwort für eine rote
Pflicht-Zeile), `check.certificate` meldet „No certificate selected" mit `canAutoFix=1` und
`autoFixDefault=0`. Was damit **noch nicht** geklickt ist: der Haken selbst und das Zurückschreiben
des erzeugten Thumbprints in das Feld — also die zweite Hälfte von Zeile 39.

Zusatz 2026-08-04: Der unbeaufsichtigte Pfad wurde in **beide** Richtungen gegen CM1 gefahren.
`httpPort: 80` bricht nach 7 s mit Exit 7 ab — Dienst, Binaries und Config nachweislich unverändert,
`healthz` durchgehend 200 —, `httpPort: 0` installiert mit Exit 0 durch. Die Port-Zeile der
Readiness-**Seite** (18/19) ist damit noch nicht geklickt, nur der Check dahinter.

Zusatz 2026-08-05: Der DB-Zugriffs-Check gegen echten SQL Server 2022, alle drei Verdikte —
vorhandener `db_owner` → grün (auch bei abweichender Groß-/Kleinschreibung des Benutzernamens, weil
über SID aufgelöst wird), Login ohne Rolle → rot mit Fix-Angebot, Login gar nicht vorhanden → rot.
`autoFixDefault=1` kommt nachweislich in der `probe.ini` an. Der Fix selbst zweimal hintereinander
gefahren: beim zweiten Mal `Pass` ohne Änderung. Fall 30 end-to-end: Datenbank existierte nicht,
`/VERYSILENT /ANSWERFILE` mit `createDatabaseAndLogin` → Exit 0, Datenbank + Login + `db_owner`
angelegt, 36 Tabellen migriert, `healthz` 200. Gegenprobe ohne den Schlüssel: Exit 7 im Pre-Flight,
nichts angefasst. Was weiter fehlt, ist die **Seite** (27–29): die Checkbox ist nie geklickt worden.

Zusatz 2026-08-05 (PostgreSQL): gegen einen eigens aufgesetzten PostgreSQL 16 mit `ssl = on` und
`sslmode=verify-full`, sieben Fälle. Rolle und Datenbank fehlen, Superuser vorhanden → rot, beide
namentlich genannt, Fix angeboten; dasselbe ohne Superuser → rot, deutsche Server-Meldung wörtlich,
kein Fix. Fix legt beides an und meldet sich zur Kontrolle als die Rolle an; zweiter Lauf ändert
nichts (`Pass`); Neuprüfung grün ohne Checkbox. Fix mit einem Konto ohne `CREATEROLE`/`CREATEDB` →
`Skipped`, und im Katalog nachgesehen: **nichts** angelegt. Falsches Rollen-Passwort bei
vorhandener Rolle → rot mit „beide vorhanden", kein Fix, Passwort unverändert.

Der Cluster antwortete auf Deutsch — was den Entwurf geändert hat: die ursprüngliche
Fehlerklassifikation las psql-Meldungen und hätte „Rolle »nodepilot« existiert nicht" als
„abgelehnt" durchgereicht. Seitdem wird `pg_roles`/`pg_database` gefragt statt geparst.

Zusatz 2026-08-05 (Identitätswechsel): gemeldeter Fall reproduziert — LocalSystem installieren, dann
frisch mit gMSA. Zwei Defekte, beide behoben und nachgemessen.

Erstens schrieb der Dienst `jwt-secret.key` mit Owner und **einer** ACE für sich selbst; nach dem
Wechsel kam die neue Identität nicht mehr an die eigene Datei („the file, its owner, or its ACL
could not be verified"). Der Installer übergibt sie jetzt.

Zweitens — und das war der schlimmere Teil — ließ der **Rollback** die ACE der neuen Identität auf
dem Datenverzeichnis stehen. Aus Sicht der zurückgestellten Identität ist das ein *untrusted
principal* mit Mutationsrechten am Elternverzeichnis des JWT-Keys, also startete auch die
wiederhergestellte Installation nicht mehr: im Log „ROLLBACK ALSO FAILED", auf dem Bildschirm die
Meldung mit „grants mutation rights to an untrusted principal" — die aus dem *zurückgerollten*
Dienst stammte, nicht aus dem neuen. Ein gescheiterter Identitätswechsel riss damit die laufende
Installation mit. Nachgemessen: die ACE entfernen, Dienst startet wieder.

Nachher, beides gegen CM1: gMSA-Installation über LocalSystem → Exit 0, Dienst läuft als
`CORP\q-sdvorch2$`, Key-Owner mitgewandert. Erzwungener Fehlschlag nach dem ACL-Schritt → Exit 7,
Dienst **weiterhin laufend** unter der alten Identität, Verzeichnis-ACL und Key-Owner
zurückgestellt.

Dabei gefunden und behoben: eine `AllowedHosts`-Liste ohne `localhost` ließ die Installation an
ihrer **eigenen** Health-Probe scheitern — `UseHostFiltering` antwortet auf `Host: localhost` mit
400, die Probe geht aber an `https://localhost:<port>/healthz/ready`. Ergebnis war ein Rollback
nach erfolgreicher Migration, mit „did not report /healthz/ready within 180s" als einzigem Hinweis.
Der Installer hängt `localhost` jetzt immer an. Fremde Hosts bleiben abgewiesen (nachgemessen:
`Host: evil.example` → 400).
