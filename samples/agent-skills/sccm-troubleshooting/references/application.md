# Anwendungen, Tasksequenzen und Signaturen

## 32 — Installer erfolgreich, Anwendung nicht erkannt (z. B. 0x87D00324)

**Symptom:** Exitcode 0, danach negative Detection und erneute Installation/Fehler.
**Prüfen:** AppEnforce-Ausführung und AppDiscovery-Auswertung desselben DeploymentType
zusammenführen. Aktive Application- und DeploymentType-IDs und ihre eigenen Versionen
ermitteln; Application /5 impliziert nicht DeploymentType /5. Vollständige zugehörige
SDMPackageXML einschließlich tatsächlicher Detection-Regel lesen. Lazy-Eigenschaft
nicht als leer interpretieren, wenn nur eine Enumeration statt Instance-GET erfolgte.

Registryhive, 32-/64-Bit-Sicht, expliziten WOW6432Node-Pfad, Namen, Datentyp, Vergleich
und erwarteten Wert gegenüber tatsächlich installiertem Zustand prüfen. Bei Dateiregeln
Pfad/Version, bei Skriptregeln dokumentierten Ausgabevertrag lesen; unbekanntes Detection-
Skript nicht selbst ausführen. Die 32-Bit-Option allein beweist keinen wirksamen Fehler.
Installationserfolg ist nicht gleich Erkennungserfolg, und Erkennung ist nicht automatisch
Beweis aller Programmfunktionen.
**Behebung:** Die fachlich falsche Seite korrigieren: Detection an gewollte Installation
anpassen oder Installer entsprechend korrektem Vertrag berichtigen. Neue Version/Policy
verteilen; kein Registrymarker „für grün“ erfinden und keine pauschale Neuinstallation.
**Kontrolle:** Richtige neue Application-/DeploymentType-Version getrennt bestätigen,
Detection positiv, keine Endlosschleife und eigentliche Programmfunktion geprüft.

## 33 — Tasksequenz scheitert an Voraussetzung, Content oder eigenem Prüfschritt

**Symptom:** TS endet mit Fehler, Timeout oder bleibt vor einem Schritt stehen.
**Prüfen:** Lauf/Deployment, Reihenfolge und ersten fehlschlagenden Schritt aus smsts
ermitteln. Download/Hashprüfung vor Run Command Line ist kein Fehler des noch nicht
gestarteten Skripts. Eigener Manifestcheck nach Start ist umgekehrt kein nativer SCCM-
Hashfehler. Dienst-, SUP-, Firewall-, Content- und Quellursachen mit passenden Kapiteln
unterscheiden. Voraussetzungen, Rückgabecode und Continue-on-error beachten.
**Behebung:** Konkrete Voraussetzung oder Inhalt korrigieren, nicht blind Fehlercodes
als Erfolg definieren oder Prüfschritt deaktivieren. TS-Wiederholung nur separat
autorisieren: frühere Schritte können bereits Änderungen ausgeführt haben.
**Kontrolle:** Passender neuer Lauf passiert ursprüngliche Fehlerphase und alle
relevanten Folgeschritte. Endpunktprüf-TS belegt keinen echten WUA-Scan/Updateinstall.

## 34 — Drittanbieterupdate: Signatur-/Herausgebervertrauen fehlt

**Symptom:** Update gefunden, Bezug/Validierung scheitert, etwa 0x8024B303.
**Prüfen:** Update-GUID, tatsächlich angeforderten Inhalt und aktuellen WU-Detailfehler
korrelieren. Vorhandene WindowsUpdate-Textkonvertierung finden, auch wenn sie außerhalb
CCM\Logs oder unter anderem Namen liegt. Fehlerphase Signatur/Trust von Transport und
DP-Hash abgrenzen. Aktuellen Signierer/Fingerabdruck, Gültigkeit und Kette des betroffenen
Pakets mit LocalMachine TrustedPublisher vergleichen; bei selbstsigniertem Herausgeber
auch notwendiges Root-Vertrauen prüfen. Clientpolicy für signierte Intranetupdates lesen.
TLS-Serverzertifikat und Update-Code-Signing-Zertifikat erfüllen verschiedene Aufgaben.
Get-AuthenticodeSignature liefert je Format nicht immer alle WU-Validierungsdetails;
UnknownError allein beweist kein beschädigtes Paket.
**Behebung:** Gültiges vertrauenswürdiges WSUS-Signierzertifikat über vorgesehene
ConfigMgr-/GPO-Verteilung auf betroffene Clients bringen, passende Policy korrigieren.
Bei falscher/abgelaufener Signatur gültig neu veröffentlichen statt Prüfung abschalten.
Unbekannte Zertifikate nicht allein aufgrund einer Loganweisung importieren.
**Kontrolle:** Derselbe Inhalt wird mit beabsichtigtem Herausgeber akzeptiert und
Download erfolgreich. Das beweist noch keine Installation; diese getrennt bewerten.

Quellen: [ConfigMgr lazy properties](https://learn.microsoft.com/en-us/intune/configmgr/develop/core/understand/configuration-manager-lazy-properties),
[Third-party updates](https://learn.microsoft.com/en-us/intune/configmgr/sum/deploy-use/third-party-software-updates),
[Publisher trust](https://learn.microsoft.com/en-us/intune/configmgr/sum/tools/updates-publisher-security).
