# Distribution Point, Boundary und Paketquelle

Erst Package/Content-ID und angeforderte Version aus aktuellem Clientrequest bestimmen.
Sitecode/Provider entdecken, dann gezielt SMS_Package, SMS_DistributionPoint und
SMS_PackageStatusDistPointsSummarizer lesen. State 0 der letzten Klasse bedeutet
Installed, ist aber eine Zusammenfassung und keine frische Prüfung der Bytes.
Ein DP für andere Pakete beweist keine Verteilung des betroffenen Pakets.

## 22 — Content nicht auf zuständigen DP verteilt / Verteilung entfernt

**Symptom:** WaitingContent, leere Locationantwort, etwa 0x87D00607.
**Prüfen:** Paket existiert? Passende Package-DP-Zuordnung und Verteilstatus mit
Version vorhanden? Clientlog nennt genau diesen Content und keine nutzbare Location?
Erfolgreiche leere Providerabfrage von Fehler/Zugriff verweigert unterscheiden.
Asynchronen Entfernungs-/Verteilstatus berücksichtigen; Zusammenfassung kann nachlaufen.
**Behebung:** Exakten Inhalt auf vorgesehenen geeigneten DP verteilen, erfolgreiche
passende Version abwarten. Nicht sämtliche Pakete neu verteilen.
**Kontrolle:** Client bekommt geeignete Location und vollendet Download/Integritätsprüfung.
Historisch entfernte Verteilung nicht allein aus später gesundem Status rekonstruieren.

## 23 — Boundary-/Boundary-Group-Zuordnung liefert keinen geeigneten DP

**Symptom:** Keine Contentlocation trotz vorhandener Distribution.
**Prüfen:** Aktuelle Client-IP/AD-Site gegen BoundaryType/Value prüfen; BoundaryID →
GroupID → SiteSystems verbinden, zugehörige Fallback-/Nachbarschaftsregeln berücksichtigen.
AD-Site-Boundary ist kein IP-Literal. Alte Inventar-IP und fehlender exakter IP-Eintrag
schließen andere passende Boundarytypen nicht aus. Namen allein sind keine Verknüpfung.
**Behebung:** Tatsächliche Lücke/Fehlzuordnung in Boundary/Gruppe/DP-Zuständigkeit
korrigieren; fremde Netze nicht pauschal zusätzlich aufnehmen.
**Kontrolle:** Client erhält im vorgesehenen Verhalten eine erreichbare passende
Location. Dies ist auch eine Differentialprüfung, wenn tatsächlich Fall 22 vorliegt.

## 24 — Paketquelle fehlt, zeigt falsch oder ist für die Verteilung nicht lesbar

**Symptom:** distmgr meldet Quellfehler (z. B. Fehler 2), Version kommt nicht auf DP an.
**Prüfen:** PkgSourcePath/SourceVersion, neuer Verteilversuch und Logs korrelieren.
UNC auf den tatsächlich zugeordneten Share/Server abbilden; Quelldateien und Elternpfad
lesen. Erfolgreich geprüfte Abwesenheit von Access denied/WinRM-Doppelhop unterscheiden.
Das Agentenkonto kann andere Rechte als Site-/Computerkonto besitzen. Share vorhanden
beweist weder Unterordner noch NTFS-Zugriff der Verteilidentität. Falls diese Sicht
fehlt, genau diese Lücke melden, nicht „Pfad fehlt“ behaupten.
**Behebung:** Falschen Quellpfad korrigieren, erwartete Originaldaten wiederherstellen
oder notwendige eng begrenzte Share-/NTFS-Rechte der tatsächlichen Identität herstellen.
Danach betroffenen Inhalt unterstützt aktualisieren/verteilen; Version darf steigen.
**Kontrolle:** Erfolgreicher neuer Verteilversuch, erwartete aktuelle Version auf DP,
korrespondierende Clientpolicy und erfolgreicher Download. Nicht alte Version fixieren.

Quelle: [Distribution status](https://learn.microsoft.com/en-us/intune/configmgr/develop/reference/core/servers/configure/sms_packagestatusdistpointssummarizer-server-wmi-class),
[Boundary membership](https://learn.microsoft.com/en-us/intune/configmgr/develop/reference/core/servers/configure/sms_boundarygroupmembers-server-wmi-class).
