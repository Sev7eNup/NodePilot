# Netzwerk, Auflösung und Management Point

Vom tatsächlich scheiternden Request ausgehen: FQDN, Protokoll, Port, Pfad,
Clientidentität und Zeitpunkt. MP-, SUP- und DP-Endpunkte können verschieden sein.
Ein Browserabruf als Benutzer bildet den Maschinenkonto-/SYSTEM-Pfad nicht sicher ab.

## 07 — DNS-/hosts-Fehlleitung zum falschen Ziel

**Symptom:** Timeout/Verbindungsfehler trotz scheinbar korrekter DNS-Antwort.
**Prüfen:** DNS-Ergebnis, lokale hosts-Datei, Resolvercache und tatsächlich verwendete
Zieladresse gegenüberstellen. Resolve-DnsName allein schließt hosts-Overrides nicht
aus. Namensvarianten/FQDN/Alias des fehlgeschlagenen Requests vergleichen. Gültige
Route belegt keine gültige Namensauflösung.
**Behebung:** Nur falschen zuständigen DNS-/hosts-Eintrag korrigieren, vorher sichern;
anschließend kontrollierte Cacheaktualisierung, falls erforderlich.
**Kontrolle:** Ursprünglicher Name löst im Anwendungskontext zum vorgesehenen Ziel auf
und dieselbe Operation funktioniert. Kein pauschales Leeren aller DNS-Zonen.

## 08 — Falscher oder unerreichbarer WinHTTP-Proxy

**Symptom:** WUA/MP-Kommunikation scheitert, interaktiver Browser funktioniert.
**Prüfen:** Effektive WinHTTP-Konfiguration einschließlich Bypass sowie tatsächlichen
Requestpfad lesen (`netsh winhttp show proxy` bei erlaubtem CMD). Proxyname/Port und
Erreichbarkeit anhand vorhandener Daten abgleichen; WPAD/PAC und Authkontext beachten.
WinINET-Konfiguration ist nicht automatisch die WinHTTP-Konfiguration.
**Behebung:** Gewollten Proxy oder gezielten Intranet-Bypass in der zuständigen
Verwaltung korrigieren. Nicht reflexartig „direct“ setzen oder Benutzerproxy importieren.
**Kontrolle:** Gleiche Operation mit ihrer Ausführungsidentität erreicht das Ziel.

## 09 — Aktive Firewallregel oder falscher Netzwerkpfad

**Symptom:** Scan, Policy oder Download scheitert trotz erreichbarem/gesundem Server.
**Prüfen:** Aktives Netzwerkprofil, Regelrichtung/Aktion/Enabled, PolicyStoreSourceType,
Port-, Adress-, Dienst- und Programmfilter der verdächtigen Regel lesen. Tatsächliche
Zieladresse/Port gegen Filter abgleichen. Find-NetRoute liest die beste Route;
fehlende exakte /32-Route schließt passende Netz-/Default-Route nicht aus. Keine
passende lokale Regel schließt externe Firewall/Proxy nicht aus.
**Behebung:** Exakt belegte Blockregel in ihrer Policyquelle entfernen oder passend
eingrenzen. Eine Allow-Regel hebt eine passende explizite Blockregel nicht einfach auf.
Bei falscher Route nur bestätigten Pfad korrigieren; keine globale Firewallabschaltung.
**Kontrolle:** Passende aktive Filter plus erfolgreicher ursprünglicher Request;
fehlende Drop-Events allein sind kein Erfolgsbeweis.

## 10 — MP-Webseite/IIS-Anwendung gestoppt

**Symptom:** Keine aktuellen Policies, MP-Verbindung etwa mit 12029/HTTP-Fehlern.
**Prüfen:** Tatsächliche MP-Site, Bindings, Anwendungspool und Websitezustand lesen.
W3SVC/WAS Running belegt keinen gestarteten Webauftritt. Passende IIS-Site-ID/Logs
mit PolicyAgent, LocationServices und CcmMessaging korrelieren. Ein erfolgreicher
SUP-Request auf anderem Port widerlegt keine MP-Störung.
**Behebung:** Betroffene Site/Pool entsprechend Sollkonfiguration starten und den
belegten Stopgrund beheben. Kein IIS-Reset über alle Sites als erste Maßnahme.
**Kontrolle:** Passender MP-Policyrequest/-response erfolgreich; „keine neuen
Zuweisungen“ kann eine gültige Antwort sein und bedeutet nicht verlorene Policies.

Quellen: [WinHTTP](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/netsh-winhttp),
[Firewallregel-Vorrang](https://learn.microsoft.com/en-us/windows/security/operating-system-security/network-security/windows-firewall/rules).
