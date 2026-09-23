# Kleine Leseabfragen und Semantik

Jeden Codeblock als einzelne Prüfung auf dem fest zugewiesenen Ziel verwenden.
Platzhalter durch zuvor beobachtete literale Werte ersetzen. Namespace/Klasse/Feld
an der installierten Version prüfen. Die NodePilot-Hostpolicy entscheidet, welche
Abfrage zulässig ist; der Skill erweitert keine Rechte. Nicht alle nachstehenden
Diagnosewege stehen in jeder Installation zur Verfügung.

## Dienste und Quellen

```powershell
Get-CimInstance -ClassName Win32_Service -Filter "Name='CcmExec'" | Select-Object Name,State,StartMode,PathName
```

```powershell
Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate' | Select-Object WUServer,WUStatusServer
```

```powershell
Get-Content -LiteralPath 'C:\Windows\System32\drivers\etc\hosts'
```

```powershell
Select-String -LiteralPath 'C:\Windows\CCM\Logs\WUAHandler.log' -Pattern 'observed-update-guid','observed-error' -SimpleMatch -Context 3,3 | Select-Object LineNumber,Line,Context
```

Bei Listen von Eigenschaften getrennte Namen (`Name,State`), nicht einen einzelnen
String `'Name,State'` verwenden. Keine Vollserialisierung großer CIM-Objekte/Metadaten.

## Schema und Standort

```powershell
Get-CimInstance -Namespace root/SMS -ClassName SMS_ProviderLocation | Select-Object Machine,SiteCode,ProviderForLocalSite
```

```powershell
Get-CimClass -Namespace root/ccm/ClientSDK -ClassName CCM_SoftwareUpdate | Select-Object -ExpandProperty CimClassProperties | Select-Object Name,CimType
```

| Quelle | Schlüssel/Unterscheidung |
|---|---|
| SMS_UpdateComplianceStatus am Siteprovider | CI_ID und MachineID, nicht ResourceID |
| SMS_CIDeploymentUnknownAssetDetails | AssignmentID/AssignmentUniqueID + MachineID; CI_ID kann 0 sein |
| CCM_SoftwareUpdate in root/ccm/ClientSDK | UpdateID, nicht Site-CI_ID |
| CCM_UpdateStatus in root/ccm/SoftwareUpdates/UpdatesStore | UniqueId, Article, Status, ScanTime |
| CCM_UpdateCIAssignment in Policy/Machine/ActualConfig | AssignmentID, AssignedCIs, Deadline/StartTime/UseGMTTimes |
| SMS_Application / SMS_DeploymentType | Eigene CI-/Model-Identität und Revision; SDMPackageXML ist lazy |

SMS_Application/DeploymentType zuerst eng nach beobachteter Identität lesen und
vollständige Instanz nachladen. Ein leerer lazy Wert aus Enumeration ist kein leeres
Deployment. NodePilot führt das Instance-GET für diese freigegebenen Klassen aus;
andere Hosts benötigen ihren unterstützten Readpfad. SDMPackageXML nicht als
gewöhnliches SELECT-Projektionsfeld behandeln, wenn der Provider dies ablehnt.

## IIS und Zertifikate

```powershell
Get-Website | Select-Object Name,Id,State,PhysicalPath
```

```powershell
Get-WebBinding -Name 'observed-site' | Select-Object protocol,bindingInformation,certificateHash
```

```powershell
Get-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Location 'observed-site/ClientWebService' -Filter 'system.webServer/security/access' -Name sslFlags
```

```powershell
Get-Item -LiteralPath 'IIS:\AppPools\observed-pool' | Select-Object Name,State,recycling
```

```powershell
Get-ChildItem -LiteralPath 'Cert:\LocalMachine\TrustedPublisher' | Select-Object Subject,Thumbprint,NotBefore,NotAfter
```

```powershell
Get-AuthenticodeSignature -LiteralPath 'C:\observed-content\package.cab' | Select-Object Status,StatusMessage,SignerCertificate
```

Zertifikat-Metadaten lesen, niemals private Schlüssel exportieren. Modulabhängigkeiten
(IIS/WebAdministration) sind Hostvoraussetzungen. Fehlende Module nicht installieren.
Eventprovider zunächst mit Get-WinEvent -ListProvider und Logs mit -ListLog entdecken,
danach zeitlich begrenzt lesen; keine erfundenen Provider endlos erneut abfragen.

## Zustand und tatsächliche Datei

```powershell
Get-CimInstance -Namespace root/ccm/ClientSDK -ClassName CCM_ServiceWindow | Select-Object ID,Type,Duration,StartTime,EndTime
```

```powershell
Get-WindowsCapability -Online -Name 'observed-capability-name' | Select-Object Name,State
```

```powershell
Get-FileHash -LiteralPath 'C:\observed-library\FileLib\ABCD\observed-full-hash' -Algorithm SHA256
```

Pfad und Hashalgorithmus aus echter Zuordnung gewinnen, nicht dieses Muster wörtlich
benutzen. Ein Hashaufruf gegen Metadaten-INI prüft nur diese INI. Get-Item/Get-ChildItem
für Existenz, Größe und Inventar ergänzen. Agenten keine fremden UNC-Ziele erfinden
lassen; Servermitglied mit expliziter Zielbindung verwenden.
