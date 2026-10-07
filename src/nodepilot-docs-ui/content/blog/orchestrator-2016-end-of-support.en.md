# System Center 2016 Orchestrator: planning for the end of support on January 11, 2027

Extended support for System Center 2016 Orchestrator ends on January 11, 2027. After that date, Microsoft publishes no more security updates for this version, and neither free nor paid support is available. Your runbooks will not suddenly run worse on that day. They will, however, run on a platform where newly discovered vulnerabilities are no longer fixed. [Microsoft Lifecycle: System Center 2016 Orchestrator](https://learn.microsoft.com/en-us/lifecycle/products/system-center-2016-orchestrator).

Only version 2016 is affected. System Center 2019 Orchestrator is supported until April 9, 2029, version 2022 until April 13, 2032 and version 2025 until January 9, 2035. If you run a newer version, you have time, but the questions below still apply.

## Three questions before any decision

Whether an upgrade or a different tool makes sense cannot be answered without an inventory. Three questions reveal the actual scope:

- **Which runbooks actually run?** Over the years, runbooks pile up that nobody starts anymore. The execution history shows which processes run regularly and which only sit in the folder tree.
- **Which Integration Packs are in use?** Standard activities can usually be replaced. An Integration Pack for a third-party system, on the other hand, often decides which path is possible at all.
- **Under which accounts and against which targets?** Service accounts, stored credentials and firewall rules do not appear in the runbook diagram, but they decide every migration.

The result is a prioritized list: runbooks that write to production systems deserve more review than a read-only status check.

## The possible paths

**Upgrade to System Center 2025 Orchestrator.** Microsoft describes upgrades only from the previous version: [2016 (Update Rollup 6 or later) to 2019, 2019 to 2022, and 2022 to 2025](https://learn.microsoft.com/en-us/system-center/orchestrator/upgrade-orch?view=sc-orch-2025). A 2016 environment therefore needs three steps. Each one uninstalls and reinstalls the Orchestrator components and, where necessary, requires a newer operating system or other software. The alternative is a fresh installation of 2022 or 2025 next to the old one, with the runbooks moved over by export and import in the Runbook Designer. Integration Packs need a check on either route: Orchestrator 2022 and later are 64-bit applications, and [Kelverion reports](https://www.kelverion.com/blog/migrating-from-orchestrator-2016-or-2019-to-orchestrator-2022) that packs written for 2019 and earlier are not compatible. System Center licensing and operations remain as they are.

**Azure Automation.** Microsoft offers a migration toolkit that its own guide labels as beta. It converts runbooks into graphical runbooks. Monitor activities, variables and connections are not carried over and have to be recreated or replaced. To reach on-premises systems, the runbooks need a Hybrid Runbook Worker. [Microsoft: Migrate from Orchestrator to Azure Automation](https://learn.microsoft.com/en-us/azure/automation/automation-orchestrator-migration).

**Service Management Automation (SMA).** SMA runs runbooks in your own datacenter but does not support graphical runbooks. According to the same Microsoft guide, Orchestrator runbooks have to be rewritten in PowerShell by hand.

**A different workflow orchestrator.** Tools such as NodePilot stay inside your network and work agentless over WinRM. NodePilot imports exports in `.ois_export` format, maps supported activities and leaves unsupported ones in place as disabled placeholders. Stored credentials are not carried over, and imported workflows start out disabled. The article [Importing SCOrch runbooks and reviewing the result](blog/scorch-import/) shows the details.

## A migration path in five steps

Whatever the target, a step-by-step switch has proven reliable:

1. **Build an inventory:** list active runbooks, Integration Packs, accounts and schedules.
2. **Start with a read-only runbook:** for example a daily status check that changes nothing.
3. **Write down expectations first:** output on success, behaviour on an error, behaviour when a target is unreachable.
4. **Run both in a controlled way:** the new workflow first runs manually or against test targets. Two active schedules for the same job would run it twice.
5. **Switch only after sign-off:** enable the schedule in the new tool and disable the runbook in SCOrch, but do not delete it yet.

This order moves a solid share of runbooks before the deadline without moving everything at once. Whatever is not ready stays on the old platform, and its risk is then carried knowingly.

For support dates, options and a comparison with NodePilot, see [Evaluate an open-source alternative to System Center Orchestrator](scorch-alternative/).
