# SUP, WSUS und effektive IIS-Konfiguration

## 11 — SUP-/WSUS-URL, Port oder Binding passen nicht zusammen

**Symptom:** Scan/TS-Endpunktprüfung läuft in Timeout oder connection refused.
**Prüfen:** Client WUServer/WUStatusServer und tatsächlichen Request, SUP-Port/SSL-
Konfiguration sowie IIS-Binding und Listener vergleichen. 8530/8531 sind häufig,
aber keine universellen Sollwerte. Alter HTTP-Erfolg vor Bindingänderung widerlegt
fehlenden aktuellen Listener nicht. TLS-Servername/Zertifikat ebenfalls abgleichen.
**Behebung:** Anhand gewollter Topologie die falsche Seite korrigieren: Clientpolicy,
SUP-Konfiguration oder Binding. Nicht allein aufgrund Standardports entscheiden.
**Kontrolle:** Dieselbe Clientoperation erreicht richtigen Endpunkt und schließt ab.

## 12 — WsusPool gestoppt

**Symptom:** Aktuelle WSUS-Anfrage erhält HTTP 503.
**Prüfen:** Effektiven Pool für WSUS bestimmen, State und WAS-Ereignisse lesen;
Zeitbezug zur IIS-Anfrage herstellen. Laufende W3SVC/WsusService-Dienste widerlegen
einen gestoppten Pool nicht. 503 allein unterscheidet Stop, Überlast und Recycling nicht.
**Behebung:** Bestätigten Stopgrund beseitigen und betroffenen Pool wieder starten;
bei unmittelbarer Wiederholung Ursache wie Fall 13 untersuchen.
**Kontrolle:** Pool bleibt aktiv, ursprüngliche Anfrage/Scan gelingt.

## 13 — WsusPool recycelt wegen unzureichendem Speicherlimit

**Symptom:** Wiederkehrende 503, Cache-Neuaufbau, Recycling/Stop unter Last.
**Prüfen:** Effektive privateMemory/virtualMemory, Recycling-/Rapid-Fail-Einstellungen,
WAS-Ereignis mit Begründung und Serverressourcen vergleichen. privateMemory ist in KB;
0 bedeutet kein solches Limit. Ein kleines Limit plus zugehöriges Recyclingereignis
ist wesentlich stärker als „Pool war irgendwann gestoppt“. SQL/WID mit berücksichtigen.
**Behebung:** Ungeeignetes Limit gezielt korrigieren. Microsoft empfiehlt für WSUS in
seinen Best Practices deaktivierte private/virtuelle Limits (0); andere Anleitungen
nennen erhöhte endliche Limits. Entscheidung mit verfügbarem RAM und Betriebsstandard
begründen, nicht blind einen Laborwert übernehmen. Danach Pool kontrolliert betreiben.
**Kontrolle:** Keine entsprechende Recyclingserie, stabiler Speicher und Scanabschluss
unter vergleichbarer Last. Ein einzelner HTTP-200-Abruf genügt nicht für Laststabilität.

## 14 — WSUS-HTTPS fordert fälschlich Clientzertifikate / TLS-Abweichung

**Symptom:** HTTPS-Scan scheitert, etwa Zertifikatanforderung oder HTTP 403.
**Prüfen:** Serverzertifikat/SAN/Gültigkeit/Vertrauenskette und Binding getrennt von
IIS-Clientzertifikat-Einstellungen lesen. Effektive (auch geerbte) sslFlags der
betroffenen WSUS-Anwendung: SslRequireCert versus nur Require SSL. IIS-Status,
Substatus und Windows-Fehler sind Hinweise; passende Anfrage erforderlich.
**Behebung:** Bei nachgewiesen versehentlicher Clientzertifikatanforderung den
betroffenen WSUS-Endpunkt gemäß SUP-TLS-Dokumentation auf „Client certificates:
Ignore“ korrigieren, erforderliches TLS erhalten. Bei Serverzertifikatfehler stattdessen
Zertifikat/Binding/Kette korrigieren. Keine globale TLS-/Zertifikatsprüfung abschalten.
**Kontrolle:** Derselbe HTTPS-Scan gelingt mit vorgesehener Clientidentität und
validiertem Serverzertifikat; keine unnötige Änderung anderer IIS-Anwendungen.

Quellen: [WSUS Best Practices](https://learn.microsoft.com/en-us/troubleshoot/mem/configmgr/update-management/windows-server-update-services-best-practices),
[WSUS-Verbindungsfehler](https://learn.microsoft.com/en-us/troubleshoot/mem/configmgr/update-management/troubleshoot-wsus-connection-failures),
[SUP-TLS-Konfiguration](https://learn.microsoft.com/en-us/intune/configmgr/sum/get-started/software-update-point-ssl).
