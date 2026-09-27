import type { Lang } from '../i18n/languages'
import type { SolutionSlug } from './router'

interface Solution { title: string; summary: string; body: string }

/** Decision pages complement the tutorials; they do not promise a drop-in migration. */
export const solutions: Record<Lang, Record<SolutionSlug, Solution>> = {
  de: {
    'powershell-automation': {
      title: 'PowerShell-Skripte als Windows-Workflows automatisieren',
      summary: 'PowerShell-Skripte verbinden, gezielt auslösen und ihre Ergebnisse prüfen: mit dem visuellen Windows-Workflow-Orchestrator NodePilot.',
      body: `<h2>Wenn mehrere Skripte zusammenarbeiten müssen</h2>
<p>Ein einzelnes PowerShell-Skript kann einen Dienst prüfen oder eine Datei bereitstellen. Sobald ein weiterer Schritt von seinem Ergebnis abhängt, braucht der Ablauf eine klare Reihenfolge, Bedingungen und einen Fehlerpfad. In NodePilot werden diese Zusammenhänge im visuellen Designer modelliert; anschließend lässt sich die Ausführung jeder einzelnen Aktivität verfolgen.</p>
<h2>Vorhandene Skripte weiterverwenden</h2>
<p>Die Run-Script-Aktivität führt PowerShell lokal auf dem NodePilot-Host oder über WinRM auf einem verwalteten Windows-System aus. Für entfernte Ausführungen müssen WinRM, Dienstidentität und Berechtigungen zum Zielsystem passen. Auf dem Ziel wird kein zusätzlicher NodePilot-Agent installiert.</p>
<p>Ausgaben werden über benannte Variablen an nachfolgende Schritte übergeben. Bedingungen entscheiden über den nächsten Pfad. Für wiederkehrende Aufgaben stehen neben manuellen Starts auch zeitgesteuerte Auslöser zur Verfügung.</p>
<h2>Mit einer überprüfbaren Aufgabe beginnen</h2>
<p>Der <a data-site-path="blog/first-workflow">erste Workflow mit einem PowerShell-Schritt</a> liest zunächst nur den Rechnernamen aus. Damit wird der Weg vom Designer bis zur Ausgabe geprüft, bevor Aktionen mit Schreibzugriff hinzukommen. Die <a data-docs-path="getting-started/quickstart">Kurzanleitung</a> führt durch den Produkteinstieg.</p>
<h2>Was zum Betrieb dazugehört</h2>
<p>Ein erfolgreicher Status allein belegt noch nicht das fachlich richtige Ergebnis. Vorher sollte feststehen, welche Ausgabe erwartet wird und wie Fehler behandelt werden. NodePilot zeigt Ausführung und Protokolle an; das Testen der Skripte und die Auswahl geeigneter Berechtigungen bleiben Teil der eigenen Arbeit.</p>
<p><a data-site-path="product">Designer, Ausführungshistorie und Debugging ansehen</a> oder <a data-site-path="walkthrough">die geführte Browserdemo ausprobieren</a>.</p>`,
    },
    'scorch-alternative': {
      title: 'Eine Open-Source-Alternative zu System Center Orchestrator prüfen',
      summary: 'SCOrch ablösen oder aktualisieren? Supportdaten, Wege wie Azure Automation und SMA und wie sich Runbooks mit NodePilot importieren und prüfen lassen.',
      body: `<h2>Wie lange System Center Orchestrator unterstützt wird</h2>
<p>Für die Planung zählt, welche Orchestrator-Version im Einsatz ist. Microsoft legt für jede Version ein festes Supportende fest (Fixed Lifecycle Policy). Angegeben ist jeweils der letzte unterstützte Tag:</p>
<table><thead><tr><th>Version</th><th>Mainstream-Support</th><th>Erweiterter Support</th></tr></thead><tbody><tr><td><a href="https://learn.microsoft.com/de-de/lifecycle/products/system-center-2016-orchestrator">System Center 2016 Orchestrator</a></td><td>11.01.2022</td><td>11.01.2027</td></tr><tr><td><a href="https://learn.microsoft.com/de-de/lifecycle/products/system-center-2019-orchestrator">System Center 2019 Orchestrator</a></td><td>09.04.2024</td><td>09.04.2029</td></tr><tr><td><a href="https://learn.microsoft.com/de-de/lifecycle/products/system-center-2022-orchestrator">System Center 2022 Orchestrator</a></td><td>13.04.2027</td><td>13.04.2032</td></tr><tr><td><a href="https://learn.microsoft.com/de-de/lifecycle/products/system-center-2025-orchestrator">System Center 2025 Orchestrator</a></td><td>08.01.2030</td><td>09.01.2035</td></tr></tbody></table>
<p>Für System Center 2016 Orchestrator endet der erweiterte Support am 11. Januar 2027; danach liefert Microsoft keine Sicherheitsupdates mehr. Neuere Versionen bleiben deutlich länger im Support. Ein Wechsel des Werkzeugs ist also keine Pflicht, sondern eine Entscheidung zwischen mehreren Wegen.</p>
<h2>Die Wege nach SCOrch 2016</h2>
<ul><li><strong>Upgrade auf System Center 2025 Orchestrator:</strong> Die Runbooks bleiben in derselben Umgebung, der Support reicht bis Januar 2035. Lizenzierung und Betrieb der System-Center-Infrastruktur bleiben bestehen.</li><li><strong>Azure Automation:</strong> Microsoft bietet ein Migrations-Toolkit an, das die eigene <a href="https://learn.microsoft.com/de-de/azure/automation/automation-orchestrator-migration">Anleitung</a> als Beta führt. Es wandelt Runbooks in grafische Runbooks um. Monitor-Aktivitäten, Variablen und Verbindungen werden nicht übernommen, und für den Zugriff auf lokale Systeme ist ein Hybrid Runbook Worker nötig.</li><li><strong>Service Management Automation (SMA):</strong> SMA führt Runbooks im eigenen Rechenzentrum aus, unterstützt aber keine grafischen Runbooks. Orchestrator-Runbooks müssen dafür laut Microsoft von Hand in PowerShell neu geschrieben werden.</li><li><strong>NodePilot:</strong> ein quelloffener, selbst gehosteter Workflow-Orchestrator für Windows mit grafischem Designer und einem Import für <code>.ois_export</code>-Dateien. Die Runbook-Automatisierung bleibt im eigenen Netz; Aufwand entsteht beim Prüfen und Nacharbeiten der importierten Abläufe.</li></ul>
<p>Welcher Weg passt, hängt von Lizenzsituation, Cloud-Strategie und den eingesetzten Integration Packs ab. Eine Bestandsaufnahme der vorhandenen Runbooks ist in jedem Fall der erste Schritt.</p>
<h2>Mit den bestehenden Runbooks anfangen</h2>
<p>Wer System Center Orchestrator ablösen möchte, muss mehr als die sichtbaren Aktivitäten übernehmen. Auch Verbindungen, Bedingungen, Published Data und die verwendeten Identitäten bestimmen das Verhalten eines Runbooks. NodePilot bietet einen Import für SCOrch-Exporte im Format <code>.ois_export</code>, um vorhandene Abläufe als Ausgangspunkt zu verwenden.</p>
<h2>Was der Import leistet und wo Prüfung nötig ist</h2>
<p>Unterstützte Aktivitäten werden auf NodePilot-Aktivitäten abgebildet. Nicht unterstützte Aktivitäten bleiben als deaktivierte Platzhalter sichtbar. Ein Importbericht hält fest, welche Stellen Aufmerksamkeit brauchen. Verschlüsselte Zugangsdaten werden nicht rekonstruiert; importierte Workflows sind zunächst deaktiviert.</p>
<p>Im Beitrag <a data-site-path="blog/scorch-import">SCOrch-Runbooks importieren und das Ergebnis prüfen</a> sind der Importablauf und seine Grenzen beschrieben. NodePilot ist kein zugesagter Ersatz für jede Integration oder jedes Verhalten von System Center Orchestrator.</p>
<h2>Eine Migration in kleinen Schritten beurteilen</h2>
<ol><li>Ein repräsentatives Runbook und seine erwarteten Ergebnisse auswählen.</li><li>Den Export importieren und alle Hinweise im Importbericht durchgehen.</li><li>Zielsysteme, Dienstkonten, Datenreferenzen und Zeitpläne prüfen.</li><li>Erfolgs- und Fehlerfälle in einer Testumgebung mit dem bisherigen Verhalten vergleichen.</li><li>Erst danach den geprüften Workflow bewusst aktivieren.</li></ol>
<h2>SCOrch und NodePilot im Vergleich</h2>
<table><thead><tr><th>Merkmal</th><th>System Center Orchestrator</th><th>NodePilot</th></tr></thead><tbody><tr><td>Zielsysteme</td><td>agentenlos</td><td>agentenlos über WinRM, dasselbe Modell</td></tr><tr><td>Runbooks erstellen</td><td>Runbook Designer als Desktop-Anwendung</td><td>Designer im Browser mit Live-Status jedes Schritts</td></tr><tr><td>Testen</td><td>Runbook Tester mit Haltepunkten</td><td>Schritt-Debugger in der echten Engine, mit bedingten Haltepunkten und Variablenwerten zur Laufzeit</td></tr><tr><td>Versionen</td><td>nicht eingebaut</td><td>jede Änderung als Version, mit Vergleich und Rollback</td></tr><tr><td>Schnittstellen</td><td>Web-API zum Starten und Überwachen von Jobs</td><td>REST-API für alle Funktionen, <code>np</code>-CLI und MCP-Server</td></tr><tr><td>Datenbank</td><td>SQL Server</td><td>PostgreSQL oder SQL Server</td></tr><tr><td>Lizenz</td><td>kommerziell</td><td>Apache 2.0, keine Kosten pro Host</td></tr><tr><td>Support</td><td>Hersteller</td><td>Community; ein Open-Source-Projekt mit einem Maintainer</td></tr></tbody></table>
<p>Die Tabelle beschreibt den Funktionsumfang. Ob ein bestimmtes Runbook ohne Nacharbeit läuft, zeigt erst der Import mit anschließendem Test.</p>
<h2>Eigener Betrieb und offene Lizenz</h2>
<p>NodePilot läuft in der eigenen Windows-Umgebung und ist unter Apache 2.0 veröffentlicht. Die <a data-site-path="self-hosted-automation">Voraussetzungen für den eigenen Betrieb</a> und die <a data-docs-path="getting-started/installation">Installationsanleitung</a> helfen bei der Evaluation. Ein Supportvertrag oder eine Betriebsgarantie folgt aus der Open-Source-Lizenz nicht.</p>`,
    },
    'self-hosted-automation': {
      title: 'Windows-Automatisierung selbst hosten – mit Open Source',
      summary: 'NodePilot in der eigenen Infrastruktur betreiben: Windows-Workflows, WinRM, PostgreSQL oder SQL Server und Quellcode unter Apache 2.0.',
      body: `<h2>Workflows in der eigenen Umgebung ausführen</h2>
<p>NodePilot ist ein selbst gehosteter Workflow-Orchestrator für Windows. API, Engine und Scheduler laufen als Windows-Dienst; die Bedienung erfolgt im Browser oder über die Desktop-App. Entfernte Windows-Systeme werden über WinRM angesprochen. Ein zusätzlicher NodePilot-Agent auf diesen Zielsystemen ist nicht erforderlich.</p>
<h2>Desktop oder Server wählen</h2>
<p>Die Desktop-Variante ermöglicht den lokalen Einstieg. Für den Serverbetrieb werden eine Windows-Umgebung und eine unterstützte PostgreSQL- oder SQL-Server-Datenbank benötigt. Versionsanforderungen, Installationswege und die jeweiligen Komponenten stehen in der <a data-docs-path="getting-started/installation">Installationsdokumentation</a>.</p>
<h2>Offener Code, eigener Betrieb</h2>
<p>Der Quellcode steht unter Apache 2.0. Es gibt keine kostenpflichtig gesperrten Produktfunktionen. Der Code lässt sich prüfen und verändern. Zum eigenen Betrieb gehören dafür auch Updates, Backups, Wiederherstellungstests sowie passende Dienstkonten und Zugriffsrechte.</p>
<p>Selbst gehostet bedeutet nicht, dass jede konfigurierte Aktion ohne externe Verbindung auskommt. REST-Aufrufe, E-Mail und eine optional angebundene KI verwenden die selbst eingerichteten Dienste. Entscheidend ist die tatsächliche Konfiguration der Workflows und Integrationen.</p>
<h2>Vor der Einführung ausprobieren</h2>
<p>Die <a data-site-path="walkthrough">Browserdemo</a> zeigt die Oberfläche mit simulierten Daten. Für die eigene Installation bietet der Beitrag <a data-site-path="blog/first-workflow">Einen ersten Workflow erstellen</a> einen kleinen Funktionstest. <a data-site-path="blog/why-nodepilot">Warum es NodePilot gibt</a> beschreibt die Motivation und Grenzen des Projekts.</p>`,
    },
  },
  en: {
    'powershell-automation': {
      title: 'Automate PowerShell scripts as Windows workflows',
      summary: 'Connect PowerShell scripts, trigger executions and inspect results with NodePilot, a visual workflow orchestrator for Windows.',
      body: `<h2>When several scripts need to work together</h2>
<p>A single PowerShell script can check a service or prepare a file. Once another step depends on its output, the process needs an explicit order, conditions and a failure path. NodePilot models those connections in a visual designer and lets you inspect the execution of each activity.</p>
<h2>Keep using your existing scripts</h2>
<p>The Run Script activity executes PowerShell locally on the NodePilot host or over WinRM on a managed Windows system. Remote execution requires working WinRM access, an appropriate service identity and target permissions. No additional NodePilot agent is installed on the target.</p>
<p>Named output variables pass results to subsequent steps. Conditions choose the next path. Recurring tasks can use scheduled triggers as well as manual starts.</p>
<h2>Start with a task you can verify</h2>
<p>The <a data-site-path="blog/first-workflow">first workflow with a PowerShell step</a> only reads the computer name. It checks the path from the designer to an actual output before you add actions that write data. The <a data-docs-path="getting-started/quickstart">quickstart</a> covers getting started with the product.</p>
<h2>Plan for operations</h2>
<p>A successful status alone does not prove that the outcome is correct. Define the expected output and how failures should be handled. NodePilot exposes executions and logs; testing your scripts and choosing suitable permissions remain part of your work.</p>
<p><a data-site-path="product">Explore the designer, execution history and debugging</a> or <a data-site-path="walkthrough">try the guided browser demo</a>.</p>`,
    },
    'scorch-alternative': {
      title: 'Evaluate an open-source alternative to System Center Orchestrator',
      summary: 'Replace or upgrade SCOrch? Support end dates, options such as Azure Automation and SMA, and how to import and verify runbooks with NodePilot.',
      body: `<h2>How long System Center Orchestrator is supported</h2>
<p>Planning starts with the Orchestrator version you run. Microsoft sets a fixed end of support for each version (Fixed Lifecycle Policy). The table lists the last supported day:</p>
<table><thead><tr><th>Version</th><th>Mainstream support</th><th>Extended support</th></tr></thead><tbody><tr><td><a href="https://learn.microsoft.com/en-us/lifecycle/products/system-center-2016-orchestrator">System Center 2016 Orchestrator</a></td><td>2022-01-11</td><td>2027-01-11</td></tr><tr><td><a href="https://learn.microsoft.com/en-us/lifecycle/products/system-center-2019-orchestrator">System Center 2019 Orchestrator</a></td><td>2024-04-09</td><td>2029-04-09</td></tr><tr><td><a href="https://learn.microsoft.com/en-us/lifecycle/products/system-center-2022-orchestrator">System Center 2022 Orchestrator</a></td><td>2027-04-13</td><td>2032-04-13</td></tr><tr><td><a href="https://learn.microsoft.com/en-us/lifecycle/products/system-center-2025-orchestrator">System Center 2025 Orchestrator</a></td><td>2030-01-08</td><td>2035-01-09</td></tr></tbody></table>
<p>Extended support for System Center 2016 Orchestrator ends on January 11, 2027; after that, Microsoft ships no more security updates. Newer versions stay supported much longer, so changing tools is not mandatory. It is a choice between several paths.</p>
<h2>The paths after SCOrch 2016</h2>
<ul><li><strong>Upgrade to System Center 2025 Orchestrator:</strong> runbooks stay in the same environment, with support until January 2035. System Center licensing and operations remain as they are.</li><li><strong>Azure Automation:</strong> Microsoft offers a migration toolkit that its own <a href="https://learn.microsoft.com/en-us/azure/automation/automation-orchestrator-migration">guide</a> labels as beta. It converts runbooks into graphical runbooks. Monitor activities, variables and connections are not carried over, and reaching on-premises systems requires a Hybrid Runbook Worker.</li><li><strong>Service Management Automation (SMA):</strong> SMA runs runbooks in your own datacenter but does not support graphical runbooks. According to Microsoft, Orchestrator runbooks have to be rewritten in PowerShell by hand.</li><li><strong>NodePilot:</strong> an open-source, self-hosted workflow orchestrator for Windows with a graphical designer and an importer for <code>.ois_export</code> files. Runbook automation stays inside your network; the effort goes into reviewing and completing the imported workflows.</li></ul>
<p>The right path depends on licensing, cloud strategy and the Integration Packs in use. Either way, an inventory of your existing runbooks comes first.</p>
<h2>Start with your existing runbooks</h2>
<p>Replacing System Center Orchestrator involves more than copying visible activities. Connections, conditions, Published Data and execution identities also determine a runbook's behaviour. NodePilot imports SCOrch exports in <code>.ois_export</code> format to use existing automation as a starting point.</p>
<h2>What the import does and what needs review</h2>
<p>Supported activities are mapped to NodePilot activities. Unsupported activities remain visible as disabled placeholders. An import report records areas that need attention. Encrypted credentials are not reconstructed, and imported workflows start out disabled.</p>
<p>The article <a data-site-path="blog/scorch-import">Importing SCOrch runbooks and reviewing the result</a> explains the process and its limits. NodePilot does not promise to replace every System Center Orchestrator integration or behaviour.</p>
<h2>Evaluate a migration in small steps</h2>
<ol><li>Select a representative runbook and define its expected results.</li><li>Import the export and review every warning in the import report.</li><li>Check target machines, service accounts, data references and schedules.</li><li>Compare successful and failing cases with the previous behaviour in a test environment.</li><li>Explicitly enable the reviewed workflow only after those checks.</li></ol>
<h2>SCOrch and NodePilot compared</h2>
<table><thead><tr><th>Aspect</th><th>System Center Orchestrator</th><th>NodePilot</th></tr></thead><tbody><tr><td>Target systems</td><td>agentless</td><td>agentless over WinRM, the same model</td></tr><tr><td>Authoring</td><td>Runbook Designer desktop application</td><td>browser designer with live status for every step</td></tr><tr><td>Testing</td><td>Runbook Tester with breakpoints</td><td>step debugger in the real engine, with conditional breakpoints and runtime variable overrides</td></tr><tr><td>Versioning</td><td>not built in</td><td>every edit kept as a version, with diff and rollback</td></tr><tr><td>Interfaces</td><td>web API to start and monitor jobs</td><td>REST API for every operation, <code>np</code> CLI and MCP server</td></tr><tr><td>Database</td><td>SQL Server</td><td>PostgreSQL or SQL Server</td></tr><tr><td>Licence</td><td>commercial</td><td>Apache 2.0, no per-host cost</td></tr><tr><td>Support</td><td>vendor</td><td>community; a single-maintainer open-source project</td></tr></tbody></table>
<p>The table compares capabilities. Whether a specific runbook works without rework only shows after importing and testing it.</p>
<h2>Your own infrastructure and an open licence</h2>
<p>NodePilot runs in your Windows environment and is released under Apache 2.0. Review the <a data-site-path="self-hosted-automation">self-hosting requirements</a> and <a data-docs-path="getting-started/installation">installation guide</a> during evaluation. An open-source licence does not include a support contract or an operational guarantee.</p>`,
    },
    'self-hosted-automation': {
      title: 'Self-hosted Windows automation with open source',
      summary: 'Run NodePilot in your own infrastructure: Windows workflows, WinRM, PostgreSQL or SQL Server and source code under Apache 2.0.',
      body: `<h2>Run workflows in your own environment</h2>
<p>NodePilot is a self-hosted workflow orchestrator for Windows. Its API, engine and scheduler run as a Windows service, controlled through a browser or desktop app. Remote Windows systems are reached over WinRM. No additional NodePilot agent is required on those targets.</p>
<h2>Choose desktop or server</h2>
<p>The desktop edition provides a local starting point. A server installation requires a Windows environment and a supported PostgreSQL or SQL Server database. The <a data-docs-path="getting-started/installation">installation documentation</a> lists version requirements, installation paths and the components involved.</p>
<h2>Open code, your own operations</h2>
<p>The source code is available under Apache 2.0, without paid feature tiers. You can inspect and modify it. Operating your installation also means managing updates, backups, recovery tests, service accounts and access rights.</p>
<p>Self-hosting does not mean that every configured action works without an external connection. REST calls, email and an optional AI integration use the services you configure. The actual workflow and integration configuration determines those connections.</p>
<h2>Try it before introducing it</h2>
<p>The <a data-site-path="walkthrough">browser demo</a> shows the interface with simulated data. For your own installation, <a data-site-path="blog/first-workflow">Creating a first workflow</a> provides a small functional test. <a data-site-path="blog/why-nodepilot">Why NodePilot exists</a> explains the motivation and limits of the project.</p>`,
    },
  },
}

const examples: Record<Lang, Record<SolutionSlug, string>> = {
  de: {
    'powershell-automation': `<h2>Beispiel: Einen Windows-Dienst prüfen</h2>
<p>Ein manueller Trigger startet eine Run-Script-Aktivität. Das folgende Skript liest den Zustand des Windows-Zeitdienstes aus, ohne ihn zu verändern:</p>
<pre><code>Get-Service -Name W32Time -ErrorAction Stop | Select-Object Name, Status</code></pre>
<p>Die Ausgabe landet in einer benannten Variable; das Ausführungsprotokoll zeigt den tatsächlichen Zustand. Ein abgeschlossener Skriptlauf bedeutet noch nicht, dass der Dienst läuft. Erst danach kommen eine Bedingung und ein Benachrichtigungsschritt für den Fehlerfall hinzu. <a data-docs-path="activities-reference">Run Script und Ausgabevariablen</a> beschreibt die Konfiguration.</p>
<figure><img data-media="designer" alt="NodePilot-Designer mit PowerShell-Aktivität und verbundenen Workflow-Schritten"><figcaption>Der Designer zeigt einen Beispielworkflow; die Dienstprüfung oben ist ein eigener kleiner Einstieg.</figcaption></figure>
<p><a data-site-path="walkthrough">Einen Workflow in der geführten Demo ausführen und prüfen</a>.</p>`,
    'scorch-alternative': `<h2>Beispiel: Eine tägliche Statusabfrage migrieren</h2>
<p>Ein guter Anfang ist ein Runbook, das auf einem Testrechner einen Dienstzustand abfragt und das Ergebnis weitergibt. Erwartete Ausgabe und Fehlerverhalten werden vorab notiert. Nach dem Import werden Platzhalter ersetzt und der Ablauf mit einem vorhandenen sowie einem absichtlich ungültigen Dienstnamen getestet. Erst wenn Ausgabe, Verzweigung und Protokoll übereinstimmen, wird der Zeitplan aktiviert.</p>
<table><thead><tr><th>Prüfpunkt</th><th>SCOrch-Ausgangspunkt</th><th>In NodePilot prüfen</th></tr></thead><tbody><tr><td>Aktivitäten</td><td>Runbook und verwendete Integration Packs</td><td>Abbildung im Importbericht; Platzhalter ersetzen</td></tr><tr><td>Datenfluss</td><td>Published Data und Linkbedingungen</td><td>Variablenreferenzen und Zweige mit echten Testwerten</td></tr><tr><td>Identität</td><td>Ausführungskonto und gespeicherte Zugangsdaten</td><td>Zugangsdaten neu konfigurieren und Zielzugriff testen</td></tr><tr><td>Zeitplan</td><td>Bisherige Auslöser</td><td>Trigger prüfen; importierte Workflows bleiben zunächst deaktiviert</td></tr></tbody></table>
<figure><img data-media="liveops" alt="LiveOps-Ansicht von NodePilot zur Überwachung von Workflow-Ausführungen"><figcaption>Die LiveOps-Ansicht hilft beim Prüfen der Ausführung. Der Screenshot zeigt die Oberfläche, keinen dokumentierten Migrationstest.</figcaption></figure>
<p><a data-docs-path="import-export">SCOrch-Import, Aktivitätszuordnung und Einschränkungen</a> · <a data-site-path="walkthrough">Ausführung und Fehlersuche in der Browserdemo ansehen</a>.</p>`,
    'self-hosted-automation': `<h2>Beispiel: Ein interner täglicher Zustandsbericht</h2>
<p>NodePilot läuft auf einem internen Windows-Host mit eigener Datenbank. Ein geplanter Workflow fragt über WinRM den Zustand eines freigegebenen Testservers ab, protokolliert das Ergebnis und meldet eine Abweichung über den eingerichteten E-Mail-Dienst. Für die erste Prüfung genügt eine rein lesende Abfrage; Dienstneustarts gehören in einen separat geprüften Ablauf.</p>
<p>Die Dienstidentität wird auf die benötigten Ziele und Rechte begrenzt. WinRM, HTTPS-Konfiguration, Sicherung der Datenbank und Wiederherstellung sollten geprüft sein, bevor der Bericht Teil des Betriebs wird. Die <a data-docs-path="security/hardening">Anleitung zur Absicherung</a> und die <a data-docs-path="configuration/remote-execution">Konfiguration für entfernte Ausführung</a> beschreiben diese Voraussetzungen.</p>
<figure><img data-media="dashboard" alt="NodePilot-Dashboard mit Übersicht über Workflows und Ausführungen"><figcaption>Die Übersicht gehört zur selbst betriebenen NodePilot-Instanz; angezeigt wird eine Beispielumgebung.</figcaption></figure>
<p><a data-site-path="walkthrough">Die Bedienung vor der Installation in der Demo kennenlernen</a>.</p>`,
  },
  en: {
    'powershell-automation': `<h2>Example: Check a Windows service</h2>
<p>A manual trigger starts a Run Script activity. This script reads the Windows Time service state without changing it:</p>
<pre><code>Get-Service -Name W32Time -ErrorAction Stop | Select-Object Name, Status</code></pre>
<p>Store its output in a named variable and inspect the actual state in the execution log. A completed script does not prove the service is running. Then add a condition and a notification for the failure branch. See <a data-docs-path="activities-reference">Run Script and output variables</a> for configuration.</p>
<figure><img data-media="designer" alt="NodePilot designer with a PowerShell activity and connected workflow steps"><figcaption>The designer shows an example workflow; the service check above is a separate small starting point.</figcaption></figure>
<p><a data-site-path="walkthrough">Run a workflow and inspect its result in the guided demo</a>.</p>`,
    'scorch-alternative': `<h2>Example: Migrate a daily status check</h2>
<p>Start with a runbook that reads a service state on a test machine and passes the result on. Record its expected output and failure behaviour. Import the export, replace placeholders where needed and test both an existing and a deliberately invalid service name. Compare output, branching and logs before enabling the schedule.</p>
<table><thead><tr><th>Review area</th><th>SCOrch starting point</th><th>Check in NodePilot</th></tr></thead><tbody><tr><td>Activities</td><td>Runbook and Integration Packs</td><td>Mappings in the import report; replace placeholders</td></tr><tr><td>Data flow</td><td>Published Data and link conditions</td><td>Variable references and branches with real test values</td></tr><tr><td>Identity</td><td>Execution account and stored credentials</td><td>Reconfigure credentials and verify target access</td></tr><tr><td>Schedule</td><td>Existing triggers</td><td>Review triggers; imported workflows start disabled</td></tr></tbody></table>
<figure><img data-media="liveops" alt="NodePilot LiveOps view for monitoring workflow executions"><figcaption>LiveOps helps inspect execution. This screenshot illustrates the interface, not a documented migration test.</figcaption></figure>
<p><a data-docs-path="import-export">SCOrch import, activity mapping and limitations</a> · <a data-site-path="walkthrough">Explore execution and troubleshooting in the browser demo</a>.</p>`,
    'self-hosted-automation': `<h2>Example: An internal daily status report</h2>
<p>Run NodePilot on an internal Windows host with your own database. A scheduled workflow uses WinRM to query an approved test server, records the result and reports deviations through your configured email service. Start with a read-only query; service restarts belong in a separately reviewed process.</p>
<p>Limit the service identity to the targets and permissions it needs. Verify WinRM, HTTPS configuration, database backups and recovery before relying on the report in operations. The <a data-docs-path="security/hardening">hardening guide</a> and <a data-docs-path="configuration/remote-execution">remote execution configuration</a> explain these requirements.</p>
<figure><img data-media="dashboard" alt="NodePilot dashboard showing workflows and executions"><figcaption>This overview belongs to the self-hosted NodePilot instance; the screenshot shows an example environment.</figcaption></figure>
<p><a data-site-path="walkthrough">Explore the interface in the demo before installing</a>.</p>`,
  },
}

for (const lang of ['de', 'en'] as const) {
  for (const slug of Object.keys(examples[lang]) as SolutionSlug[]) solutions[lang][slug].body += examples[lang][slug]
}
