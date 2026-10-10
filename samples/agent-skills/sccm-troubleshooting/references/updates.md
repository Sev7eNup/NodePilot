# Scan, Compliance und Installationsplanung

## 15 — Software-Update-Scan schlägt fehl

**Symptom:** Kein erfolgreicher Scanabschluss; etwa 0x80244017, 0x8024401C,
0x80244022 oder 0x80240438 in WUAHandler/ScanAgent. Kein Code beweist allein die Ursache.
**Prüfen:** Aktuellen Scanjob, SUP/WSUS-Endpunkt und Windows-Update-Detailfehler
ermitteln. Vorhandene WindowsUpdate-Textlogs/Events lesen, nicht nur Wrapperfehler.
Dienst, effektive Quelle, DNS/hosts, Proxy, Firewall, IIS und Authentifizierung anhand
der scheiternden Schicht unterscheiden. Vorherige erfolgreiche Scans zeitlich trennen.
**Behebung:** Den konkret nachgewiesenen Blocker aus Dienst-/Netzwerk-/IIS-Anleitung
beheben. Kein pauschaler WUA-, SoftwareDistribution- oder WMI-Reset aus dem HRESULT.
**Kontrolle:** Nach autorisiertem neuen Scan ein nativer erfolgreicher Abschluss
für dieselbe Quelle; danach Complianceübermittlung separat prüfen.

## 16 — Konflikt der Scanquelle je Updateklasse

**Symptom:** Erwartete WSUS-Updates fehlen oder Suche nutzt eine andere Quelle.
**Prüfen:** Betriebssystemstand, verwaltete Updateklassen und effektive Richtlinien
ermitteln. WUServer/UseWUServer allein reichen nicht: Feature-, Quality-, Driver-
und Other-Update-Quellen sowie UseUpdateClassPolicySource und ihre tatsächliche
Policyherkunft prüfen. Erwartete Quelle gegen aktuellen nativen Scanrequest abgleichen.
Registryabweichung ohne betroffenen Scan belegt lediglich eine Konfigurationsabweichung.
**Behebung:** Konflikt in GPO/MDM/ConfigMgr-Zuständigkeit auflösen, gewünschte Quelle
je Klasse konsistent setzen. Keine Policywerte nur lokal übersteuern.
**Kontrolle:** Frischer administrativ gestarteter Scan für betroffene Klasse nutzt
gewollte Quelle und liefert erwartete Metadaten. Ohne passenden Katalog bleibt offen.

## 17 — Update-Compliance „Unknown“

**Symptom:** Client/Deployment bleibt Unknown, ScanAgent ohne gültigen Abschluss.
**Prüfen:** Assignment und dessen AssignedCIs zum Update zuordnen; Client Policy und
letzten Scan verfolgen. SMS_UpdateComplianceStatus nach CI_ID + MachineID abfragen.
Unbekannte Assignment-Assets können in SMS_CIDeploymentUnknownAssetDetails mit CI_ID=0
liegen: MachineID + Assignment(-Unique)ID verwenden und AssignedCIs separat verknüpfen.
Leere CI-Abfrage ist kein Gegenbeweis. Fehlenden/fehlgeschlagenen Scan, fehlende Policy
und verlorene/verzögerte State-Verarbeitung auseinanderhalten.
**Behebung:** Nachgewiesenen Scan-/Policy-/Reportingblocker beheben. Supersedence allein
ist keine Erklärung. Ein späterer CI-Status beweist nicht automatisch gesunden Scan.
**Kontrolle:** Erfolgreicher Scan, passende Meldung und richtige serverseitige
Zuordnung; Assignment-Gruppenstatus und Einzel-CI-Status separat bewerten.

## 18 — Required, aber Deadline/Verfügbarkeit noch nicht erreicht

**Symptom:** Fehlendes Update zugewiesen, automatische Installation startet nicht.
**Prüfen:** Updateanwendbarkeit, tatsächliche Clientzuweisung, Available/StartTime,
EnforcementDeadline, UseGMTTimes und Zielzeitzone mit UpdatesDeployment abgleichen.
Deadline ist kein Ablaufdatum. Eine zukünftige Deadline kann erwartetes Verhalten
erklären; ein bereits vergangener Termin widerlegt dieses Argument.
**Behebung:** Bei gewolltem zukünftigen Termin keine Reparatur. Falls Termin fachlich
falsch, nur die betroffene Zuweisung nach Freigabe korrigieren.
**Kontrolle:** Passende Planungsentscheidung; tatsächliche Installation erst nach
erreichter Freigabe/Deadline und allen weiteren Voraussetzungen bewerten.

## 19 — Wartungsfenster fehlt, ist falsch typisiert oder zu kurz

**Symptom:** Required und fällig, aber kein Start; etwa assignmentbezogen 0x87D00667.
**Prüfen:** ServiceWindowManager und passende Update-Assignment-Entscheidung lesen.
Effektive Clientfenster mit Sammlungskonfiguration, maximaler Updatelaufzeit und
Restzeit des Fensters vergleichen. Überlappungen, OverrideServiceWindows und
Deployment-Einstellungen berücksichtigen. Per-Update ErrorCode=0 widerlegt keinen
vorherigen Assignment-Block. Nicht versehentlich ein unbeteiligtes Fenster vergleichen.

| Datenquelle | Bedeutung |
|---|---|
| CCM_ServiceWindow Duration | Sekunden |
| SMS_ServiceWindow Duration | Minuten; eingebettetes Serverobjekt |
| CCM_ServiceWindow Type 1 | Alle Deployments |
| Type 4 | Softwareupdates |
| Type 5 | Tasksequenzen |
| Type 6 | Nichtarbeitszeit, kein Softwareupdatefenster |

Rohzeit, Type, Fenster-ID und konkrete Zuweisung behalten. Eine maximal erforderliche
Laufzeit von 3.600 s passt nicht in ein allein anwendbares Fenster mit 1.800 s.
Das ist ein Beispiel für den Vergleich, keine pauschale Sollkonfiguration.
**Behebung:** Passendes Wartungsfenster verlängern/ergänzen oder einen zulässigen
Termin nutzen. Maximallaufzeit nur nach begründeter fachlicher Prüfung ändern;
nicht künstlich verkürzen oder Fenster global umgehen, nur um Start zu erzwingen.
**Kontrolle:** Derselbe Assignment akzeptiert ein ausreichend langes gültiges
Fenster und die autorisierte Installation endet erfolgreich.

## 20 — Deployment nicht anwendbar, falscher Zielumfang oder CI-Lebenszyklus

**Symptom:** Erwartetes Update wird nicht angeboten oder installiert.
**Prüfen:** Zielmitgliedschaft, aktive Policy, Update-GUID/Revision, IsExpired,
IsSuperseded, Applicability und gewünschte Version. IsLatest und IsSuperseded können
gleichzeitig wahr sein. Erfolgreicher Scan bedeutet nicht, dass jedes Update passt.
**Behebung:** Nur belegte Fehlzuweisung/fehlende Voraussetzung oder ungewollte
Versionsauswahl korrigieren. Ein superseded Update nicht ohne Anforderung ersetzen.
**Kontrolle:** Richtige Zielgruppe/Policy und begründeter Required/Installed/NotRequired-
Status; fehlende Installation bei NotRequired kann korrekt sein.

Quellen: [Scanfehler](https://learn.microsoft.com/en-us/troubleshoot/mem/configmgr/update-management/troubleshoot-software-update-scan-failures),
[Deploymentprüfung](https://learn.microsoft.com/en-us/troubleshoot/mem/configmgr/update-management/troubleshoot-software-update-deployments),
[Scanquellen](https://learn.microsoft.com/en-us/windows/deployment/update/wufb-wsus),
[Clientfenster](https://learn.microsoft.com/en-us/intune/configmgr/develop/reference/core/clients/sdk/ccm_servicewindow-client-wmi-class),
[Serverfenster](https://learn.microsoft.com/en-us/intune/configmgr/develop/reference/core/servers/configure/sms_servicewindow-server-wmi-class).
