# Reddit draft

Two things decide whether this lands. The SCOrch import is the only thing NodePilot
does that n8n, Rundeck, Jenkins and a pile of PowerShell scripts cannot, so it leads.
And naming the weaknesses yourself takes the sting out of the first critical reply —
do not cut that section to save space; cut features instead.

## Title — pick one

- **r/sysadmin:** SCOrch is supported to 2035 — but authoring still means the desktop designer. So I built an open-source alternative.
- **r/PowerShell, r/SCCM, general:** NodePilot: agentless workflow orchestration for Windows that imports your SCOrch runbooks

Avoid "I'm building" — it reads as unfinished, which undersells 30 releases behind a
coverage gate.

## Body

I'm the author. Apache-2.0, no paid tier, nothing to sell.

I've worked with System Center Orchestrator for years in a Windows shop, and I want to be
precise about the complaint, because "SCOrch is dead" gets repeated a lot and it isn't
true. System Center 2025 Orchestrator shipped in November 2024 — mainstream support to
2030, extended to 2035. If you run it, you are not on a clock.

What bothers me is narrower. The web console added in 2022 runs and monitors runbooks, but
it cannot build one: authoring still means the desktop Runbook Designer on a machine with
the client installed. And once a runbook exists there is no version history, no diff
between two states and no rollback.

So I built the thing I wanted. Same agentless WinRM model, but the designer, the step
debugger and the version history live in a browser. PowerShell, REST, SQL, WMI, registry,
file, folder and service activities; schedule, webhook, file-watcher and event-log
triggers; execution history and a live operations view.

**The part I'd most like tested:** it imports SCOrch runbooks directly from `.ois_export`
XML — activities, links, conditions, global variables, and Published Data references
rewritten into its own data bus. Anything it cannot map becomes a *disabled* placeholder
carrying the original type name and its full property list, and the import report names
every lossy translation instead of quietly guessing. That takes evaluating this from
"rebuild everything" down to about ten minutes.

The 84-second tour below starts with a SCOrch `.ois_export` import and shows a health-check
workflow with parallel branches, step timings and output, live operations, logs, the AI
chat, and the identity settings preview.
Fictional demo data and a scripted chat response throughout — it's a product tour, not a
benchmark.

**Where it falls short, so you don't have to find out yourself:**

- Windows-only by design — PowerShell remoting over WinRM, DPAPI for credentials. No Linux, no containers, and that isn't on the roadmap.
- Single maintainer. You get the source, not a support contract.
- The installer is self-signed, so SmartScreen warns on first launch. Checksums and the signer thumbprint ship with each release, but the prompt is there.
- Young project. Expect rough edges, and tell me about them.

Source: https://github.com/Sev7eNup/NodePilot — Apache-2.0.

If you still run SCOrch: export a runbook, import it, and tell me what the report says it
couldn't translate. That is the single most useful thing anyone could send me right now.

And the open question I'd genuinely like answered — for anyone on SCOrch, PowerShell
scripts, Ansible, Rundeck or n8n for Windows work: what would actually have to be true for
you to move a production runbook onto something new?

# GitHub description

An 84-second tour of NodePilot, an agentless workflow orchestrator for Windows and an open
replacement for System Center Orchestrator: visual designer, PowerShell and service
activities, File Copy, LLM queries, execution history, Live-Ops, logs, global AI Chat and
the identity settings preview. It also imports SCOrch `.ois_export` runbooks directly.
Recorded against the real frontend with fictional demo data and a scripted chat response.

# Files

- `NodePilot-Product-Tour.mp4`: full tour, 84.1 seconds, 2560 × 1440, H.264, 30 fps, silent.
- `NodePilot-Product-Tour-1080p.mp4`: 1920 × 1080 sharing copy.
- `NodePilot-Preview.gif`: approximately 20-second looping preview, 960 × 540.
- `NodePilot-Poster.png`: still preview of the workflow designer.

# Before posting

- **Upload the 1080p file to Reddit.** Reddit re-encodes uploads and serves v.redd.it at
  1080p at most, so the 1440p master only gets downscaled for no visible gain. Keep the
  master for YouTube or the docs site.
- **Read each subreddit's rules first** — they change, and a removed post costs the channel
  for months.
- Post to one subreddit at a time, never several the same day.
- Be in the comments for the first few hours. That matters more than the post text.

Nothing has been uploaded or posted. Review the wording before publishing.
