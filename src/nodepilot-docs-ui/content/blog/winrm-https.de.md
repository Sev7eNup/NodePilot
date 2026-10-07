# Einen bestehenden WinRM-Zugang um HTTPS erweitern

Für `srv-app01.contoso.com` soll ein HTTPS-Zugang bereitgestellt werden. WinRM läuft bereits, und PowerShell Remoting ist eingerichtet. Damit konzentriert sich die Änderung auf das Serverzertifikat, seine Bindung an den Listener und die anschließende Prüfung vom Verwaltungsrechner.

HTTPS ergänzt den TLS-geschützten Transport und eine anhand des Zertifikats überprüfbare Serveridentität. Das ist insbesondere über Vertrauensgrenzen hinweg oder ohne Kerberos hilfreich. Die vorhandene Berechtigungsprüfung bleibt Teil des Zugriffs.

## 1. Das Zertifikat auswählen

Für eine Verbindung zu `srv-app01.contoso.com` muss das Zertifikat diesen Namen abdecken, üblicherweise über einen passenden Subject Alternative Name. Ein Zertifikat für einen anderen Namen wird nicht dadurch passend, dass beide DNS-Einträge auf dieselbe IP-Adresse zeigen.

Auf dem Zielsystem gehört das Serverzertifikat samt privatem Schlüssel in den persönlichen Zertifikatsspeicher des lokalen Computers. Es muss gültig und für Server Authentication vorgesehen sein. Auf dem Verwaltungsrechner muss sich seine Vertrauenskette prüfen lassen. Für den regulären Betrieb bietet sich ein Zertifikat der dafür vorgesehenen Unternehmens-PKI an. Microsoft beschreibt die erforderlichen Eigenschaften in der [Anleitung zur HTTPS-Konfiguration](https://learn.microsoft.com/en-us/troubleshoot/windows-client/system-management-components/configure-winrm-for-https).

Eine erste Bestandsaufnahme erfolgt lokal auf dem Zielserver:

```powershell
Get-ChildItem -Path Cert:\LocalMachine\My |
    Select-Object Subject, DnsNameList, NotAfter,
        HasPrivateKey, EnhancedKeyUsageList, Thumbprint
```

Die Ausgabe hilft bei der Auswahl. Sie ersetzt jedoch keine vollständige Prüfung der Zertifikatskette und des Sperrstatus vom späteren Verwaltungsrechner aus.

## 2. Den Listener anlegen und den Netzwerkzugang begrenzen

Der neue Listener ergänzt den Transport des bereits eingerichteten Remoting-Zugangs. Einen fehlenden PowerShell-Endpunkt registriert er nicht. Vor einer Änderung zeigt die Listener-Abfrage, ob bereits eine HTTPS-Konfiguration vorhanden ist:

```powershell
winrm enumerate winrm/config/listener
```

Ein neuer Listener lässt sich in einer erhöhten PowerShell auf dem Zielserver mit einem ausdrücklich ausgewählten Zertifikat anlegen. Rechnername und Fingerabdruck sind durch die eigenen Werte zu ersetzen. Der Befehl verändert die Konfiguration, aktualisiert aber keinen bereits vorhandenen Listener. [Microsoft: HTTPS-Listener mit New-WSManInstance](https://learn.microsoft.com/en-us/powershell/module/microsoft.wsman.management/new-wsmaninstance).

```powershell
$listenerConfig = @{
    ResourceURI = 'winrm/config/Listener'
    SelectorSet = @{
        Transport = 'HTTPS'
        Address   = '*'
    }
    ValueSet = @{
        Hostname              = 'srv-app01.contoso.com'
        CertificateThumbprint = '<Fingerabdruck des Serverzertifikats>'
    }
}

New-WSManInstance @listenerConfig
winrm enumerate winrm/config/listener
```

`Address='*'` bindet den Listener an alle verfügbaren lokalen Adressen. Welche Verwaltungsrechner ihn erreichen dürfen, muss zusätzlich über die Firewall begrenzt werden. Für den regulären HTTPS-Port ist eine passende Freigabe für TCP 5986 erforderlich, möglichst beschränkt auf die vorgesehenen Verwaltungsadressen und Netzwerkprofile.

Ein vorhandener HTTP-Listener verschwindet durch diese Ergänzung nicht. Soll künftig ausschließlich HTTPS zulässig sein, gehört die Abschaltung des bisherigen Zugangs in einen eigenen Umstellungsschritt, nachdem dessen Verbraucher auf HTTPS umgestellt und geprüft wurden. [Microsoft: WinRM-Listener und Konfiguration](https://learn.microsoft.com/en-us/windows/win32/winrm/installation-and-configuration-for-windows-remote-management).

## 3. Die Verbindung unter den späteren Bedingungen prüfen

Der Test sollte denselben DNS-Namen verwenden wie die spätere Automatisierung. Zunächst wird die TCP-Verbindung geprüft, anschließend eine authentifizierte WS-Management-Anfrage. Das Beispiel setzt eine Domänenumgebung mit funktionierendem Kerberos voraus:

```powershell
$target = 'srv-app01.contoso.com'

Test-NetConnection -ComputerName $target -Port 5986
Test-WSMan -ComputerName $target -UseSSL -Port 5986 `
    -Authentication Kerberos
```

Ohne `-UseSSL` prüft der Aufruf nicht die beabsichtigte HTTPS-Verbindung. Ebenso wichtig ist der Authentifizierungsparameter: Ohne dessen Angabe sendet `Test-WSMan` eine anonyme Identifikationsanfrage. Eine Antwort belegt dann keine erfolgreiche Benutzeranmeldung. [Microsoft: Test-WSMan](https://learn.microsoft.com/en-us/powershell/module/microsoft.wsman.management/test-wsman).

Erst eine tatsächliche Remotesitzung prüft zusätzlich den Zugriff auf den ausgewählten PowerShell-Endpunkt. Im folgenden Beispiel muss `Microsoft.PowerShell` auf dem Zielsystem vorhanden und für den Benutzer freigegeben sein:

```powershell
Invoke-Command -ComputerName $target -UseSSL -Port 5986 `
    -Authentication Kerberos -ConfigurationName 'Microsoft.PowerShell' `
    -ScriptBlock {
        [pscustomobject]@{
            Computer = $env:COMPUTERNAME
            Identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
            Version  = $PSVersionTable.PSVersion.ToString()
        }
    }
```

Der Endpunkt besitzt eigene Zugriffsrechte. Ein erfolgreicher TLS-Verbindungsaufbau sagt daher noch nichts darüber aus, ob das verwendete Konto dort Befehle ausführen darf. [Microsoft: PowerShell-Sitzungskonfigurationen](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_session_configurations).

## Die Anmeldung bleibt eine eigene Entscheidung

Auch über HTTP schützt WinRM die PowerShell-Nachrichten nach einer Kerberos-Authentifizierung durch Verschlüsselung auf Nachrichtenebene. HTTPS verwendet zusätzlich TLS, wobei das Serverzertifikat die Prüfung der Gegenstelle ermöglicht. Die Anmeldung des Benutzers bleibt eine eigene Aufgabe. Ein HTTPS-Listener erteilt somit weder Zugriffsrechte noch löst er automatisch ein Delegierungsproblem. [Microsoft: Sicherheitsbetrachtung zu PowerShell Remoting](https://learn.microsoft.com/en-us/powershell/scripting/security/remoting/winrm-security).

In einer Domäne können HTTPS und Kerberos gemeinsam verwendet werden. Außerhalb dieses Rahmens muss das Anmeldeverfahren gesondert festgelegt sein. Dabei ist zwischen dem Serverzertifikat für TLS und einer ausdrücklich eingerichteten Clientzertifikatsauthentifizierung zu unterscheiden. Die Bereitstellung des ersten aktiviert nicht automatisch die zweite.

## Nach der Einrichtung beginnt die Zertifikatspflege

Bei einem Namensfehler ist der DNS-Name mit dem Zertifikat abzugleichen. Für eine fehlgeschlagene Vertrauensprüfung müssen die Zertifikatskette und ihre Verfügbarkeit auf dem Verwaltungsrechner untersucht werden. Die Zertifikatsprüfung dauerhaft zu überspringen, würde diese Ursachen bestehen lassen.

Auch nach einer Erneuerung ist zu kontrollieren, welches Zertifikat der Listener tatsächlich verwendet. Dessen `CertificateThumbprint` muss mit dem ausgewählten Zertifikat übereinstimmen. Ein gültiges Zertifikat im Server-Store allein belegt diese Bindung nicht. [Microsoft: Zertifikat und Listener abgleichen](https://learn.microsoft.com/en-us/troubleshoot/windows-client/system-management-components/configure-winrm-for-https).

Der abschließende Test erfolgt wieder vom vorgesehenen Verwaltungsrechner und umfasst eine tatsächlich geöffnete Sitzung. Fingerabdruck und Ablaufdatum gehören zu den kontrollierten Angaben. Für einen später über NodePilot genutzten Zugang sollte dieser Test auch nach der nächsten Zertifikatserneuerung zum Betriebsablauf gehören.
