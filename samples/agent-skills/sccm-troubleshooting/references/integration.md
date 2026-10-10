# Verwendung des Skills

## NodePilot

Paketname `sccm-troubleshooting`, Version `1.0.0`. Das ZIP enthält SKILL.md im Root
und das Verzeichnis references. In Einstellungen → KI-Agenten → Skills importieren
und beim Einzelagenten oder gewünschten Teammitglied auswählen. Import schaltet
den Skill nicht automatisch in bestehenden Workflows frei und erteilt keine Werkzeuge.

Für Diagnose üblicherweise PowerShell sowie files_list/files_read/files_search
auswählen. Optionale Dateisammlung nur innerhalb Laufbudget und benötigter Pfade.
Client und Site-/SUP-/DP-Server können verschiedene Mitglieder mit je einer festen
Maschinen- und Credential-Zuordnung benötigen. Dienstidentität nur explizit auswählen.
Der Supervisor braucht keine eigenen Remote-Werkzeuge, wenn er ausschließlich delegiert.
Der Benutzer beschreibt das Problem natürlich, ohne Werkzeugnamen nennen zu müssen.

Beispielauftrag: „Untersuche, warum die angegebene Anwendung auf dem betroffenen
Client nach erfolgreicher Installation weiter als fehlgeschlagen erscheint.
Prüfe Client und zuständigen Site-Server lesend. Liefere belegte Ursache,
gezielte Behebung und Erfolgskontrolle; führe keine Änderungen aus.“

Der Host bietet load_skill und read_skill_resource an. Referenzpfade aus SKILL.md
verwenden, bei hasMore mit nextOffset fortsetzen (Byteoffset, nicht Zeichenzahl).
Es gibt bewusst keine ausführbaren Skill-Skripte: vorhandene Lese-Werkzeuge genügen.
Damit benötigt das Paket selbst weder Zieltransfer noch eine geänderte PowerShell-
Ausführungsrichtlinie. Seine Referenzen enthalten keine Credentials/Laboradressen.

## Andere Agent-Skills-Hosts

Das komplette Verzeichnis `sccm-troubleshooting` in das vom Host vorgesehene
Skillverzeichnis kopieren. SKILL.md besitzt standardkonformes YAML-Frontmatter;
Referenzen sind relative Markdown-Dateien. Keine NodePilot-API ist zur Interpretation
erforderlich. Die dort verfügbaren autorisierten Lesewerkzeuge oder bereitgestellte
Logdateien verwenden. Hostrechte bleiben maßgeblich.

## Reichweite

Das Handbuch deckt bekannte Diagnosewege ab, garantiert aber keinen Modell- oder
Diagnoseerfolg. Fehlende Daten, Rechte oder Produktversionen transparent machen.
Die Beispiele sind keine fest programmierten Antworten. Andere Ursachen und
abweichende Topologien bleiben möglich. Eine Reparaturanleitung autorisiert ihre
Ausführung weder in NodePilot noch in einem anderen Host.

Formatquelle: [Agent Skills specification](https://agentskills.io/specification).
