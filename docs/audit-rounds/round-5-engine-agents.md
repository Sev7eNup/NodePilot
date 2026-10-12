# Runde 5 – ergänzende Engine-Agent-Prüfung

Root hat den verbleibenden `NodePilot.Engine/Agents`-Bereich einschließlich
`AiAgentActivity` und `AiAgentTeamActivity` übernommen. Risikobasierte Prüfung
abgeschlossen, kein zusätzlich bestätigtes Critical/High/praktisches Medium.

Nachvollzogene Datenwege:

- Activity → Parse/Budget → Journal → Gate → Target/Tools → Runtime → Finalisierung.
  Die Journalinitialisierung verlangt eine vorhandene WorkflowExecution, bevor
  irgendein Tool geöffnet wird. Ein synthetischer StepTest-Kontext leiht keine
  fremde Identität. Vollständige Agent-Einzeltestunterstützung wäre eine eigene
  Erweiterung des Persistenzvertrags, kein hier eingeführter Nebenfix.
- ToolHost bindet Tools an Auswahl/Target, lädt MCP-Registrierung erneut und prüft
  aktualisierte Freigaben vor dem Aufruf. HTTP erlaubt nur lesende Methoden,
  keine Redirects, und nutzt vorhandene Netzwerkprüfung und Größenbegrenzung.
  Unterworkflows durchlaufen Auswahl, veröffentlichten Status, Read-only-Scope
  und vorhandene Runtime-Autorisierung; das Agent-Gate wird dabei kontrolliert
  freigegeben und erneut angefordert.
- PermissionPolicy: PowerShell-AST → zugelassene Literal-/Parameterformen →
  kontrollierter Befehlsaufbau. Keine dynamische Invocation oder Scriptblöcke;
  CIM-/Event-/IIS-Spezialpfade, CMD/Bash-Argumente und gepinnte Programme geprüft.
  Der Katalog vertrauenswürdiger Zielmodule ist keine neue Betriebssystem-Sandbox.
- Skill-ZIP: begrenzte Entpackgröße, normalisierte relative Namen, keine Links,
  Hashvergleich und erneute Paketfreigabe. Ressourcen sind seitenweise lesbar;
  Skriptausführung verlangt passende Shell, geprüfte Bytes/Argumente und die
  Ausführungsrichtlinie des Zielsystems. Keine automatische Schreibfreigabe.
- Target-/FileTools: explizite Credential- oder Serviceidentity-Wahl, lokale
  Shareauflösung, Pfadgrenze/Reparse-Prüfung, blockweise Transfers, Hashprüfung,
  begrenzte Suche. ArtifactStore besitzt runbezogene Dateien und Gesamtbudget;
  ProcessScript begrenzt Prozesslaufzeit/Ausgabe und räumt temporäre Skripte auf.
- AgentRunDatabase serialisiert den geteilten Step-DbContext; Journal und
  Checkpoint redigieren Daten vor Persistenz/Live-Ausgabe. AgentContentRedactor
  behandelt auch JSON-in-JSON bis zur begrenzten Tiefe.

Quellen: alle Dateien dieser Engine-Untergruppe risikobasiert betrachtet;
Allowlistkataloge und wiederholte Darstellungstexte sind nicht als separate
Funktionsnachweise gezählt. `AgentExternalReadPolicy` und
`AgentReadOnlyWorkflowScope` hat zusätzlich der Runtime-Reviewer vollständig
geprüft. Kein neuer Produktcode/Test in diesem Ergänzungsbereich, keine echten
Zielrechner-, MCP-, LLM- oder WinRM-Aufrufe.
