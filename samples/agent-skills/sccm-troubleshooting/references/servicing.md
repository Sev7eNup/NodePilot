# Windows-Servicing und vorgelagerte Infrastruktur

## 35 — Fehlende/ungeeignete FoD-, Feature- oder Reparaturquelle

**Symptom:** DISM/CBS kann erforderlichen Payload nicht finden; z. B. 0x800f081f
oder konkreter FoD-Quellfehler. Capability bleibt NotPresent.
**Prüfen:** Letzte tatsächliche Operation und Optionen aus DISM-/CBS-Logs lesen.
Build, Architektur, Sprache, Feature/Capability und gewünschte Version gegen
Quellmedium und benötigte CABs vergleichen. Quelle wirklich vorhanden/lesbar?
LimitAccess kann Onlinefallback ausschließen. Capability-/Featurestatus unabhängig
lesen. Fehlender Payload beweist keine allgemeine Beschädigung des Component Store.
**Behebung:** Passende vertrauenswürdige vollständige Quelle bereitstellen oder
zulässige Bezugsrichtlinie korrigieren; anschließend gezielte Operation autorisieren.
Nicht beliebige ISOs nutzen, WinSxS löschen oder pauschal RestoreHealth ausführen.
**Kontrolle:** Ursprüngliche Servicingoperation erfolgreich und gewünschter Zustand
erreicht; CBS/DISM des neuen Versuchs, nicht nur Rückgabecode eines Wrappers.

## 36 — Feature-Upgrade durch Anwendung/Treiber/Kompatibilität blockiert

**Symptom:** Featureupdate stoppt in Kompatibilitätsphase, etwa 0xC1900208.
**Prüfen:** Zielversion und aktuellen Setupversuch aus setupact/setuperr,
CompatData*.xml und *_APPRAISER_HumanReadable.xml verknüpfen. Pantherpfade nach Phase
ermitteln. Konkretes blockierendes Objekt und Blocking-Einstufung lesen; alte XMLs
können überholt sein. Vorhandene SetupDiag-Ausgabe ergänzend, nicht statt Originalbeleg.
**Behebung:** Exakte inkompatible Anwendung/Treiber unterstützt aktualisieren oder
nach Freigabe entfernen; echte Reste nur nach eindeutiger Zuordnung behandeln.
Nicht Kompatibilitätsprüfung deaktivieren oder Dateien nach Namensähnlichkeit löschen.
**Kontrolle:** Neuer autorisierter Kompatibilitätslauf gegen dieselbe Zielversion
ohne diesen Blocker; dies beweist noch keinen erfolgreichen vollständigen Upgrade.
Ohne geeignetes Medium und reproduzierten Blocker keine bestandene Liveprüfung behaupten.

## 37 — WSUS-/SUP-Synchronisierung liefert keinen brauchbaren Updatekatalog

**Symptom:** Keine geeigneten Updates/Deployments trotz vermeintlichem Sync-Erfolg.
**Prüfen:** Produkte, Klassifizierungen, Upstream und aktuelle Synchronisierung über
wsyncmgr/WCM/WSUSCtrl und WSUS-Ergebnis korrelieren. Kategorieimport ist nicht vollständiger
Metadatenimport. Ein äußerer „Sync succeeded“-Eintrag kann eine andere Phase betreffen;
Canceled/UserCanceled oder Importfehler getrennt bewerten. Gewünschtes nicht abgelaufenes,
anwendbares Update im tatsächlich importierten Katalog prüfen.
**Behebung:** Nachgewiesene Auswahl-, DNS-/Proxy-/Upstream-, Berechtigungs- oder DB-
Ursache korrigieren und betroffene Synchronisierung separat autorisieren. Abbruchautor
ohne Audit nicht erfinden. Nicht künstlich „Required“ erwarten, wenn kein Update passt.
**Kontrolle:** Vollständiger relevanter Import plus verfügbarer gewünschter CI, danach
passender Clientscan. Fehlende Testbasis bleibt fehlende Testbasis.

## 38 — SQL/WID-Speicherkonkurrenz blockiert WSUS-Metadatenimport

**Symptom:** Sehr langsamer/hängender Import, belegter RESOURCE_SEMAPHORE-Wait.
**Prüfen:** Zeitgleichen Wait und Speicherdruck, mehrere Datenbankinstanzen und deren
effektive Speichergrenzen mit verfügbarem RAM korrelieren. Ein alter SQL-Timeout
erklärt keinen aktuellen Portfehler. Nur erlaubte Monitoringdaten verwenden; Host-
fehlende SQL-Rechte nicht umgehen oder über dynamische Shell ersetzen.
**Behebung:** Kapazität und begründete Instanzlimits mit DBA abstimmen, Reserve für
OS/IIS/WSUS lassen; vorhandene Limits sichern. Keine Labor-RAM-Werte übertragen,
keine pauschale DB-Verkleinerung oder Neustarts als Ursachenbeweis.
**Kontrolle:** Wait/Druck normalisiert und vollständiger Import erfolgreich.

## 39 — Gateway/DNS-Ausfall oder automatisches Herunterfahren der Infrastruktur

**Symptom:** Wiederkehrender Verlust von Internet, DNS, WinRM oder Updatequellen.
**Prüfen:** Gateway-/Routingdienst, tatsächliche Namensauflösung und Routen sowie
Systemereignisse zum Shutdown/Neustart prüfen. Ereignis 1074 kann Prozess/Grund nennen;
bei Evaluation-Lizenzablauf den protokollierten Lizenzkontext prüfen. Heruntergefahrene
VM oder ausgefallenes Gateway ist kein Beweis für einen SCCM-Clientdefekt.
**Behebung:** Gewollten Routingbetrieb oder Infrastrukturfehler korrigieren; für
abgelaufene Evaluation unterstützte gültige Lizenz bzw. neu bereitgestellte Testbasis.
Keine Lizenzüberwachung deaktivieren oder Lizenzablauf technisch umgehen.
**Kontrolle:** Stabile Infrastruktur über erwartetes Intervall und erfolgreicher
ursprünglicher Sync-/Download-/Kommunikationsablauf, nicht bloß kurzfristiger VM-Start.

Quellen: [Repair source](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/configure-a-windows-repair-source?view=windows-11),
[Upgrade compatibility](https://learn.microsoft.com/en-us/troubleshoot/windows-client/setup-upgrade-and-drivers/use-windows-setup-compatibility-scan-logs-to-identify-blocking-issues),
[SQL Server memory](https://learn.microsoft.com/en-us/sql/database-engine/configure-windows/server-memory-server-configuration-options).
