import type { Messages } from './de'

/** English texts of the project website. `satisfies` holds the keys to those of `de.ts`. */
export const en = {
  experience: {
    "nav": "Try it",
    "title": "Try NodePilot",
    "kicker": "TRY IT YOURSELF · ABOUT 2 MINUTES",
    "headline": "Your first task with NodePilot.",
    "intro": "Start a workflow, check its result and then find the cause of a failure. A short walkthrough guides you directly through the product interface.",
    "firstKicker": "YOUR FIRST SUCCESSFUL EXECUTION",
    "firstTitle": "Deliver a file.",
    "firstText": "Change a configuration value. PowerShell creates your file, File Copy delivers it and Return Data shows you the result.",
    "firstStep": "Change a value in the start dialog",
    "secondStep": "Start and follow the workflow",
    "thirdStep": "Check file content and destination",
    "firstAction": "Start guided walkthrough",
    "secondKicker": "NEXT · OR JUMP STRAIGHT IN",
    "secondTitle": "Understand a failure.",
    "secondText": "A file was created but not copied. Inspect a prepared execution and discover where it failed.",
    "clue": "Which activity is affected – and what does its log tell you?",
    "secondAction": "Investigate a failure",
    "notice": "No sign-up or installation. The browser demo uses simulated data and writes no real files. Changes stay in your tab.",
    "demo": "Explore the demo",
    "docs": "Create your first workflow"
  },
  meta: {
    description:
      'NodePilot connects PowerShell and Windows activities into visual workflows. Agentless, self-hosted, open source and free.',
  },
  skipLink: 'Skip to content',
  lang: {
    group: 'Language',
  },
  header: {
    brandLabel: 'NodePilot – Home',
    context: 'Project',
    docs: 'Documentation',
    download: 'Download',
  },
  nav: {
    label: 'Website navigation',
    open: 'Open navigation',
    close: 'Close navigation',
    discover: 'DISCOVER',
    home: 'Overview',
    product: 'The product',
    blog: 'Blog',
    resources: 'RESOURCES',
    docs: 'Documentation',
    liveDemo: 'Live demo',
    repository: 'Repository',
    releases: 'Releases',
    tagline: 'Open code.<br>Your infrastructure.',
    license: 'View license',
  },
  pages: {
    experience: "Experience NodePilot",
    home: 'Overview',
    product: 'The product',
    blog: 'Blog',
    article: 'Blog / Post',
    impressum: 'Legal notice',
    datenschutz: 'Privacy policy',
    notfound: 'Page not found',
  },
  titles: {
    experience: "Experience NodePilot",
    home: 'Windows automation, step by step.',
    product: 'The product',
    blog: 'Blog',
    impressum: 'Legal notice',
    datenschutz: 'Privacy policy',
    notfound: 'Page not found',
  },
  home: {
    eyebrow: 'WINDOWS WORKFLOW ORCHESTRATION',
    title: 'Connect processes.<br><span>See how they run.</span>',
    lead: 'Your Windows automation deserves more than a folder full of scripts.',
    description:
      'With NodePilot you build workflows in the browser, run them over WinRM and see what happens at every step. No agents on the target systems.',
    download: 'Download NodePilot',
    liveDemo: 'Open live demo',
    source: 'Source code',
    selfHosted: 'Self-hosted',
    openSource: 'Open source',
    free: 'Free',
    noTiers: 'No license tiers',
    capabilityDesigner: 'Visual designer',
    capabilityLive: 'Live runs & logs',
    capabilityScorch: 'SCOrch import',
    capabilitySso: 'LDAP & SSO',
    capabilityAi: 'Native AI support',
    resourcesKicker: 'STRAIGHT TO THE PROJECT',
    resourcesTitle: 'Everything you need to get started.',
    sourceTitle: 'One repository,<br>no restrictions',
    sourceText: 'Read it, host it, change it.<br>NodePilot is licensed under Apache 2.0.',
    copyLabel: 'Copy git clone command',
    copyTitle: 'Copy git command',
    docsTitle: 'Documentation',
    docsText: 'Installation, first workflow and operations',
    liveDemoTitle: 'Live demo',
    liveDemoText: 'The whole interface in your browser, with sample data',
    repoTitle: 'Repository',
    repoText: 'Source code, issues and contributions',
    installTitle: 'Install NodePilot',
    installText: 'Desktop app or Windows service',
    blogKicker: 'FROM DEVELOPMENT',
    blogTitle: 'Behind the next workflow.',
    blogLink: 'Visit the blog',
  },
  demo: {
    title: 'Service check',
    badge: 'EXAMPLE',
    viewOriginal: 'View the original web UI',
    tab: 'Workflow',
    stats: '4 activities · 2 paths',
    canvasLabel: 'Interactive workflow example: start, check service, result or error log',
    edgeAlways: 'Always',
    edgeSuccess: 'On Success',
    edgeFailure: 'On Failure',
    inspectorHint: 'Click a step to see its details',
    originalUi: 'Original web UI',
    pause: 'Pause animation',
    resume: 'Play animation',
    pauseLabel: 'Pause the workflow example animation',
    resumeLabel: 'Play the workflow example animation',
    idle: 'Local workflow example · no system connection',
    running: 'Simulated run · no system connection',
    nodes: {
      start: {
        name: 'Start',
        label: 'Start: show step details',
        type: 'TRIGGER',
        detail: 'Workflow started manually',
        code: 'Start → Check service',
      },
      check: {
        name: 'Check service',
        label: 'Check service: show step details',
        type: 'SERVICE CONTROL',
        detail: 'WIN-SRV-01 / Spooler',
        code: "Get-Service -Name 'Spooler'",
      },
      result: {
        name: 'Result',
        label: 'Result: show step details',
        type: 'RETURN DATA',
        detail: 'Pass data to the next workflow',
        code: 'Name: Spooler · Status: Running',
      },
      error: {
        name: 'Log error',
        label: 'Log error: show step details',
        type: 'ERROR PATH',
        detail: 'Capture output for troubleshooting',
        code: 'Error → Logging',
      },
    },
  },
  copy: {
    done: 'Git command copied to the clipboard.',
    failed: 'Copying is not possible. Please select the command directly.',
  },
  footer: {
    tagline: 'Agentless Windows automation',
    feedback: 'Feedback',
    impressum: 'Legal notice',
    datenschutz: 'Privacy',
  },
  product: {
    kicker: 'THE PRODUCT',
    title: 'One workflow.<br>From idea to log.',
    intro: 'Designer, debugger and Live Ops belong in the same workspace. Not in three different tools.',
    designerTitle: 'Design workflows',
    designerText: '27 activity types from PowerShell to SQL, wired up with conditions and parallel paths on one canvas.',
    liveopsTitle: 'Follow executions',
    liveopsText: 'See what is running, what has finished and what starts next.',
    logsTitle: 'Trace errors',
    logsText: 'Read output and structured support events right in the product.',
    video: 'Watch the product video',
    enlarge: 'Enlarge product image',
    caption: 'Original screenshot from the NodePilot repository',
    captionTag: 'Dark theme',
    detailKicker: 'EXISTING AUTOMATION',
    detailTitle: 'Your scripts stay your scripts.',
    detailText:
      'NodePilot runs PowerShell and Windows activities over WinRM. Existing SCOrch runbooks can be imported as <code>.ois_export</code> files. Review the import log and the configuration before you enable a workflow.',
    readMore: 'Read more in the documentation',
    codeExample: 'Example',
    codeComment: '# Reuse existing logic',
    features: {
      kicker: 'WHAT IT DOES',
      title: 'What NodePilot can do',
      docs: 'Documentation',
      design: {
        title: 'Design',
        a: 'Visual canvas with conditions and parallel paths',
        b: 'Step debugger with breakpoints',
        c: 'Versions with diff and rollback',
      },
      run: {
        title: 'Run',
        a: 'Agentless over WinRM',
        b: 'PowerShell, services, files, registry, REST, SQL',
        c: 'Per-step retries and sub-workflows',
      },
      triggers: {
        title: 'Triggers',
        a: 'Schedules by cron',
        b: 'File watcher, event log, database',
        c: 'Webhooks and external calls',
      },
      ops: {
        title: 'Operations & security',
        a: 'Roles and folder permissions',
        b: 'An audit log for every change',
        c: 'LDAP, Windows SSO and high availability',
      },
      ai: {
        title: 'AI',
        a: 'Generate workflows and scripts from a prompt',
        b: 'Assistant in the designer, knowledge chat',
        c: 'Local models or OpenAI-compatible, off by default',
      },
      interfaces: {
        title: 'Interfaces',
        a: 'REST API and np on the command line',
        b: 'MCP server for AI agents',
        c: 'OpenTelemetry, Prometheus, Grafana',
      },
    },
    download: 'Download NodePilot',
    repository: 'View repository',
  },
  screens: {
    designer: {
      title: 'Workflow Designer',
      alt: 'Original screenshot of the NodePilot Workflow Designer with the workflow canvas and the properties panel.',
    },
    liveops: {
      title: 'Live Ops',
      alt: 'Original screenshot of the NodePilot Live Ops view for running and finished workflows.',
    },
    logs: {
      title: 'Support Log',
      alt: 'Original screenshot of the NodePilot Support Log with structured events.',
    },
    loading: 'Loading the original web UI …',
    unavailable: 'Product image unavailable',
    unavailableText: 'The product image could not be loaded.',
    openOnGithub: 'Open image on GitHub',
  },
  gallery: {
    title: 'ORIGINAL WEB UI',
    close: 'Close product view',
    tabsLabel: 'Choose a product view',
    openOriginal: 'Open original on GitHub',
    fullSize: 'Open at full size',
    source: 'Source file on GitHub',
    caption: 'Original screenshot · {title}',
  },
  download: {
    kicker: 'INSTALL NODEPILOT',
    close: 'Close download options',
    title: 'Where should NodePilot run?',
    lead: 'Try it on your own machine or run it as a service in your environment.',
    desktopTitle: 'On my Windows PC',
    desktopText: 'Desktop app for Windows 11 x64. Database and runtime included.',
    desktopLink: 'Go to downloads',
    serverTitle: 'On a Windows Server',
    serverText: 'Installed as a Windows service. Requirements and setup are covered in the docs.',
    serverLink: 'Installation guide',
    license: 'Free · Open source · Apache 2.0',
    selfBuild: 'Build it yourself?',
    sourceLink: 'Source code on GitHub',
  },
  blog: {
    kicker: 'NODEPILOT / BLOG',
    title: 'Notes from<br>development.',
    intro: 'Why certain decisions were made, how things work and what helps when automating.',
    filterLabel: 'Filter posts by category',
    filterAll: 'All posts',
    filterBackground: 'Background',
    filterPractice: 'In practice',
    searchPlaceholder: 'Search posts',
    searchLabel: 'Search blog posts',
    readMore: 'Read post',
    emptyTitle: 'No matching post.',
    emptyText: 'Try a different search term or category.',
    reset: 'Reset filters',
    foundOne: '1 post found.',
    foundMany: '{count} posts found.',
  },
  article: {
    back: 'All posts',
    meta: 'NodePilot · Project blog',
    toBlog: 'Back to the blog',
    toDocs: 'Go to the documentation',
  },
  articles: {
    'warum-nodepilot': {
      category: 'BACKGROUND',
      title: 'Why NodePilot exists.',
      indexTitle: 'Why NodePilot exists.',
      summary: 'From PowerShell scripts and SCOrch to a tool of its own.',
      teaser:
        'PowerShell solves many tasks. Coordinating them, running them and troubleshooting them takes more than a collection of individual scripts.',
      lead: 'PowerShell is not the problem. Losing track of what happens between the scripts is.',
      body: `<p>One script checks services, another copies files. A third queries a database. Each of them works on its own. But as soon as these tasks depend on each other, knowing where the files are is no longer enough.</p>
<p>Which step ran? What output did it produce? Why did the next step not start? And what has to change without affecting the rest of the workflow?</p>
<h2>The work between the scripts</h2>
<p>This is where NodePilot comes in. PowerShell remains the tool for the actual work on the systems. The workflow describes how the individual steps fit together: with conditions, error paths and parallel branches.</p>
<p>The goal is not to replace working scripts with a new language. It is to embed them in a workflow that you can design, run and trace afterwards.</p>
<h2>One workspace instead of separate views</h2>
<p>NodePilot combines the visual designer with execution history, debugging and Live Ops. Target systems are reached over WinRM; they need no additional NodePilot agent. The application itself runs in your own environment.</p>
<p>Existing automation should not get lost either. That is why importing SCOrch runbooks is part of the project. An import does not replace a review, but it takes the existing structure as the starting point – instead of an empty canvas.</p>
<h2>Open, limits included</h2>
<p>NodePilot is released under Apache 2.0. You can read the source code, run it yourself and change it. There is no split into a free interface and product features locked behind a paywall.</p>
<p>That said, it is no substitute for a support contract. With an open-source project, careful evaluation, tests with your own workflows and a look at the documentation are part of the deal.</p>
<div class="article-note">This post describes the motivation and approach of the project, not an operational guarantee.</div>
<p class="article-source">Technical background: <a href="https://github.com/Sev7eNup/NodePilot#why-nodepilot" target="_blank" rel="noopener noreferrer">NodePilot README</a>. The <a href="docs/#/en/" target="_blank" rel="noopener noreferrer">documentation</a> covers setup and operations.</p>`,
    },
    'scorch-import': {
      category: 'IN PRACTICE',
      title: 'Bring your runbooks. Don’t start over.',
      indexTitle: 'Bring your runbooks.<br>Don’t start over.',
      summary: 'What matters when importing from System Center Orchestrator.',
      teaser:
        'What becomes of activities, links and published data – and why the import report is part of the migration.',
      lead: 'Existing automation is more than a row of boxes. An import also has to carry over the links and the data between them.',
      body: `<p>A mature SCOrch runbook holds a lot of work: the right order, error handling, conditions and data passed from one activity to the next. That is exactly what nobody should have to rebuild by hand during a migration.</p>
<h2>Start from the existing export</h2>
<p>NodePilot reads SCOrch exports in the <code>.ois_export</code> format. You can start the import from the web UI or the CLI:</p>
<pre><code>np workflow import-scorch --file .\\runbooks.ois_export</code></pre>
<p>Supported activities are translated into matching NodePilot activities. Links, conditions, global variables and published data references are carried over as well. The folder structure can come along too.</p>
<h2>The import report is part of the result</h2>
<p>Not every activity and not every property translates without loss. An unsupported activity should therefore not simply disappear. NodePilot creates a disabled placeholder for it and lists the relevant details in the import report.</p>
<p>Incomplete target system settings, data references that cannot be translated and schedules that carry over only approximately need attention as well. The result is a starting point for a review, not a promise that every automation runs unchanged.</p>
<h2>Review, test, enable deliberately</h2>
<p>Imported workflows start out disabled. Credentials are not reconstructed from the encrypted SCOrch data. Before enabling a workflow, check the items named in the report, the target systems, permissions and error paths.</p>
<p>Then test it in a suitable test environment. Only when its behavior meets your requirements do you enable the workflow explicitly.</p>
<div class="article-note">The benefit is not that every migration decision gets automated. It is that you work with the actual runbook and a report you can follow.</div>
<p class="article-source">Background and current details: <a href="https://github.com/Sev7eNup/NodePilot#coming-from-system-center-orchestrator" target="_blank" rel="noopener noreferrer">SCOrch import in the NodePilot README</a>.</p>`,
    },
  },
  notFound: {
    title: 'No workflow here.',
    text: 'This page does not exist.',
    home: 'Back to the overview',
  },
  legal: {
    kicker: 'LEGAL',
    impressum: 'Legal notice',
    datenschutz: 'Privacy policy',
    germanOnly: 'The legal texts are only available in German.',
  },
} satisfies Messages
