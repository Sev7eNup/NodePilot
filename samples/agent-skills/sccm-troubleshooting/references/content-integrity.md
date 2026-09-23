# Clientcache, Content Library und tatsächliche Bytes

## Gemeinsamer Vergleich vor der Ursachenzuordnung

Quelle, DP und Cache sind drei verschiedene Repräsentationen. Verknüpfe PackageID,
ContentID, angeforderte/deployte Version und relativen Pfad. Aktuelle Quelle kann
seit der Verteilung verändert sein. Clientcachepfad aus CAS/CTM bzw. Cachemetadaten
ermitteln; keinen geratenen Ordner oder nur die größte Datei prüfen.

Auf dem DP: PkgLib beschreibt Paket-/Contentzuordnung, DataLib relative Dateien mit
Größe/Hash, FileLib die tatsächlichen Bytes. Aus Dateideskriptor Hash H folgt üblicher
Speicherpfad FileLib/<erste vier Zeichen von H>/<H>. Tatsächliche Librarylaufwerke
ermitteln (kann mehrere umfassen). INI und SIG sind nicht der Payload. Pfade nicht aus
Contentnamen erraten. Kein Schreiben in die Library; deduplizierte Blobs können geteilt sein.

Erstelle vollständige begrenzte Inventare für diese Content-Version und eine Tabelle:
relativer Pfad | erwartete Größe/Hash | DP vorhanden/Größe/Hash | Cache vorhanden/
Größe/Hash | Quelle/Version | Evidenz. Erst Dateimengen, dann gemeinsame Dateien
vergleichen. Auch Manifest, Hilfsskript und kleine Begleitdateien gehören zum Inhalt.
Datei-SHA256 nur mit derselben Datei und demselben Algorithmus vergleichen, nie mit
einem aggregierten Pakethash. Bei großen Inventaren seitenweise arbeiten.

## 25 — Erforderliche Datei fehlt im Clientcache

**Symptom:** Native Contentprüfung scheitert, z. B. 0x80091007 vor Programmausführung.
**Prüfen:** Vollständige erwartete Dateimenge gegen tatsächlich zugeordneten Cache;
Abwesenheit am exakten Pfad mit erfolgreicher Elternverzeichnisprüfung bestätigen.
DP-Version vollständig und gültig? Eine einzelne passende Nutzdatei reicht nicht.
**Behebung:** Nur den betroffenen Cacheeintrag über unterstützte Clientverwaltung
nach Sicherung/Prüfung aktiver Nutzung erneuern und gültigen Inhalt neu beziehen lassen.
**Kontrolle:** Dateimenge und Dateiwerte vollständig, native Prüfung und ursprünglicher
Schritt erfolgreich. Fehlende Datei nicht künstlich mit geratenem Inhalt ergänzen.

## 26 — Cachedatei oder Manifest verändert

**Symptom:** Hashfehler trotz vorhandenem Payload und vollständiger Dateianzahl.
**Prüfen:** Alle Dateien derselben Version vergleichen, auch Manifesttext. Ein Manifest
enthält erwartete Werte, besitzt aber selbst ebenfalls Bytes und Hash. Passender
Payload beweist kein unverändertes Manifest. Native Prüfung kann vor Skriptprüfung scheitern.
**Behebung:** Bei gesundem DP betroffenen Cacheeintrag unterstützt erneuern; Ursache
wiederholter Änderungen getrennt untersuchen. Kein pauschales DP-Neuverteilungserfordernis.
**Kontrolle:** Betroffene Datei und übriges Inventar entsprechen gültiger Version;
native Prüfung und Ausführung erfolgreich. Historischen Änderungsautor nicht erfinden.

## 27 — Physische DP-Datei fehlt trotz Metadaten/Installed-Status

**Symptom:** HTTP 404/0x80190194, Contentdownload oder abhängiger TS-Schritt scheitert.
**Prüfen:** Exakten Request zum DP-Sitelog, Packageversion, DataLib-Deskriptor und
FileLib-Objekt verfolgen. Alle konfigurierten Librarylaufwerke berücksichtigen.
Ein fehlender geratener Pfad oder fremder ContentID beweist diesen Fall nicht.
**Behebung:** Betroffenen Inhalt über unterstützte ConfigMgr-Verteilung aus gültiger
Quelle wiederherstellen. Keine Blobs/INI manuell anlegen und keine fremden Pakete löschen.
**Kontrolle:** Zugeordnetes physisches Objekt vorhanden und gültig; frischer Bezug
derselben logischen Contentversion (oder legitim aktualisierter Version) erfolgreich.

## 28 — Physische DP-Datei beschädigt

**Symptom:** Transfer kann HTTP 200 liefern, native Prüfung scheitert mit Hashfehler.
**Prüfen:** Tatsächlichen FileLib-Payload mit erwartetem Deskriptorwert vergleichen.
Größe kann trotz Byteänderung gleich bleiben. Gesunde Quelle/Installed-Summary widerlegt
DP-Korruption nicht. Aktuellen Defekt getrennt von historischer Auslieferung belegen.
**Behebung:** Exakten betroffenen Inhalt unterstützt aktualisieren/neu verteilen;
bei Wiederholung Storage/AV/Transport mit konkreten Belegen untersuchen.
**Kontrolle:** Richtige Bytes auf DP, frischer Clientdownload und native Integrität.
Aktualisierung kann Version ändern; korrekte neue Version statt alter Nummer verlangen.

## 29 — Download-/Hashfehler ohne lokalisierten Defekt

**Symptom:** 0x80091007, 0x80070002, 0x87D00607 oder Transportfehler.
**Prüfen:** Fehlende Location, HTTP/Dateizugriff, unvollständiger Transfer, native
Hashprüfung und eigener Manifestcheck getrennt lokalisieren. CAS/CTM/DTS/smsts zum
gleichen Versuch verbinden. Größe und Dateimengen vergleichen, nicht nur HRESULTs.
Ein veralteter Content-Request-Handle (beispielsweise 0x87D01200) gehört zu seinem
Job; nicht mit einem danach erzeugten Request vermischen. Fehlgeschlagener HTTP-
Versuch mit anschließend erfolgreichem HTTPS-Fallback ist kein belegter Restdefekt.
**Behebung:** Erst nach Zuordnung Cache, DP, Quelle oder Transport gezielt behandeln.
Ohne Zuordnung Diagnose als teilweise offen schließen; kein globales Cacheleeren.
**Kontrolle:** Ursprüngliche Phase plus nachgelagerte Prüfung erfolgreich, nicht
lediglich Downloadstatus 200 oder irgendein späterer Tasksequenzlauf.

## 30 — Delta-/UUP-Pfad gestört, normaler Download funktioniert

**Symptom:** Nur passende Delta/UUP-Updates scheitern.
**Prüfen:** Zuerst wirklich anwendbares Update und nativen Delta/UUP-Versuch belegen.
Effektive Clienteinstellungen/Port und aktuellen Downloadpfad aus DeltaDownload.log,
CAS/CTM/DTS sowie vorhandenen DO/WU-Ereignissen ermitteln. Nicht vom Standardport
auf tatsächlichen Listener schließen; ein gewöhnlicher Paketdownload testet diesen
Pfad nicht. Metadaten-, Range-/Proxy-, lokalen Listener- und Contentfehler unterscheiden.
**Behebung:** Belegte Delta-spezifische Konfiguration/Transport-/Contentursache beheben.
Keine globale Sicherheitsabsenkung oder deaktivierte Delta-Option als Universalrezept.
**Kontrolle:** Gleiches anwendbares Update über tatsächlich vorgesehenen Pfad erfolgreich.
Ohne passenden Updatebestand ist dieser Test nicht ausführbar, nicht bestanden.

Quellen: [Content Library](https://learn.microsoft.com/en-us/intune/configmgr/core/plan-design/hierarchy/the-content-library),
[Delta-Clienteinstellungen](https://learn.microsoft.com/en-us/intune/configmgr/core/clients/deploy/about-client-settings).
