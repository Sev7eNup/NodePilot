# Clientinstallation und Dienste

Für alle Dienstfälle: Win32_Service Name/State/StartMode/PathName lesen, aktuelle
Service-Control-Manager-Ereignisse und passende Produktlogs korrelieren. `Manual`
oder zeitweise `Stopped` allein ist bei bedarfsgestarteten Diensten kein Defekt.
Numerische Werte nur anhand ihres Typs deuten: Get-Service Status 4 = Running,
1 = Stopped; diese Werte gelten nicht für beliebige WMI- oder Produkt-Enums.
Konfigurierten Sollzustand feststellen; nicht alle Dienste pauschal auf Automatic setzen.

## 01 — BITS deaktiviert oder für den Transfer nicht verfügbar

**Symptom:** Download bleibt stehen; DTS/CTM meldet einen BITS-bezogenen Fehler.
**Prüfen:** BITS Disabled/Stopped zum relevanten Zeitpunkt, tatsächlicher Job-/Fehler-
bezug und nachgewiesener Downloadpfad. Fehlende Location liegt vor dem Transfer und
wird nicht durch BITS erklärt. BITS kann regulär im Leerlauf stoppen.
**Behebung:** Nach bestätigtem Dienstblock ursprünglichen erlaubten Startmodus
wiederherstellen und Dienst bei Bedarf starten; verursachende Policy korrigieren.
**Kontrolle:** Betroffener Transfer schreitet fort, Contentprüfung erfolgreich.

## 02 — Windows Update Agent durch wuauserv-Zustand blockiert

**Symptom:** Scan/Updateoperation kann nicht starten oder meldet Dienstfehler.
**Prüfen:** wuauserv-Zustand und Startmodus mit aktuellem WUAHandler/WindowsUpdate-
Versuch abgleichen. Automatische Wiederaktivierung möglich: ein früherer Disabled-
Snapshot erklärt keinen später erfolgreichen Scan.
**Behebung:** Belegte Sperre in ihrer Konfigurationsquelle beheben, vorgesehenen
Startmodus wiederherstellen. Kein SoftwareDistribution-Reset ohne separaten Beleg.
**Kontrolle:** Nach autorisiertem neuen Scan nativer erfolgreicher Scanabschluss.

## 03 — SMS Agent Host (CcmExec) deaktiviert/gestoppt

**Symptom:** Keine neuen Policies, Deployments oder Clientaktivität.
**Prüfen:** Dienstkonfiguration, CcmExec.log und Systemereignisse. Dienst existiert
mit gültigem Pfad? Abschaltung ist kein Beweis für deinstallierten Client.
**Behebung:** Vorgesehenen Dienstbetrieb wiederherstellen und sperrende Policy
korrigieren; fehlende Dateien nur bei nachgewiesener Abwesenheit separat behandeln.
**Kontrolle:** Dienst läuft, neue Policy wird verarbeitet und Originalfunktion reagiert.

## 04 — SMS_EXECUTIVE auf dem Site-Server deaktiviert/gestoppt

**Symptom:** Verteilung/Serververarbeitung stockt, mehrere Komponenten ohne Fortschritt.
**Prüfen:** Dienstzustand/Startmodus, smsexec.log und betroffene Komponentenlogs.
Laufender Dienst beweist nicht, dass alle enthaltenen Komponenten laufen (Fall 21).
**Behebung:** Genehmigten Dienstbetrieb und Konfigurationsquelle gezielt korrigieren;
Auswirkungen auf alle Site-Komponenten vor Neustart berücksichtigen.
**Kontrolle:** Betroffene Komponente arbeitet und konkrete Queue/Aufgabe schreitet fort.

## 05 — WsusService deaktiviert/gestoppt

**Symptom:** WSUS-Hintergrundverarbeitung/Synchronisierung stockt.
**Prüfen:** WsusService, WSUS-Logs, SUP- und Datenbankstatus. IIS kann trotz gestopptem
WsusService antworten; HTTP 200 oder Dienststillstand allein beweist keinen Scanfehler.
**Behebung:** Sollkonfiguration des betroffenen Dienstes wiederherstellen; weitere
IIS-/DB-Ursachen nur bei entsprechendem Befund behandeln.
**Kontrolle:** Ursprüngliche WSUS-Operation einschließlich Katalogergebnis erfolgreich.

## 06 — Unvollständige Clientinstallation versus falsche Gesundheitsdiagnose

**Symptom:** Fehlende Versionsausgabe, Dienststartfehler oder vermeintlich kaputtes WMI.
**Prüfen:** root/ccm SMS_Client-Schema/ClientVersion und tatsächliche Registrywerte
prüfen. Fehlendes projiziertes Feld ist kein Defekt. Required-Datei über gültigen
Installationsvertrag/Version und Dienstpfad bestimmen; erfolgreiche Verzeichnisprüfung
und fehlgeschlagene Extraktion in ccmsetup.log korrelieren. Access denied ist keine
Dateiabwesenheit. CcmEval kann reparieren: vorhandenes Log lesen, nicht starten.
**Behebung:** Nur nach echtem Installationsschaden unterstützte Clientreparatur oder
gezielte Neuinstallation mit gültiger Quelle und korrekter Zuweisung planen. Bei
falscher Abfrage nur Diagnoseabfrage korrigieren; WMI-Reset nicht als Standardrezept.
**Kontrolle:** Erforderliche Dateien/Dienst/Clientversion und betroffene Funktion
prüfen; einzelne erfolgreiche Checks nicht als vollständige Clientgesundheit verkaufen.

Quelle: [Client checks and remediation](https://learn.microsoft.com/en-us/intune/configmgr/core/clients/manage/client-health-checks).
