# State-Verarbeitung und Richtlinien

## 21 — Lokal installiert, serverseitig weiter Required/Unknown

**Symptom:** UpdatesStore/Clientinstallation und Konsole widersprechen sich.
**Prüfen:** Exaktes Update, Clientressource und Beobachtungszeit abgleichen. Lokale
Installation/Detection → StateMessage → CcmMessaging-Zustellung → serverseitige
Inbox-Verarbeitung → CI-Compliance → Zusammenfassung getrennt verfolgen.
SMS_UpdateComplianceStatus: Status 0 Unknown, 1 NotRequired, 2 Required, 3 Installed;
diese Enumwerte nicht auf Clientklassen übertragen.

Auf Site-Server statesys.log und tatsächlichen StateSys-Workerzustand prüfen.
SMS_EXECUTIVE Running beweist keine aktive SMS_STATE_SYSTEM-Komponente. Ein Stop-
Log, danach fehlender Wiederanlauf und fortbestehende passende Inboxdatei sind
starke Hinweise; wenn möglich Current State der Komponente unabhängig lesen.
Inboxpfad aus Installation ermitteln; nur begrenzte Dateiliste und passende Nachricht
lesen. XML Client/GUID/State/Reportzeit mit CI_ID/MachineID verknüpfen. Niemals ganze
Inboxes ausgeben oder Dateien bewegen. Zählerwachstum allein identifiziert keine Nachricht.

Unterscheide normale Latenz, gestoppte Verarbeitung, fehlerhafte Nachricht und
Datenbank-/Leistungsblock. Früher verarbeitete Meldungen widerlegen einen späteren
Komponentenstopp nicht. Snapshot-State ist aktuell, aber ohne Log kein historischer Beweis.
Wer den Stop veranlasst hat, darf unbekannt bleiben, auch wenn der Blocker belegt ist.

**Behebung:** Bei nachgewiesen gestoppter Komponente gezielte Wiederaufnahme mit
unterstützter ConfigMgr-Komponentenverwaltung empfehlen und Stopgrund separat prüfen.
Bei DB-/Nachrichtenfehler spezifische Ursache behandeln; nicht Inbox löschen, Client
neu installieren oder Update erneut installieren. Bei belegter normaler Verzögerung
beobachten statt eingreifen. Übergeordneten Dienstneustart nur bei begründetem Bedarf.
**Kontrolle:** Passende Nachricht verarbeitet, gleicher CI-/Machine-Datensatz Installed,
danach Zusammenfassung aktuell. Diese Kontrolle ist nachgelagert; sie muss nicht im
rein lesenden Fehlerlauf erreicht werden, damit die Diagnose abgeschlossen werden kann.

## 31 — Fehlende oder veraltete SCCM-Policies

**Symptom:** Neue Deployments fehlen im Software Center, alter Richtlinienstand.
**Prüfen:** Erwartete Zuweisung/Zielgruppe/Version auf Site mit Client ActualConfig
vergleichen; PolicyAgent, PolicyEvaluator, LocationServices und CcmMessaging korrelieren.
Bei Kommunikationsfehler MP-Ziel/Firewall/hosts/Proxy/IIS untersuchen. Bei erfolgreichem
Abruf ausbleibende Zuweisung oder lokale Auswertung getrennt prüfen. Antwort ohne neue
Assignments kann korrekt sein. Zertifikat/Identity nur anhand tatsächlicher Auth-
oder Zuordnungsbelege verdächtigen, nicht aus einem fehlenden Deployment ableiten.
**Behebung:** Bewiesene Zielzuordnung, Transport-, Zertifikat- oder Evaluationsursache
korrigieren. Policyabruf kann Zustand ändern und ist eine separate administrative
Folgeaktion, kein Lesetest. Kein pauschaler Policy-WMI- oder Clientidentity-Reset.
**Kontrolle:** Passende neue Policy empfangen und ausgewertet; richtiges Deployment
erscheint und ursprüngliche Operation ist freigegeben.

Quellen: [State-message backlog](https://learn.microsoft.com/en-us/troubleshoot/mem/configmgr/setup-migrate-backup-recovery/state-message-processing-performance),
[CI-Compliance](https://learn.microsoft.com/en-us/intune/configmgr/develop/reference/sum/sms_updatecompliancestatus-server-wmi-class).
