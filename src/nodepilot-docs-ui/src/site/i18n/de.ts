import type { ArticleSlug, SitePage } from '../router'

interface Article {
  /** Category label in the article lists and the article header. */
  category: string
  /** Plain-text title for the article page, the home list and the document title. */
  title: string
  /** Title in the blog index; may contain a line break. */
  indexTitle: string
  /** One-line summary in the home list. */
  summary: string
  /** Longer teaser in the blog index. */
  teaser: string
  lead: string
  /** Author-written HTML. */
  body: string
}

/**
 * German texts of the project website; index.html carries the same texts as static markup.
 * `en.ts` has to provide every key. Values used through `data-i18n-html` and the article
 * bodies are author-written HTML.
 */
export const de = {
  meta: {
    description:
      'NodePilot verbindet PowerShell und Windows-Aktivitäten zu visuellen Workflows. Agentenlos, selbst gehostet, quelloffen und kostenlos.',
  },
  skipLink: 'Zum Inhalt',
  lang: {
    group: 'Sprache',
  },
  header: {
    brandLabel: 'NodePilot – Startseite',
    context: 'Projekt',
    docs: 'Dokumentation',
    download: 'Download',
  },
  nav: {
    label: 'Website-Navigation',
    open: 'Navigation öffnen',
    close: 'Navigation schließen',
    discover: 'ENTDECKEN',
    home: 'Übersicht',
    product: 'Das Produkt',
    blog: 'Blog',
    resources: 'RESSOURCEN',
    docs: 'Dokumentation',
    repository: 'Repository',
    releases: 'Releases',
    tagline: 'Offener Code.<br>Deine Infrastruktur.',
    license: 'Lizenz ansehen',
  },
  /** Breadcrumb in the header. */
  pages: {
    home: 'Übersicht',
    product: 'Das Produkt',
    blog: 'Blog',
    article: 'Blog / Beitrag',
    impressum: 'Impressum',
    datenschutz: 'Datenschutz',
    notfound: 'Seite nicht gefunden',
  } satisfies Record<SitePage, string>,
  /** Document titles; an article uses its own title. */
  titles: {
    home: 'Windows-Automatisierung, Schritt für Schritt.',
    product: 'Das Produkt',
    blog: 'Blog',
    impressum: 'Impressum',
    datenschutz: 'Datenschutz',
    notfound: 'Seite nicht gefunden',
  } satisfies Record<Exclude<SitePage, 'article'>, string>,
  home: {
    eyebrow: 'WINDOWS WORKFLOW ORCHESTRATION',
    title: 'Prozesse verbinden.<br><span>Abläufe verstehen.</span>',
    lead: 'Deine Windows-Automatisierung verdient mehr als einen Ordner voller Skripte.',
    description:
      'Mit NodePilot baust du Workflows im Browser, führst sie über WinRM aus und siehst, was in jedem Schritt passiert. Ohne Agenten auf den Zielsystemen.',
    download: 'NodePilot herunterladen',
    source: 'Quellcode',
    selfHosted: 'Self-hosted',
    openSource: 'Open Source',
    free: 'Kostenlos',
    noTiers: 'Keine Lizenzstufen',
    capabilityDesigner: 'Visueller Designer',
    capabilityLive: 'Live-Ausführung & Logs',
    capabilityScorch: 'SCOrch-Import',
    capabilitySso: 'LDAP & SSO',
    capabilityAi: 'Native AI Support',
    resourcesKicker: 'DIREKT INS PROJEKT',
    resourcesTitle: 'Alles, was du zum Loslegen brauchst.',
    sourceTitle: 'Ein Repository,<br>keine Beschränkungen',
    sourceText: 'Ansehen, selbst hosten, verändern.<br>NodePilot ist unter Apache 2.0 lizenziert.',
    copyLabel: 'Git-Clone-Befehl kopieren',
    copyTitle: 'Git-Befehl kopieren',
    docsTitle: 'Dokumentation',
    docsText: 'Installation, erster Workflow und Betrieb',
    repoTitle: 'Repository',
    repoText: 'Quellcode, Issues und Contributions',
    installTitle: 'NodePilot installieren',
    installText: 'Desktop-App oder Windows Service',
    blogKicker: 'AUS DER ENTWICKLUNG',
    blogTitle: 'Hinter dem nächsten Workflow.',
    blogLink: 'Zum Blog',
  },
  demo: {
    title: 'Service-Check',
    badge: 'BEISPIEL',
    viewOriginal: 'Original-Web-UI ansehen',
    tab: 'Workflow',
    stats: '4 Aktivitäten · 2 Pfade',
    canvasLabel: 'Interaktives Ablaufbeispiel: Start, Dienst prüfen, Ergebnis oder Fehlerprotokoll',
    edgeAlways: 'Immer',
    edgeSuccess: 'Bei Erfolg',
    edgeFailure: 'Bei Fehler',
    inspectorHint: 'Schritt anklicken, Details ansehen',
    originalUi: 'Original-Web-UI',
    pause: 'Animation pausieren',
    resume: 'Animation abspielen',
    pauseLabel: 'Animation des Ablaufbeispiels pausieren',
    resumeLabel: 'Animation des Ablaufbeispiels abspielen',
    idle: 'Lokales Ablaufbeispiel · keine Systemverbindung',
    running: 'Simulierter Lauf · keine Systemverbindung',
    nodes: {
      start: {
        name: 'Start',
        label: 'Start: Schritt-Details anzeigen',
        type: 'TRIGGER',
        detail: 'Manuell gestarteter Workflow',
        code: 'Start → Dienst prüfen',
      },
      check: {
        name: 'Dienst prüfen',
        label: 'Dienst prüfen: Schritt-Details anzeigen',
        type: 'SERVICE CONTROL',
        detail: 'WIN-SRV-01 / Spooler',
        code: "Get-Service -Name 'Spooler'",
      },
      result: {
        name: 'Ergebnis',
        label: 'Ergebnis: Schritt-Details anzeigen',
        type: 'RETURN DATA',
        detail: 'Daten an den nächsten Ablauf übergeben',
        code: 'Name: Spooler · Status: Running',
      },
      error: {
        name: 'Protokollieren',
        label: 'Protokollieren: Schritt-Details anzeigen',
        type: 'FEHLERPFAD',
        detail: 'Ausgabe für die Fehleranalyse erfassen',
        code: 'Fehler → Protokollierung',
      },
    },
  },
  copy: {
    done: 'Git-Befehl in die Zwischenablage kopiert.',
    failed: 'Kopieren nicht möglich. Bitte den Befehl direkt markieren.',
  },
  footer: {
    tagline: 'Agentenlose Windows-Automatisierung',
    feedback: 'Feedback',
    impressum: 'Impressum',
    datenschutz: 'Datenschutz',
  },
  product: {
    kicker: 'DAS PRODUKT',
    title: 'Ein Ablauf.<br>Von der Idee bis zum Log.',
    intro: 'Designer, Debugger und Live Ops gehören in denselben Arbeitsbereich. Nicht in drei verschiedene Werkzeuge.',
    designerTitle: 'Workflows entwerfen',
    designerText: '27 Aktivitätstypen von PowerShell bis SQL, verbunden mit Bedingungen und parallelen Pfaden auf einem Canvas.',
    liveopsTitle: 'Ausführungen verfolgen',
    liveopsText: 'Sehen, was gerade läuft, was abgeschlossen ist und was als Nächstes startet.',
    logsTitle: 'Fehler nachvollziehen',
    logsText: 'Ausgaben und strukturierte Support-Ereignisse direkt im Produkt lesen.',
    video: 'Produktvideo ansehen',
    enlarge: 'Produktbild vergrößern',
    caption: 'Original-Screenshot aus dem NodePilot-Repository',
    captionTag: 'Dunkles Design',
    detailKicker: 'BESTEHENDE AUTOMATISIERUNG',
    detailTitle: 'Deine Skripte bleiben deine Skripte.',
    detailText:
      'NodePilot führt PowerShell und Windows-Aktivitäten über WinRM aus. Vorhandene SCOrch-Runbooks lassen sich als <code>.ois_export</code> importieren. Prüfe anschließend das Importprotokoll und die Konfiguration, bevor du einen Workflow aktivierst.',
    readMore: 'In der Dokumentation weiterlesen',
    codeExample: 'Beispiel',
    codeComment: '# Bestehende Logik weiterverwenden',
    features: {
      kicker: 'FUNKTIONSUMFANG',
      title: 'Was NodePilot kann',
      docs: 'Dokumentation',
      design: {
        title: 'Entwerfen',
        a: 'Visueller Canvas mit Bedingungen und parallelen Pfaden',
        b: 'Schritt-Debugger mit Haltepunkten',
        c: 'Versionen mit Vergleich und Rücksprung',
      },
      run: {
        title: 'Ausführen',
        a: 'Agentenlos über WinRM',
        b: 'PowerShell, Dienste, Dateien, Registry, REST, SQL',
        c: 'Wiederholungen je Schritt und Sub-Workflows',
      },
      triggers: {
        title: 'Auslöser',
        a: 'Zeitplan per Cron',
        b: 'Dateiüberwachung, Ereignisprotokoll, Datenbank',
        c: 'Webhooks und externe Aufrufe',
      },
      ops: {
        title: 'Betrieb & Sicherheit',
        a: 'Rollen und Ordnerrechte',
        b: 'Audit-Log über jede Änderung',
        c: 'LDAP, Windows-SSO und Hochverfügbarkeit',
      },
      ai: {
        title: 'KI',
        a: 'Workflows und Skripte per Prompt erzeugen',
        b: 'Assistent im Designer, Wissens-Chat',
        c: 'Lokale Modelle oder OpenAI-kompatibel, standardmäßig aus',
      },
      interfaces: {
        title: 'Schnittstellen',
        a: 'REST-API und np als Kommandozeile',
        b: 'MCP-Server für KI-Agenten',
        c: 'OpenTelemetry, Prometheus, Grafana',
      },
    },
    download: 'NodePilot herunterladen',
    repository: 'Repository ansehen',
  },
  screens: {
    designer: {
      title: 'Workflow Designer',
      alt: 'Original-Screenshot des NodePilot Workflow Designers mit Workflow-Canvas und Eigenschaftenbereich.',
    },
    liveops: {
      title: 'Live Ops',
      alt: 'Original-Screenshot der NodePilot Live-Ops-Ansicht für laufende und abgeschlossene Workflows.',
    },
    logs: {
      title: 'Support Log',
      alt: 'Original-Screenshot des NodePilot Support Logs mit strukturierten Ereignissen.',
    },
    loading: 'Original-Web-UI wird geladen …',
    unavailable: 'Produktbild nicht erreichbar',
    unavailableText: 'Das Produktbild konnte nicht geladen werden.',
    openOnGithub: 'Bild auf GitHub öffnen',
  },
  gallery: {
    title: 'ORIGINAL-WEB-UI',
    close: 'Produktansicht schließen',
    tabsLabel: 'Produktansicht auswählen',
    openOriginal: 'Original auf GitHub öffnen',
    fullSize: 'In voller Größe öffnen',
    source: 'Quelldatei auf GitHub',
    caption: 'Original-Screenshot · {title}',
  },
  download: {
    kicker: 'NODEPILOT INSTALLIEREN',
    close: 'Download-Auswahl schließen',
    title: 'Wo soll NodePilot laufen?',
    lead: 'Auf deinem Rechner ausprobieren oder als Dienst in deiner Umgebung betreiben.',
    desktopTitle: 'Auf meinem Windows-PC',
    desktopText: 'Desktop-App für Windows 11 x64. Mit gebündelter Datenbank und Runtime.',
    desktopLink: 'Zu den Downloads',
    serverTitle: 'Auf einem Windows Server',
    serverText: 'Installation als Windows Service. Voraussetzungen und Einrichtung stehen in den Docs.',
    serverLink: 'Installationsanleitung',
    license: 'Kostenlos · Open Source · Apache 2.0',
    selfBuild: 'Selbst bauen?',
    sourceLink: 'Quellcode auf GitHub',
  },
  blog: {
    kicker: 'NODEPILOT / BLOG',
    title: 'Notizen aus<br>der Entwicklung.',
    intro: 'Warum bestimmte Entscheidungen getroffen wurden, wie Dinge funktionieren und was beim Automatisieren hilft.',
    filterLabel: 'Blog nach Kategorie filtern',
    filterAll: 'Alle Beiträge',
    filterBackground: 'Hintergrund',
    filterPractice: 'Praxis',
    searchPlaceholder: 'Beitrag suchen',
    searchLabel: 'Blogbeiträge durchsuchen',
    readMore: 'Beitrag lesen',
    emptyTitle: 'Kein passender Beitrag.',
    emptyText: 'Versuche einen anderen Suchbegriff oder eine andere Kategorie.',
    reset: 'Filter zurücksetzen',
    foundOne: '1 Beitrag gefunden.',
    foundMany: '{count} Beiträge gefunden.',
  },
  article: {
    back: 'Alle Beiträge',
    meta: 'NodePilot · Projekt-Blog',
    toBlog: 'Zur Blogübersicht',
    toDocs: 'Zur Dokumentation',
  },
  articles: {
    'warum-nodepilot': {
      category: 'HINTERGRUND',
      title: 'Warum es NodePilot gibt.',
      indexTitle: 'Warum es NodePilot gibt.',
      summary: 'Von PowerShell-Skripten und SCOrch zum eigenen Werkzeug.',
      teaser:
        'PowerShell löst viele Aufgaben. Für das Zusammenspiel, die Ausführung und die Fehlersuche braucht es mehr als eine Sammlung einzelner Skripte.',
      lead: 'PowerShell ist nicht das Problem. Der Überblick zwischen den Skripten ist es.',
      body: `<p>Ein Skript prüft Dienste, ein anderes kopiert Dateien. Ein drittes fragt eine Datenbank ab. Jedes für sich funktioniert. Sobald diese Aufgaben aber voneinander abhängen, reicht es nicht mehr, nur zu wissen, wo die Dateien liegen.</p>
<p>Welcher Schritt ist gelaufen? Welche Ausgabe hat er geliefert? Warum ist der nächste Schritt nicht gestartet? Und was muss angepasst werden, ohne dabei den restlichen Ablauf zu verändern?</p>
<h2>Die Arbeit zwischen den Skripten</h2>
<p>Genau hier setzt NodePilot an. PowerShell bleibt das Werkzeug für die eigentliche Systemarbeit. Der Workflow beschreibt, wie die einzelnen Schritte zusammenhängen: mit Bedingungen, Fehlerpfaden und parallelen Zweigen.</p>
<p>Das Ziel ist nicht, funktionierende Skripte durch eine neue Sprache zu ersetzen. Es geht darum, sie in einen Ablauf einzubetten, der sich gestalten, ausführen und anschließend nachvollziehen lässt.</p>
<h2>Ein Arbeitsbereich statt einzelner Ansichten</h2>
<p>NodePilot verbindet den visuellen Designer mit Ausführungshistorie, Debugging und Live Ops. Die Zielsysteme werden über WinRM angesprochen; dort wird kein zusätzlicher NodePilot-Agent benötigt. Die Anwendung selbst läuft in der eigenen Umgebung.</p>
<p>Auch vorhandene Automatisierung soll nicht verloren gehen. Deshalb gehört der Import von SCOrch-Runbooks zum Projekt. Ein Import ersetzt keine Prüfung, nimmt aber die vorhandene Struktur als Ausgangspunkt – statt mit einem leeren Canvas zu beginnen.</p>
<h2>Offen, einschließlich der Grenzen</h2>
<p>NodePilot ist unter Apache 2.0 veröffentlicht. Der Quellcode lässt sich ansehen, selbst betreiben und verändern. Es gibt keine Aufteilung in eine freie Oberfläche und kostenpflichtig gesperrte Produktfunktionen.</p>
<p>Das ist trotzdem kein Ersatz für einen Supportvertrag. Bei einem Open-Source-Projekt gehören eine sorgfältige Evaluation, Tests mit den eigenen Workflows und ein Blick in die Dokumentation dazu.</p>
<div class="article-note">Dieser Beitrag beschreibt die Motivation und den Ansatz des Projekts, keine Betriebsgarantie.</div>
<p class="article-source">Technische Grundlage: <a href="https://github.com/Sev7eNup/NodePilot#why-nodepilot" target="_blank" rel="noopener noreferrer">NodePilot README</a>. Die <a href="docs/#/de/" target="_blank" rel="noopener noreferrer">Dokumentation</a> beschreibt Einrichtung und Betrieb.</p>`,
    },
    'scorch-import': {
      category: 'PRAXIS',
      title: 'Runbooks mitnehmen. Nicht neu anfangen.',
      indexTitle: 'Runbooks mitnehmen.<br>Nicht neu anfangen.',
      summary: 'Was beim Import aus System Center Orchestrator wichtig ist.',
      teaser:
        'Was aus Aktivitäten, Verbindungen und Published Data wird – und warum das Importprotokoll zum Migrationsprozess gehört.',
      lead: 'Vorhandene Automatisierung ist mehr als eine Reihe von Kästchen. Der Import muss auch die Verbindungen und Daten dazwischen berücksichtigen.',
      body: `<p>In einem gewachsenen SCOrch-Runbook steckt viel Arbeit: die richtige Reihenfolge, Fehlerbehandlung, Bedingungen und Daten, die von einer Aktivität an die nächste weitergegeben werden. Genau das sollte bei einer Migration nicht von Hand rekonstruiert werden müssen.</p>
<h2>Mit dem vorhandenen Export starten</h2>
<p>NodePilot liest SCOrch-Exporte im Format <code>.ois_export</code>. Der Import lässt sich über die Oberfläche oder die CLI starten:</p>
<pre><code>np workflow import-scorch --file .\\runbooks.ois_export</code></pre>
<p>Unterstützte Aktivitäten werden in passende NodePilot-Aktivitäten übersetzt. Verbindungen, Bedingungen, globale Variablen und Published-Data-Verweise werden dabei ebenfalls berücksichtigt. Die Ordnerstruktur kann mit übernommen werden.</p>
<h2>Ein Importbericht ist Teil des Ergebnisses</h2>
<p>Nicht jede Aktivität und nicht jede Eigenschaft lässt sich verlustfrei übersetzen. Eine nicht unterstützte Aktivität soll deshalb nicht einfach verschwinden. NodePilot legt dafür einen deaktivierten Platzhalter an und führt die relevanten Informationen im Importbericht auf.</p>
<p>Auch unvollständige Zielsystemangaben, nicht übersetzbare Datenreferenzen oder nur näherungsweise übertragene Zeitpläne brauchen Aufmerksamkeit. Das Ergebnis ist ein Ausgangspunkt für die Prüfung, nicht die Zusage, dass jede Automatisierung unverändert läuft.</p>
<h2>Prüfen, testen, bewusst aktivieren</h2>
<p>Importierte Workflows sind zunächst deaktiviert. Zugangsdaten werden nicht aus den verschlüsselten SCOrch-Daten rekonstruiert. Vor der Aktivierung sollten deshalb die im Bericht genannten Punkte, Zielsysteme, Berechtigungen und Fehlerpfade kontrolliert werden.</p>
<p>Danach folgt ein Test mit einer geeigneten Testumgebung. Erst wenn das Verhalten zu den eigenen Anforderungen passt, wird der Workflow ausdrücklich aktiviert.</p>
<div class="article-note">Der Vorteil liegt nicht darin, jede Migrationsentscheidung zu automatisieren. Er liegt darin, mit dem tatsächlichen Runbook und einem nachvollziehbaren Bericht zu arbeiten.</div>
<p class="article-source">Grundlage und aktuelle Details: <a href="https://github.com/Sev7eNup/NodePilot#coming-from-system-center-orchestrator" target="_blank" rel="noopener noreferrer">SCOrch-Import im NodePilot README</a>.</p>`,
    },
  } satisfies Record<ArticleSlug, Article>,
  notFound: {
    title: 'Hier liegt kein Workflow.',
    text: 'Diese Seite gibt es nicht.',
    home: 'Zur Übersicht',
  },
  legal: {
    kicker: 'RECHTLICHES',
    impressum: 'Impressum',
    datenschutz: 'Datenschutz',
    germanOnly: 'Die Rechtstexte liegen nur auf Deutsch vor.',
  },
}

/** Key structure every language has to provide. */
export type Messages = typeof de
