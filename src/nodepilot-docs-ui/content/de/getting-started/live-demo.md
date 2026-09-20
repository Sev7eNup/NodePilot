# Live-Demo

Die [Live-Demo](https://sev7enup.github.io/NodePilot/demo/) ist die echte NodePilot-Oberfläche mit
Beispieldaten, vollständig im Browser. Nichts wird installiert, kein Konto angelegt, und keine Daten
verlassen Ihren Rechner.

## Was möglich ist

Alles, was das Frontend allein leistet, verhält sich wie in einer echten Installation:

- **Fünf Beispiel-Workflows** in drei Ordnern durchsehen: eine tägliche Plattenplatz-Prüfung, einen
  Windows-Update-Health-Check, einen Spooler-Watchdog, der den Dienst neu startet und eskaliert
  wenn er unten bleibt, eine SCCM-Paket- und AD-Gruppen-Bereitstellung und eine Temp-Bereinigung
  mit Trigger-Parametern.
- **Den Designer öffnen** und die komplette Autoren-Oberfläche nutzen: Canvas-Linter,
  Variablen-Picker, Auto-Layout, Versions-Diff, Simulation und Pre-Publish-Check. Nichts davon
  braucht einen Server — auch in einer echten Installation läuft es im Browser.
- **Bearbeiten und veröffentlichen.** Workflow auschecken, ändern, veröffentlichen. Der Edit-Lock
  verhält sich wie im Produkt: Auschecken deaktiviert den Workflow, veröffentlicht wird also vor
  dem Ausführen.
- **Einen Workflow ausführen** und zusehen, wie der Canvas Schritt für Schritt aufleuchtet und sich
  die Live-Konsole füllt.
- **Anlegen, ändern und löschen** — Maschinen, Credentials, globale Variablen samt Ordnern, Custom
  Activities, Benutzer, Wartungsfenster und Ordnerrechte. Jeder dieser Schreibvorgänge passiert
  wirklich, nur eben in Ihrem Tab.
- **Den Rest der Anwendung ansehen** — Ausführungshistorie, Audit-Log und Support-Log sind alle aus
  denselben Beispielläufen abgeleitet, keine zwei Bildschirme widersprechen sich.

## Was nicht geht

Alles, was einen echten Server braucht, sagt das auch, statt es vorzutäuschen — und benennt dabei
die Aktion, statt stumm zu scheitern: PowerShell auf einem Host ausführen, eine WinRM-Verbindung
testen, Mail versenden, ein Runbook importieren, ein Backup exportieren oder zurückspielen, das
Support-Log herunterladen, SQL ausführen und der KI-Assistent.

## Ihre eigene Kopie

Die Demo hält ihren Zustand ausschließlich im Browser-Tab. Zwei Besucher sehen die Änderungen des
anderen nie, und zwei Tabs desselben Browsers ebenso wenig. Ein Reload stellt die Beispieldaten
wieder her; die Schaltfläche **Zurücksetzen** in der Demo-Leiste tut dasselbe.

Bereit für den Ernstfall? Weiter mit der [Installation](./installation).
