# Test-ADServiceAccount liefert False: das gMSA auf dem Host prüfen

Liefert `Test-ADServiceAccount` den Wert `False`, ist das verwaltete Konto auf dem Computer, der den Test ausführt, nicht einsatzbereit. Zuerst diesen Host, das Kontoobjekt und die Berechtigung zum Kennwortabruf prüfen. Eine vorschnelle Neuanlage des Kontos oder Änderung der Anwendungsrechte erschwert die Diagnose.

Ein zurückgegebenes `False` ist von einem Befehlsfehler zu unterscheiden. Ein fehlendes ActiveDirectory-Modul, ein falsch geschriebener Kontoname und ein unerreichbares Verzeichnis sind verschiedene Fehler. Sobald der Kontotest `True` liefert, bleiben Aufgaben- beziehungsweise Dienststart und Dateizugriffe gesondert zu prüfen.

Diese Anleitung ergänzt die [gMSA-Grundlagen](/blog/gmsa-service-accounts/). Die Befehle verwenden Beispielnamen. Am 25. Oktober 2026 haben wir im Labor auf Windows Server 2025 mit Windows PowerShell 5.1.26100.1591 einen Host ohne Abrufberechtigung geprüft, die Berechtigung korrigiert und danach eine geplante Aufgabe mit erlaubtem und verweigertem Zielzugriff ausgeführt. Die übrigen Diagnosezweige sind Prüfschritte, keine Behauptung, dass jeder Fehler nachgestellt wurde.

## 1. Den Fehler auf dem Ausführungshost erfassen

Lokal auf dem Computer, der später den Auftrag ausführt, eine erhöhte Windows-PowerShell-Sitzung öffnen. Ein Test auf dem Domänencontroller oder dem eigenen Arbeitsplatz beantwortet eine andere Frage. Beginnen mit:

```powershell
$env:COMPUTERNAME
$PSVersionTable.PSVersion
Get-Module -ListAvailable ActiveDirectory
Import-Module ActiveDirectory -ErrorAction Stop
Test-ADServiceAccount -Identity 'gmsaExport' -Verbose -ErrorAction Stop
```

Die tatsächliche Ausgabe und den vollständigen Warnungs- beziehungsweise Fehlertext festhalten. Fehlt das Modul, zunächst die passenden AD-Verwaltungswerkzeuge über den üblichen Bereitstellungsweg installieren. Erzeugt eine entfernte Administrationssitzung einen weiteren Authentifizierungssprung, die Diagnose zuerst lokal wiederholen, bevor Delegationseinstellungen geändert werden.

| Beobachtung | Nächster Schritt |
|---|---|
| Modul oder Befehl nicht gefunden | AD-Modul in dieser Laufzeit bereitstellen |
| Konto nicht gefunden | Schreibweise, Domäne und Kontoobjekt in Schritt 2 prüfen |
| Verzeichnis nicht erreichbar | DNS, DC-Ermittlung und sicheren Kanal in Schritt 3 prüfen |
| Der Befehl liefert `False` | Abrufberechtigung, KDS und lokale Installation prüfen |
| Der Befehl liefert `True`, aber der Auftrag scheitert | In Schritt 6 mit Start und Ressourcenrechten fortfahren |

Die [Microsoft-Dokumentation des Cmdlets](https://learn.microsoft.com/en-us/powershell/module/activedirectory/test-adserviceaccount?view=windowsserver2025-ps) beschreibt den lokalen Kontotest. Er prüft weder das Skript noch dessen Zielfreigabe.

## 2. Konto und berechtigte Computer ansehen

Auf einem Rechner mit AD-Verwaltungszugriff das gewünschte Objekt prüfen:

```powershell
Get-ADServiceAccount -Identity 'gmsaExport' `
    -Properties PrincipalsAllowedToRetrieveManagedPassword, KerberosEncryptionType |
    Select-Object Name, SamAccountName, ObjectClass, Enabled, `
        PrincipalsAllowedToRetrieveManagedPassword, KerberosEncryptionType
```

`ObjectClass` muss für diese Anleitung ein gruppenverwaltetes Dienstkonto bezeichnen und `Enabled` auf `True` stehen. `SamAccountName` endet auf `$`. Diesen vollständigen Kontonamen im Aufgaben-Principal verwenden; er ist nicht das Computerkonto des Ausführungshosts.

Jeden Eintrag in `PrincipalsAllowedToRetrieveManagedPassword` betrachten. Dort können direkt Computerkonten oder Gruppen stehen. Verweist die Eigenschaft auf `gmsaExport-Hosts`, genau diese Gruppe prüfen:

```powershell
Get-ADComputer -Identity 'srv-job01' |
    Select-Object Name, DistinguishedName, Enabled
Get-ADGroupMember -Identity 'gmsaExport-Hosts' -Recursive |
    Select-Object Name, ObjectClass, DistinguishedName
```

Entscheidend ist der ausführende **Computer**, nicht der Administrator, der das Konto angelegt hat. Eine fehlende Berechtigung muss ein entsprechend berechtigter Domänenadministrator korrigieren. `PrincipalsAllowedToRetrieveManagedPassword` nicht blind durch einen einzelnen Host ersetzen: Die vorhandene Liste kann weitere aktive Verbraucher berechtigen.

Wurde ein Computer gerade einer Gruppe hinzugefügt, kann die Verzeichnisabfrage bereits die neue Mitgliedschaft zeigen, während sein bestehender Sicherheitskontext sie noch nicht enthält. AD-Replikation berücksichtigen und bei Bedarf einen Neustart des betroffenen Hosts einplanen. Das Löschen von System-Kerberos-Tickets ist keine harmlose Leseprüfung und kann andere Aufträge beeinflussen; es gehört hier nicht zum Standardablauf.

## 3. Die Domänenverbindung des Hosts prüfen

Auf dem Mitgliedsserver:

```powershell
Get-CimInstance Win32_ComputerSystem | Select-Object Name, Domain, PartOfDomain
Resolve-DnsName -Name '_ldap._tcp.dc._msdcs.contoso.com' -Type SRV
nltest.exe /dsgetdc:contoso.com
Test-ComputerSecureChannel -Verbose
w32tm.exe /query /status
```

`contoso.com` durch die tatsächliche AD-DNS-Domäne ersetzen. Domänenmitgliedschaft, auffindbare Domänencontroller und einen funktionsfähigen sicheren Kanal bestätigen. Zeitquelle und Synchronisationsinformationen prüfen; eine konfigurierte Quelle allein belegt noch keine übereinstimmenden Uhren. `Test-ComputerSecureChannel` ist hier für einen Mitgliedscomputer gedacht, nicht für einen Domänencontroller.

Native Programme können Fehler über Rückgabecode und Text melden. Scheitert eine Prüfung, das konkrete DNS-, Verbindungs-, Vertrauens- oder Zeitproblem beheben und den Kontotest wiederholen. `-Repair`, ein erneuter Domänenbeitritt oder gelockerte Kerberos-Einstellungen sind keine pauschalen ersten Schritte.

## 4. KDS-Bereitschaft und danach die lokale Installation prüfen

Auf einem Domänencontroller mit den nötigen Verwaltungsrechten:

```powershell
$keys = Get-KdsRootKey
foreach ($key in $keys) {
    $key | Select-Object KeyId, CreationTime, EffectiveTime
}
```

Ein neuer KDS-Stammschlüssel braucht Zeit, bis er über die Domänencontroller hinweg verwendbar ist. Microsoft dokumentiert bis zu zehn Stunden Wartezeit; das Beispiel mit Rückdatierung ist für eine Testumgebung mit einem einzelnen DC vorgesehen. Es ist keine allgemeine Reparatur für den Produktivbetrieb. Existiert bereits ein Schlüssel, keinen weiteren anlegen, nur weil ein Host scheitert. Siehe [KDS-Stammschlüssel erstellen](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/manage/group-managed-service-accounts/group-managed-service-accounts/create-the-key-distribution-services-kds-root-key).

Nach Prüfung von Berechtigung und Verzeichnisbereitschaft auf dem vorgesehenen Ausführungshost installieren und testen:

```powershell
Install-ADServiceAccount -Identity 'gmsaExport' -ErrorAction Stop
Test-ADServiceAccount -Identity 'gmsaExport' -Verbose -ErrorAction Stop
```

Scheitert die Installation, ihren genauen Fehlertext aufbewahren. Im Labor führte die fehlende Abrufberechtigung des Hosts zu `Access Denied`. Diese beobachtete Ursache macht nicht jeden Installationsfehler zu einem Berechtigungsfehler.

Verweist die Warnung auf Kerberos-Verschlüsselung, Kontoeinstellungen mit der unterstützten Verschlüsselungsrichtlinie der Domäne und den passenden Ereignissen vergleichen. Keinen älteren Verschlüsselungstyp aktivieren, nur damit die Warnung verschwindet. Microsofts [gMSA-Verwaltungsanleitung](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/manage/group-managed-service-accounts/group-managed-service-accounts/manage-group-managed-service-accounts) beschreibt die Voraussetzungen.

## 5. Eine Ursache ändern und dieselbe Prüfung wiederholen

Das Labor verwendete ein eigenes Test-gMSA mit direkter Computerberechtigung. Produktive Gruppen und Dienste wurden nicht verändert. Derselbe Mitgliedsserver lieferte:

| Konfiguration | `Test-ADServiceAccount` | `Install-ADServiceAccount` |
|---|---|---|
| Nur ein anderer Computer war berechtigt | `False` | Zugriff verweigert |
| Stattdessen war der geprüfte Computer berechtigt | Nach Installation `True` | Erfolgreich |

Weder Kontoneuanlage noch Austausch des KDS-Schlüssels oder Administratormitgliedschaft waren dafür nötig. Der Versuch grenzt eine Ursache ein; er deckt nicht jeden Replikations-, Gruppenmitgliedschafts- oder Verschlüsselungsfall ab. Bei Gruppenberechtigungen zusätzlich den erneuerten Sicherheitskontext des Computers berücksichtigen.

## 6. Bei erfolgreichem Kontotest dem scheiternden Auftrag folgen

Bei einer geplanten Aufgabe Principal, ausführbare Datei und letzten abgeschlossenen Lauf prüfen:

```powershell
$task = Get-ScheduledTask -TaskName 'GmsaExportProbe'
$task.Principal | Select-Object UserId, LogonType, RunLevel
$task.Actions | Select-Object Execute, Arguments, WorkingDirectory
Get-ScheduledTaskInfo -TaskName 'GmsaExportProbe' |
    Select-Object LastRunTime, LastTaskResult
```

`DOMÄNE\Konto$`, die gewünschte Laufzeit und einen passenden Anmeldetyp bestätigen. Startet der Prozess gar nicht, die wirksamen Stapelanmelderechte prüfen. Bei einem Windows-Dienst stattdessen sein konfiguriertes Konto, die Dienstanmelderechte und die Ereignisse des Service Control Managers betrachten. Das sind getrennte Anwendungsprüfungen.

Startet der Prozess, die scheiternde Operation anhand seiner eigenen Ausgabe bestimmen. Ein erfolgreicher Schreibversuch unter dem Administratorkonto sagt nichts über die Rechte des gMSA aus. Die [Anleitung zur geplanten Aufgabe](/blog/gmsa-scheduled-task/) enthält eine Probe, die ihre Identität protokolliert, eine eindeutige Datei schreibt und zurückliest.

Im zweiten Labortest blieb der Kontotest in beiden Fällen `True`. Mit Zielzugriff lieferte die Aufgabe `0`; ohne NTFS-Zugriff auf ein separates Testverzeichnis lieferte sie `1` und protokollierte den verweigerten Pfad. Hier fehlte eine Ressourcenberechtigung. KDS oder die Abrufberechtigung des Kontos zu ändern, hätte an der falschen Stelle angesetzt.

## Ein kurzes Diagnoseprotokoll führen

Host und Laufzeit, Kontoidentität, genauen fehlgeschlagenen Befehl, ursprüngliches Ergebnis, eine Konfigurationsänderung und das wiederholte Ergebnis gemeinsam festhalten. Sobald der Kontotest funktioniert, einen tatsächlichen Auftrag unter dem gMSA ausführen und dessen Ausgabe prüfen. Vor einer öffentlichen Weitergabe Domänennamen, Pfade und Kontodetails bereinigen; verwaltete Kennwörter gehören nie ins Protokoll.

Führt NodePilot den Auftrag aus, dessen Ausführungsprotokoll mit diesen Windows-Prüfungen zusammen betrachten. Die [Dokumentation zur Remote-Ausführung](/docs/de/configuration/remote-execution/) erläutert die Konfiguration. Ein grüner Kontotest ist eine Voraussetzung; erfolgreiche Arbeit braucht weiterhin einen Nachweis aus dem ausführenden Prozess.
