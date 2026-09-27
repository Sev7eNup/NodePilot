# WinRM verstehen: Was eine erfolgreiche Verbindung tatsächlich belegt

`Test-WSMan` liefert eine Antwort. Damit ist zunächst nur festgestellt, dass der angesprochene WinRM-Dienst auf eine Identifikationsanfrage reagiert hat. Für die Diagnose kommt es darauf an, welche weiteren Voraussetzungen der konkrete Aufruf überhaupt überprüft.

Ohne ausdrücklich angegebenen Authentifizierungsparameter sendet `Test-WSMan` seine Identifikationsanfrage anonym. Eine Antwort belegt daher keine erfolgreiche Benutzeranmeldung. Auch der authentifizierte Test ersetzt noch nicht das Öffnen des vorgesehenen PowerShell-Endpunkts. [Microsoft: Verhalten von Test-WSMan](https://learn.microsoft.com/en-us/powershell/module/microsoft.wsman.management/test-wsman).

Zwischen dem erreichbaren Dienst und einer zulässigen Aktion liegen getrennte Prüfungen. Aus dieser Trennung ergibt sich ein Weg durch die Konfiguration, bei dem Zugriffsrechte erst dort untersucht werden, wo sie tatsächlich eine Rolle spielen.

Die Beispiele beziehen sich auf Windows mit Active Directory und PowerShell Remoting über WS-Management. Rechnernamen und Pfade sind Platzhalter. Befehle zur Diagnose sind von Änderungen an der Konfiguration ausdrücklich getrennt.

## Vom Client zur Identität der Gegenstelle

Windows Remote Management ist Microsofts Implementierung von WS-Management. PowerShell Remoting nutzt diesen Verwaltungsdienst als Transport, während die Sitzung selbst in einem PowerShell-Endpunkt auf dem Zielsystem ausgeführt wird. Daraus ergeben sich mehrere unabhängig prüfbare Voraussetzungen. [Microsoft: Windows Remote Management](https://learn.microsoft.com/en-us/windows/win32/winrm/portal).

| Ebene | Was vorhanden sein muss | Was ein Erfolg noch nicht belegt |
|---|---|---|
| Netzwerk | Erreichbarer Zielport und passende Firewallregeln | Dass der erwartete Dienst antwortet |
| WinRM | Laufender Dienst und geeigneter Listener | Dass der Benutzer authentifiziert werden kann |
| Authentifizierung | Akzeptierte Identität und passendes Verfahren | Dass der PowerShell-Endpunkt zugänglich ist |
| Sitzungsendpunkt | Registrierte Konfiguration und Zugriffsrecht | Dass die gewünschte Fachoperation erlaubt ist |
| Zielressource | Rechte auf Dienst, Datei oder Anwendung | Dass ein weiterer Rechner dieselbe Identität akzeptiert |

Die regulären Listener verwenden TCP 5985 für HTTP und TCP 5986 für HTTPS. Ein laufender Dienst allein sagt noch nichts über einen verwendbaren Listener aus. Lokal auf dem Zielsystem zeigen folgende Abfragen den tatsächlichen Stand, einschließlich der gebundenen Adressen. [Microsoft: Installation und Konfiguration von WinRM](https://learn.microsoft.com/en-us/windows/win32/winrm/installation-and-configuration-for-windows-remote-management).

```powershell
Get-Service -Name WinRM
winrm enumerate winrm/config/listener
winrm get winrm/config/service
```

Gerade bei mehreren Netzwerkkarten lohnt sich der Vergleich zwischen der erwarteten Verwaltungsadresse und `ListeningOn`. Eine Konfiguration kann formal vorhanden sein, während der angesprochene Netzwerkpfad daran vorbeiführt.

### Listener-Adressen und zugelassene Quellrechner

Für die zentrale Bereitstellung liegt die Richtlinie „Allow remote server management through WinRM“ unter der Computerkonfiguration in den administrativen Vorlagen, „Windows Components“, „Windows Remote Management (WinRM)“, „WinRM Service“.

Ihre IPv4- und IPv6-Filter bestimmen, auf welchen lokalen Adressen der Zielrechner lauscht. Sie sind keine Liste zugelassener Verwaltungsrechner. Ein `*` bedeutet alle verfügbaren lokalen Adressen, ein leerer Filter keine Adresse der betreffenden Familie. Diese Unterscheidung ist entscheidend, wenn ein Server mehrere Netzsegmente bedient. [Microsoft: Richtlinie AllowRemoteServerManagement](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-remotemanagement#allowremoteservermanagement).

Die Beschränkung eingehender Verbindungen auf Verwaltungsnetze gehört in die Firewall. Die HTTP-Richtlinie erzeugt außerdem nicht automatisch einen HTTPS-Listener samt Zertifikat. Dienstkonfiguration, Listener und Firewallregeln müssen als zusammengehörige, aber getrennte Einstellungen verwaltet werden.

Für eine Bestandsaufnahme sind das aktive Netzwerkprofil und die resultierenden Richtlinien ebenso relevant wie die lokal sichtbare Konfiguration:

```powershell
Get-NetConnectionProfile
gpresult /Scope Computer /r
winrm get winrm/config
```

Zeigt WinRM bei einer Einstellung `Source="GPO"`, ist eine lokale Änderung nicht die maßgebliche Korrektur. Auch eine Firewallfreigabe kann je nach aktivem Netzwerkprofil unterschiedlich wirken. Vor dem nächsten Versuch sollte daher geklärt sein, welche Richtlinie und welches Profil tatsächlich greifen. [Microsoft: Fehlerbehebung bei Remoting](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_remote_troubleshooting).

Für die Einführung empfiehlt sich daraus ein gestaffeltes Vorgehen: zunächst eine begrenzte Servergruppe, anschließend ein Verbindungstest aus dem vorgesehenen Verwaltungsnetz und ein Gegenversuch aus einem nicht freigegebenen Netz. So wird neben der Funktion auch die beabsichtigte Begrenzung überprüft.

### Verschlüsselung und Serveridentität

HTTP bedeutet bei PowerShell Remoting mit Kerberos nicht, dass Befehle und Ergebnisse im Klartext übertragen werden. Nach der Authentifizierung schützt WinRM die Nachrichten auf Protokollebene. HTTPS ergänzt den TLS-Transport und ermöglicht die Prüfung der Serveridentität anhand eines Zertifikats. Transport und Authentifizierung sind getrennte Entscheidungen. [Microsoft: Sicherheitsbetrachtung zu PowerShell Remoting](https://learn.microsoft.com/en-us/powershell/scripting/security/remoting/winrm-security).

Für domänengebundene Systeme mit funktionierendem Kerberos kann HTTP daher eine begründete Betriebsentscheidung sein. HTTPS bietet sich insbesondere an, wenn Kerberos nicht zur Verfügung steht oder die Netzvorgaben TLS verlangen. Basic liefert selbst keine Nachrichtenverschlüsselung. Die Kombination aus Basic, HTTP und `AllowUnencrypted=true` gibt diesen Schutz auf und eignet sich nicht als Fehlerbehebung. [Microsoft PowerShell Team: Risiken von AllowUnencrypted](https://devblogs.microsoft.com/powershell/compromising-yourself-with-winrms-allowunencrypted-true/).

Auch `TrustedHosts` wird leicht missverstanden. Der Eintrag bestätigt keine Serveridentität, sondern unterdrückt die entsprechende Prüfung für die eingetragenen Ziele bei Verbindungen, deren Gegenstelle sich darüber nicht verifizieren lässt. Er erteilt weder Rechte auf dem Server noch ersetzt er eine Firewallregel. Ein pauschales `*` erweitert diese Ausnahme auf beliebige Ziele. [Microsoft: TrustedHosts und Serveridentität](https://learn.microsoft.com/en-us/powershell/scripting/security/remoting/winrm-security).

Für HTTPS gehört ein geeignetes Serverzertifikat in den Zertifikatsspeicher des lokalen Computers. Der verwendete DNS-Name muss zum Zertifikat passen, dessen Gültigkeit und Vertrauenskette der Client prüfen können muss. Die erweiterte Schlüsselverwendung muss Server Authentication einschließen. Microsoft beschreibt diese Voraussetzungen einschließlich der Prüfung von Subject Alternative Names in der [Anleitung zur HTTPS-Konfiguration](https://learn.microsoft.com/en-us/troubleshoot/windows-client/system-management-components/configure-winrm-for-https).

Im Betrieb gehört dazu ein Zuständiger für die Zertifikatserneuerung. Nach einem Austausch sollte geprüft werden, welches Zertifikat tatsächlich am Listener gebunden ist und ob der Verwaltungsrechner die Verbindung weiterhin akzeptiert. Ein neu ausgestelltes Zertifikat im Store allein ist noch kein erfolgreicher Funktionstest.

### Kerberos und die SPN-Zuordnung

Für die Diagnose ist ein vollständiger DNS-Name die bessere Ausgangsbasis als eine IP-Adresse. Mit `Negotiate` darf die Verbindung zwischen Kerberos und NTLM aushandeln. Wird dagegen ausdrücklich `Kerberos` angegeben, kann ein erfolgreicher NTLM-Fallback einen Kerberos-Fehler nicht verdecken. [Microsoft: Authentifizierungsverfahren von Test-WSMan](https://learn.microsoft.com/en-us/powershell/module/microsoft.wsman.management/test-wsman).

```powershell
$target = 'srv-app01.contoso.com'

Resolve-DnsName -Name $target
Test-NetConnection -ComputerName $target -Port 5985
Test-WSMan -ComputerName $target -Authentication Kerberos
```

Diese Prüfungen sollten vom tatsächlichen Verwaltungsrechner erfolgen. Wenn eine spätere Automatisierung mit einer anderen Identität arbeitet, ist zusätzlich unter deren Bedingungen zu testen. Eine erfolgreiche Sitzung vom Administratorarbeitsplatz belegt nicht, dass der ausführende Dienst über denselben Netzwerkzugang und dieselben Rechte verfügt.

Ein besonders aufschlussreicher Fehler ist `0x80090322`. PowerShell verwendet für WinRM standardmäßig einen SPN im Format `HTTP/<FQDN>`. Ist dieser einem anderen Dienstkonto zugeordnet, etwa für eine Webanwendung, kann die Kerberos-Anmeldung scheitern. Ein erster lesender Prüfpunkt ist daher:

```powershell
setspn -Q HTTP/srv-app01.contoso.com
```

Eine gefundene Zuordnung muss mit dem vorgesehenen Dienstkonto abgeglichen werden. Sie sollte nicht allein deshalb gelöscht werden, weil WinRM einen Fehler meldet. Microsoft beschreibt unter anderem portgebundene SPNs in Verbindung mit `IncludePortInSPN` als Lösung für bestimmte Konflikte. Das ist eine abgestimmte Konfigurationsänderung, kein allgemeiner Reparaturschalter. [Microsoft: WinRM-Fehler 0x80090322](https://learn.microsoft.com/en-us/troubleshoot/windows-server/system-management-components/error-0x80090322-when-connecting-powershell-to-remote-server-via-winrm).

## Die Sitzung und ihre Berechtigungen

Ein erreichbarer WinRM-Dienst garantiert noch keine PowerShell-Sitzung. Sitzungskonfigurationen besitzen eigene Sicherheitsbeschreibungen, über die der Zugriff auf den jeweiligen Endpunkt geregelt wird. Danach gelten weiterhin die Berechtigungen auf die tatsächlich angesprochenen Ressourcen. [Microsoft: Sitzungskonfigurationen](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_session_configurations).

Lokal auf dem Zielsystem, in einer erhöhten PowerShell, lassen sich die registrierten Konfigurationen anzeigen:

```powershell
Get-PSSessionConfiguration |
    Select-Object Name, PSVersion, Permission
```

Bei parallel installiertem Windows PowerShell 5.1 und PowerShell 7 muss außerdem feststehen, welche Laufzeit der Auftrag verwenden soll. `Enable-PSRemoting` richtet Endpunkte für die PowerShell-Installation ein, in der der Befehl ausgeführt wird. Die Ausführung in PowerShell 7 ersetzt nicht automatisch die Konfiguration von Windows PowerShell. Der Befehl verändert Dienst-, Firewall- und Sitzungseinstellungen und gehört deshalb in die geplante Bereitstellung. [Microsoft: Enable-PSRemoting](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/enable-psremoting).

Der folgende Test setzt voraus, dass `PowerShell.7` registriert und für den Benutzer zugänglich ist:

```powershell
Invoke-Command -ComputerName 'srv-app01.contoso.com' `
    -Authentication Kerberos -ConfigurationName 'PowerShell.7' `
    -ScriptBlock {
        [pscustomobject]@{
            Computer = $env:COMPUTERNAME
            Identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
            Version  = $PSVersionTable.PSVersion.ToString()
            Edition  = $PSVersionTable.PSEdition
        }
    }
```

Damit werden Zielrechner, ausführende Identität und Laufzeit in derselben Sitzung sichtbar. Das ist aussagekräftiger als die Annahme, dass eine lokal installierte PowerShell-Version auch automatisch auf dem entfernten Rechner verwendet wird.

### Eine begrenzte Schnittstelle mit JEA

Ein Betriebskonto, das einen bestimmten Dienst prüfen soll, benötigt dafür nicht zwangsläufig einen uneingeschränkten administrativen Endpunkt. Just Enough Administration, kurz JEA, erlaubt die Bereitstellung begrenzter Verwaltungsfunktionen. In einer Role Capability können sowohl Cmdlets als auch deren erlaubte Parameterwerte festgelegt werden. [Microsoft: JEA Role Capabilities](https://learn.microsoft.com/en-us/powershell/scripting/security/remoting/jea/role-capabilities).

Ein Ausschnitt für eine ausschließlich lesende Dienstabfrage könnte so aussehen:

```powershell
@{
    VisibleCmdlets = @{
        Name = 'Get-Service'
        Parameters = @{
            Name = 'Name'
            ValidateSet = 'Spooler'
        }
    }
}
```

Das ist noch keine vollständige Endpunktkonfiguration. Die Role Capability muss als `.psrc` in einem geeigneten Modul bereitgestellt und einer Rolle in der Sitzungskonfiguration zugeordnet werden. Der Ausschnitt begrenzt den Wert eines angegebenen `Name`-Parameters. Er macht den Parameter jedoch nicht verpflichtend: Ohne `Name` könnte `Get-Service` weiterhin die Dienste auflisten. Soll ausschließlich ein festgelegter Dienst sichtbar sein, ist eine eigene Wrapper-Funktion mit fest eingebautem Dienstnamen die engere Schnittstelle. Breite Freigaben wie `*-Service` oder beliebige ausführbare Programme würden diese Begrenzung unterlaufen.

Zur JEA-Konfiguration gehört außerdem die Ausführungsidentität. Virtuelle Konten erlauben lokale Verwaltungsaufgaben, während ein gMSA für benötigte Netzwerkzugriffe eine eigene Domänenidentität bereitstellen kann. Bei gemeinsam genutzter Identität ist die Zuordnung einer Aktion zum ursprünglichen Benutzer auf zusätzliche Protokollierung angewiesen. [Microsoft: JEA-Sitzungskonfiguration](https://learn.microsoft.com/en-us/powershell/scripting/security/remoting/jea/session-configurations).

Die Standardrechte virtueller Konten verdienen besondere Aufmerksamkeit: Auf Mitgliedsservern gehören sie standardmäßig zu den lokalen Administratoren, auf Domänencontrollern zu Domain Admins. Abweichende Gruppenzuordnungen lassen sich ausdrücklich festlegen. Eine Konfiguration für einen Mitgliedsserver sollte daher nicht unverändert auf einen Domänencontroller übertragen werden. [Microsoft: Sicherheitsbetrachtung zu JEA](https://learn.microsoft.com/en-us/powershell/scripting/security/remoting/jea/security-considerations).

### Der Zugriff auf einen dritten Rechner

Der typische Fall beginnt auf Verwaltungsrechner A. Von dort wird eine Sitzung zu Server B geöffnet, in der ein Befehl auf eine Freigabe von Server C zugreifen soll. Die erste Anmeldung funktioniert, der zweite Zugriff wird verweigert, weil die ursprünglichen Anmeldeinformationen nicht einfach weitergereicht werden.

Bevor Delegierung eingerichtet wird, muss der zweite Zugriff feststehen. Für SMB und eine weitere WinRM-Sitzung gelten nicht dieselben Voraussetzungen. Microsoft nennt sowohl bei klassischer als auch bei ressourcenbasierter eingeschränkter Kerberos-Delegierung ausdrücklich die Grenze, dass der zweite Hop für WinRM nicht unterstützt wird. Eine erfolgreiche Dateifreigabe ist deshalb kein Nachweis für funktionierendes verschachteltes Remoting. [Microsoft: Second Hop in PowerShell Remoting](https://learn.microsoft.com/en-us/powershell/scripting/security/remoting/ps-remoting-second-hop).

CredSSP kann Anmeldeinformationen an Server B delegieren, vergrößert damit aber dessen Vertrauensumfang. Bei einer Kompromittierung von B sind auch diese Anmeldeinformationen gefährdet. Es sollte daher nicht allein deshalb eingeschaltet werden, weil ein Skript auf einer Freigabe „Access denied“ meldet. Derselbe Microsoft-Artikel erläutert die jeweiligen Alternativen und Grenzen.

Häufig lässt sich der Ablauf so aufteilen, dass A beide Ressourcen direkt anspricht. Liegt beispielsweise eine Konfigurationsdatei bereits auf A, kann sie über eine vorhandene Sitzung nach B übertragen werden. Für diese Übertragung braucht B keine Anmeldung an C. `Copy-Item` unterstützt dafür `-ToSession`. [Microsoft: Copy-Item mit Remotesitzungen](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.management/copy-item).

Das folgende Beispiel schreibt eine Datei auf den Zielrechner und kann eine gleichnamige Zieldatei überschreiben. Es setzt eine vorhandene Quelldatei und einen bestehenden, beschreibbaren Zielordner voraus:

```powershell
$session = New-PSSession -ComputerName 'srv-app01.contoso.com' `
    -Authentication Kerberos -ErrorAction Stop

try {
    Copy-Item -LiteralPath 'C:\Staging\appsettings.json' `
        -Destination 'C:\ProgramData\ContosoApp\appsettings.json' `
        -ToSession $session -ErrorAction Stop
}
finally {
    Remove-PSSession -Session $session
}
```

Diese Variante löst den konkreten Dateitransfer. Sie verschafft dem entfernten Prozess keine allgemeine Identität für weitere Netzwerkzugriffe. Benötigt die Anwendung selbst dauerhaft eine Freigabe, muss deren Dienstidentität dafür passend berechtigt sein.

## Daten und Last einer laufenden Sitzung

Übertragene PowerShell-Objekte sind in der Regel serialisierte Zustandsaufnahmen. Nach der Deserialisierung stehen ihre Eigenschaften zur Verfügung, aber nicht die ursprünglichen Methoden des lebenden Objekts. Eine lokal empfangene Dienstinformation ist deshalb kein Stellvertreter, auf dem beliebige Methoden weiterhin auf dem Server ausgeführt würden. [Microsoft: Ausgabe entfernter Befehle](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_remote_output).

Die benötigte Operation gehört in den entfernten ScriptBlock. Zurückgegeben werden möglichst kleine, für die Weiterverarbeitung geeignete Ergebnisse:

```powershell
Invoke-Command -ComputerName 'srv-app01.contoso.com' `
    -Authentication Kerberos -ScriptBlock {
        Get-Service -Name Spooler -ErrorAction Stop |
            Select-Object Name, Status, StartType
    }
```

Formatierungen wie `Format-Table` sind erst bei der abschließenden Darstellung sinnvoll. Für den Datenaustausch sollten die benötigten Eigenschaften erhalten bleiben, statt bereits auf dem Zielsystem eine für Menschen formatierte Ausgabe zu erzeugen.

### Quoten im Zusammenspiel mehrerer Aufträge

Ein einzelner erfolgreicher Aufruf sagt wenig darüber aus, wie sich hundert gleichzeitige Aufträge verhalten. WinRM begrenzt unter anderem die Zahl der Shells pro Benutzer sowie Prozesse und Speicher pro Shell. Effektive Quoten lassen sich auf dem Zielsystem auslesen:

```powershell
winrm get winrm/config/winrs
```

Gruppenrichtlinien können lokale Quoten übersteuern. Deshalb sollten die Werte der eigenen Umgebung geprüft werden, bevor eine vermeintliche Standardeinstellung als Ursache angenommen wird. [Microsoft: Quoten für Remote Shells](https://learn.microsoft.com/en-us/windows/win32/winrm/quotas).

`Invoke-Command -ThrottleLimit` begrenzt die Parallelität des jeweiligen Aufrufs. Mehrere gleichzeitig gestartete Aufrufe erhalten dadurch noch keine gemeinsame Obergrenze. [Microsoft: Invoke-Command](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/invoke-command).

Für eine zentrale Automatisierung folgt daraus eine betriebliche Anforderung: Parallelität muss auch über Aufträge hinweg begrenzt sein. Andernfalls vervielfachen mehrere Scheduler oder Skriptinstanzen die Last, obwohl jeder einzelne Aufruf einen zurückhaltenden Wert verwendet. Zunächst sollten gleichzeitige Sitzungen, Laufzeiten und Fehler je Ziel erfasst werden. Eine höhere Quote ist erst dann begründbar, wenn der tatsächliche Engpass feststeht.

## Befunde zuordnen und protokollieren

Bei der Fehlersuche hilft eine feste Reihenfolge, in der jeder Schritt eine andere Voraussetzung überprüft. Ist eine Ebene bereits bestätigt, kann die Untersuchung beim nächsten Übergang ansetzen, ohne funktionierende Einstellungen vorsorglich zu verändern.

| Beobachtung | Nächster Prüfpunkt |
|---|---|
| DNS liefert eine unerwartete Adresse | Namensauflösung und vorgesehenen Verwaltungsnamen abgleichen |
| TCP 5985 oder 5986 ist nicht erreichbar | Routing, Firewallprofil und Listener-Adresse untersuchen |
| WS-Management antwortet, Kerberos scheitert | Verwendete Identität und SPN-Zuordnung prüfen |
| Authentifizierter Test gelingt, Sitzung scheitert | Endpunktname und dessen Zugriffsrechte prüfen |
| Sitzung funktioniert, Fachoperation scheitert | Effektive Identität und Rechte an der Zielressource kontrollieren |
| Nur der Zugriff von B auf C scheitert | Protokoll und Identität des zweiten Hops bestimmen |
| Fehler treten erst bei vielen Aufträgen auf | Gemeinsame Parallelität und effektive Quoten erfassen |

Für eine HTTPS-Verbindung muss bereits der Test den richtigen Transport verwenden:

```powershell
Test-WSMan -ComputerName 'srv-app01.contoso.com' `
    -UseSSL -Port 5986 -Authentication Kerberos
```

Dieser Test setzt eine Umgebung voraus, in der Kerberos möglich ist. In einer Arbeitsgruppe ist das Authentifizierungsverfahren entsprechend anders zu planen. Ein HTTPS-Listener allein ersetzt keine passende Benutzeranmeldung.

Zur Betriebsdiagnose gehört schließlich die Protokollierung der tatsächlichen PowerShell-Laufzeit. Windows PowerShell 5.1 verwendet für Script Block Logging `Microsoft-Windows-PowerShell/Operational`, PowerShell 7 dagegen `PowerShellCore/Operational`. Bei aktiviertem Script Block Logging erscheinen dort Ereignisse mit der ID 4104. Die Konfiguration und Verfügbarkeit sollten für beide Laufzeiten separat geprüft werden. [Microsoft: Windows PowerShell Logging](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_logging?view=powershell-5.1), [Microsoft: PowerShell Logging unter Windows](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_logging_windows).

Solche Protokolle können Skriptinhalte und damit vertrauliche Angaben enthalten. Zugriff, Weiterleitung und Aufbewahrung gehören deshalb zur Logging-Konfiguration. Microsoft beschreibt Protected Event Logging als Möglichkeit, sensible Ereignisinhalte zu schützen. Für die Auswertung sollten Auftragskennung, Zielrechner und Zeitstempel so erfasst sein, dass sich ein Fehler der betreffenden Ausführung zuordnen lässt.

Für einen über NodePilot gestarteten Workflow lässt sich dieselbe Zuordnung nutzen: Zielrechner und Ausführung bestimmen, die betroffene Verbindungsebene prüfen und erst danach die passende Einstellung korrigieren. Die Authentifizierung und die Rechte auf dem Windows-System gelten auch für diese Ausführung.
