using NodePilot.Cli.Commands.Audit;
using NodePilot.Cli.Commands.Auth;
using NodePilot.Cli.Commands.Backup;
using NodePilot.Cli.Commands.Config;
using NodePilot.Cli.Commands.Credentials;
using NodePilot.Cli.Commands.Db;
using NodePilot.Cli.Commands.Exec;
using NodePilot.Cli.Commands.Globals;
using NodePilot.Cli.Commands.Machines;
using NodePilot.Cli.Commands.Alerting;
using NodePilot.Cli.Commands.Maintenance;
using NodePilot.Cli.Commands.Secrets;
using NodePilot.Cli.Commands.SharedFolders;
using NodePilot.Cli.Commands.Settings;
using NodePilot.Cli.Commands.Stats;
using NodePilot.Cli.Commands.System;
using NodePilot.Cli.Commands.Users;
using NodePilot.Cli.Commands.Workflow;
using Spectre.Console.Cli;

namespace NodePilot.Cli;

/// <summary>
/// Single source of truth for the <c>np</c> command tree. Both <c>Program.cs</c>
/// (production CLI host) and the test harness (<c>CommandTestHarness</c>) call into
/// this method so every command available in production is also reachable from tests
/// — and so a forgotten registration in the harness can no longer make new commands
/// look "tested" while really only their API-client wrapper was covered.
/// </summary>
public static class CommandRegistration
{
    public static void Register(IConfigurator config)
    {
        // -- auth ----------------------------------------------------------------
        config.AddBranch("auth", auth =>
        {
            auth.SetDescription("Authenticate against a NodePilot server.");
            auth.AddCommand<LoginCommand>("login").WithDescription("Log in and store an encrypted session token.");
            auth.AddCommand<LogoutCommand>("logout").WithDescription("Revoke the current session and forget the local token.");
            auth.AddCommand<WhoamiCommand>("whoami").WithDescription("Show the active session.");
            auth.AddCommand<AuthMethodsCommand>("methods").WithDescription("Discover which auth methods (Local/LDAP/Windows-SSO) the server has enabled.");
        });

        // -- backup --------------------------------------------------------------
        config.AddBranch("backup", b =>
        {
            b.SetDescription("System-configuration backup & restore (Admin).");
            b.AddCommand<BackupManifestCommand>("manifest").WithDescription("Show what a backup would contain (section counts).");
            b.AddCommand<BackupExportCommand>("export").WithDescription("Export a sealed .npbackup archive (passphrase via env/file/prompt).");
            b.AddCommand<BackupPreviewCommand>("preview").WithDescription("Dry-run a restore: per-section new/conflict counts.");
            b.AddCommand<BackupRestoreCommand>("restore").WithDescription("Restore a .npbackup archive (--policy, --yes).");
        });

        // -- workflow ------------------------------------------------------------
        config.AddBranch("workflow", wf =>
        {
            wf.SetDescription("Manage workflows.");
            wf.AddCommand<WorkflowListCommand>("list").WithDescription("List all workflows.");
            wf.AddCommand<WorkflowGetCommand>("get").WithDescription("Show one workflow in detail.");
            wf.AddCommand<WorkflowRunCommand>("run").WithDescription("Execute a workflow (optionally wait/follow).");
            wf.AddCommand<WorkflowLockCommand>("lock").WithDescription("Take the edit lock and disable the workflow.");
            wf.AddCommand<WorkflowUnlockCommand>("unlock").WithDescription("Release the edit lock (workflow stays disabled).");
            wf.AddCommand<WorkflowPublishCommand>("publish").WithDescription("Save + enable + unlock atomically.");
            wf.AddCommand<WorkflowEnableCommand>("enable").WithDescription("Enable trigger evaluation.");
            wf.AddCommand<WorkflowDisableCommand>("disable").WithDescription("Disable a workflow (kill switch).");
            wf.AddCommand<WorkflowCancelAllCommand>("cancel-all").WithDescription("Cancel every running execution of a workflow.");
            wf.AddCommand<WorkflowDuplicateCommand>("duplicate").WithDescription("Create a copy of a workflow.");
            wf.AddCommand<WorkflowDeleteCommand>("delete").WithDescription("Permanently delete a workflow (Admin only).");
            wf.AddCommand<WorkflowExportCommand>("export").WithDescription("Export one or all workflows.");
            wf.AddCommand<WorkflowImportCommand>("import").WithDescription("Import an envelope JSON.");
            wf.AddCommand<WorkflowVersionsCommand>("versions").WithDescription("Show the version history of a workflow.");
            wf.AddCommand<WorkflowVersionGetCommand>("version").WithDescription("Show one specific workflow version (full DefinitionJson).");
            wf.AddCommand<WorkflowRollbackCommand>("rollback").WithDescription("Roll back to a previous version.");
            wf.AddCommand<WorkflowForceUnlockCommand>("force-unlock").WithDescription("Admin: break a foreign edit-lock.");
            wf.AddCommand<WorkflowImportScorchCommand>("import-scorch").WithDescription("Import an SCOrch .ois_export XML file.");
            wf.AddCommand<WorkflowStatsCommand>("stats").WithDescription("Per-step duration + failure-rate stats over a window.");
            wf.AddCommand<WorkflowContractCommand>("contract").WithDescription("Show the calling contract (inputs + outputs) for a workflow.");
            wf.AddCommand<WorkflowCoverageCommand>("coverage").WithDescription("Per-node coverage stats — what logic actually ran in the window.");
            wf.AddCommand<WorkflowTriggerCommand>("trigger").WithDescription("Fire a workflow via the API-key-gated external trigger surface.");
            wf.AddCommand<WorkflowStepTestCommand>("step-test").WithDescription("Run a single step in isolation (mock variables, optional config override).");
            wf.AddCommand<WorkflowStepTestContextCommand>("step-test-context").WithDescription("Inspect / pick the upstream-variable context for a step.");
            wf.AddCommand<WorkflowMoveFolderCommand>("move-folder").WithDescription("Move a workflow into a different shared folder.");
        });

        // -- exec ----------------------------------------------------------------
        config.AddBranch("exec", e =>
        {
            e.SetDescription("Inspect and steer executions.");
            e.AddCommand<ExecListCommand>("list").WithDescription("List recent executions.");
            e.AddCommand<ExecGetCommand>("get").WithDescription("Show one execution in detail.");
            e.AddCommand<ExecStepsCommand>("steps").WithDescription("Show step rows of one execution.");
            e.AddCommand<ExecCancelCommand>("cancel").WithDescription("Cancel a running execution.");
            e.AddCommand<ExecRetryCommand>("retry").WithDescription("Re-run with the original parameters.");
            e.AddCommand<ExecWatchCommand>("watch").WithDescription("Stream live step events (SignalR, polling fallback).");
            e.AddCommand<ExecResumeCommand>("resume").WithDescription("Resume a debug-paused step (continue|stepOver|stop).");
            e.AddCommand<ExecPausedStepsCommand>("paused-steps").WithDescription("List currently paused step ids of an execution.");
        });

        // -- audit ---------------------------------------------------------------
        config.AddBranch("audit", a =>
        {
            a.SetDescription("Query the audit log (Admin only).");
            a.AddCommand<AuditListCommand>("list").WithDescription("List audit entries with optional filters.");
        });

        // -- system --------------------------------------------------------------
        config.AddCommand<HealthCommand>("health").WithDescription("Check live + ready endpoints.");
        config.AddBranch("cron", c =>
        {
            c.SetDescription("Cron utilities.");
            c.AddCommand<CronNextCommand>("next").WithDescription("Preview next fire times for a cron expression.");
        });

        // -- machine -------------------------------------------------------------
        config.AddBranch("machine", m =>
        {
            m.SetDescription("Manage WinRM target machines.");
            m.AddCommand<MachineListCommand>("list").WithDescription("List all managed machines.");
            m.AddCommand<MachineGetCommand>("get").WithDescription("Show one machine in detail.");
            m.AddCommand<MachineCreateCommand>("create").WithDescription("Register a new machine.");
            m.AddCommand<MachineUpdateCommand>("update").WithDescription("Update a machine (only flags you pass are changed).");
            m.AddCommand<MachineDeleteCommand>("delete").WithDescription("Delete a machine (Admin only).");
            m.AddCommand<MachineTestCommand>("test").WithDescription("Probe WinRM connectivity (live).");
        });

        // -- credential ----------------------------------------------------------
        config.AddBranch("credential", c =>
        {
            c.SetDescription("Manage stored service-account credentials (DPAPI-encrypted).");
            c.AddCommand<CredentialListCommand>("list").WithDescription("List all credentials.");
            c.AddCommand<CredentialGetCommand>("get").WithDescription("Show one credential (no password material).");
            c.AddCommand<CredentialCreateCommand>("create").WithDescription("Create a new credential.");
            c.AddCommand<CredentialUpdateCommand>("update").WithDescription("Rename, rotate password, or change domain.");
            c.AddCommand<CredentialDeleteCommand>("delete").WithDescription("Delete a credential (Admin only).");
        });

        // -- db ------------------------------------------------------------------
        config.AddBranch("db", db =>
        {
            db.SetDescription("Admin-only database introspection and ad-hoc SQL.");
            db.AddCommand<DbInfoCommand>("info").WithDescription("Show active provider and query-console capabilities.");
            db.AddCommand<DbQueryCommand>("query").WithDescription("Execute a SQL statement (read-mode by default; --write opts in).");
        });

        // -- globals -------------------------------------------------------------
        config.AddBranch("globals", g =>
        {
            g.SetDescription("Manage global variables ({{globals.NAME}}).");
            g.AddCommand<GlobalsListCommand>("list").WithDescription("List all global variables (secrets are masked).");
            g.AddCommand<GlobalsCreateCommand>("create").WithDescription("Create a new global variable (Admin only).");
            g.AddCommand<GlobalsUpdateCommand>("update").WithDescription("Update a global variable (Admin only).");
            g.AddCommand<GlobalsDeleteCommand>("delete").WithDescription("Delete a global variable (Admin only).");
            g.AddCommand<GlobalsExportCommand>("export").WithDescription("Export all globals as JSON (secrets shown as ***).");
            g.AddCommand<GlobalsImportCommand>("import").WithDescription("Bulk-import globals from JSON file (Admin only).");
            g.AddCommand<GlobalsMoveVariableCommand>("move-folder").WithDescription("Move a variable into a folder (Admin only).");
            g.AddBranch("folder", f =>
            {
                f.SetDescription("Organize global variables into a folder tree.");
                f.AddCommand<GlobalsFolderListCommand>("list").WithDescription("List all folders (path + variable counts).");
                f.AddCommand<GlobalsFolderCreateCommand>("create").WithDescription("Create a folder (Admin only).");
                f.AddCommand<GlobalsFolderRenameCommand>("rename").WithDescription("Rename a folder (Admin only).");
                f.AddCommand<GlobalsFolderMoveCommand>("move").WithDescription("Reparent a folder (Admin only).");
                f.AddCommand<GlobalsFolderDeleteCommand>("delete").WithDescription("Delete an empty folder (Admin only).");
            });
        });

        // -- maintenance ---------------------------------------------------------
        config.AddBranch("maintenance", m =>
        {
            m.SetDescription("Manage maintenance windows (gate when workflows may run).");
            m.AddCommand<MaintenanceListCommand>("list").WithDescription("List all maintenance windows.");
            m.AddCommand<MaintenanceGetCommand>("get").WithDescription("Show one maintenance window.");
            m.AddCommand<MaintenanceCreateCommand>("create").WithDescription("Create a maintenance window (Admin only).");
            m.AddCommand<MaintenanceUpdateCommand>("update").WithDescription("Update a maintenance window (Admin only).");
            m.AddCommand<MaintenanceDeleteCommand>("delete").WithDescription("Delete a maintenance window (Admin only).");
        });

        // -- alerting ------------------------------------------------------------
        config.AddBranch("alerting", a =>
        {
            a.SetDescription("Manage alerting rules (notify on matching events).");
            a.AddCommand<AlertingListCommand>("list").WithDescription("List all alerting rules.");
            a.AddCommand<AlertingGetCommand>("get").WithDescription("Show one alerting rule.");
            a.AddCommand<AlertingCreateCommand>("create").WithDescription("Create an alerting rule (Admin only).");
            a.AddCommand<AlertingUpdateCommand>("update").WithDescription("Update an alerting rule (Admin only).");
            a.AddCommand<AlertingDeleteCommand>("delete").WithDescription("Delete an alerting rule (Admin only).");
            a.AddCommand<AlertingTestFireCommand>("test-fire").WithDescription("Send a test notification through a rule's routes (Admin only).");
            a.AddCommand<AlertingDeliveriesCommand>("deliveries").WithDescription("Show the recent delivery ledger (filter by --rule / --status).");
        });

        // -- system-alert (built-in infrastructure/service-health alert policies, ADR 0008) --
        config.AddBranch("system-alert", sa =>
        {
            sa.SetDescription("Manage system-alert policies (alert on infrastructure/service signals).");
            sa.AddCommand<SystemAlertCatalogCommand>("catalog").WithDescription("List the available system-alert sources (catalog).");
            sa.AddCommand<SystemAlertListCommand>("list").WithDescription("List all system-alert policies.");
            sa.AddCommand<SystemAlertGetCommand>("get").WithDescription("Show one system-alert policy.");
            sa.AddCommand<SystemAlertCreateCommand>("create").WithDescription("Create a policy from a SaveSystemAlertPolicyRequest JSON file (Admin only).");
            sa.AddCommand<SystemAlertUpdateCommand>("update").WithDescription("Update a policy from a SaveSystemAlertPolicyRequest JSON file (Admin only).");
            sa.AddCommand<SystemAlertEnableCommand>("enable").WithDescription("Enable a system-alert policy (Admin only).");
            sa.AddCommand<SystemAlertDisableCommand>("disable").WithDescription("Disable a system-alert policy (Admin only).");
            sa.AddCommand<SystemAlertDeleteCommand>("delete").WithDescription("Delete a system-alert policy (Admin only).");
            sa.AddCommand<SystemAlertTestFireCommand>("test-fire").WithDescription("Send a test notification through a policy's routes (Admin only).");
        });

        // -- user ----------------------------------------------------------------
        config.AddBranch("user", u =>
        {
            u.SetDescription("Manage NodePilot users (Admin only).");
            u.AddCommand<UserListCommand>("list").WithDescription("List all users.");
            u.AddCommand<UserCreateCommand>("create").WithDescription("Create a new user.");
            u.AddCommand<UserUpdateCommand>("update").WithDescription("Update role / active flag / password.");
            u.AddCommand<UserDeleteCommand>("delete").WithDescription("Delete a user.");
        });

        // -- stats / observability ----------------------------------------------
        config.AddCommand<DashboardCommand>("dashboard").WithDescription("Show the dashboard summary.");
        config.AddBranch("operations", o =>
        {
            o.SetDescription("Live-ops / NOC view.");
            o.AddCommand<NodePilot.Cli.Commands.Operations.OperationsGraphCommand>("graph")
                .WithDescription("Workflow call-graph + currently-running executions (RBAC-scoped).");
        });
        config.AddBranch("observability", o =>
        {
            o.SetDescription("Observability / Prometheus surface.");
            o.AddCommand<ObservabilitySummaryCommand>("summary").WithDescription("Pre-composed telemetry summary panels.");
            o.AddCommand<ObservabilityQueryCommand>("query").WithDescription("Ad-hoc PromQL instant query (Admin/Operator).");
            o.AddCommand<ObservabilityQueryRangeCommand>("query-range").WithDescription("Ad-hoc PromQL range query (Admin/Operator).");
        });

        // -- settings (Admin-only runtime config) -------------------------------
        config.AddBranch("settings", st =>
        {
            st.SetDescription("Inspect and edit runtime configuration (Admin only — /api/admin/settings).");
            st.AddCommand<SettingsStatusCommand>("status").WithDescription("Show overrides path + restart-required state.");
            st.AddCommand<SettingsSystemInfoCommand>("system-info").WithDescription("Read-only system info (DB provider, cluster, JWT issuer, ...).");
            st.AddCommand<SettingsGetCommand>("get").WithDescription("GET the full snapshot or one section (use --etag-only for chained automation).");
            st.AddCommand<SettingsPutCommand>("put").WithDescription("PUT one section from a JSON file with optimistic-concurrency --etag.");
            st.AddBranch("test", t =>
            {
                t.SetDescription("Probe SMTP / LLM connectivity from a candidate section payload.");
                t.AddCommand<SettingsTestSmtpCommand>("smtp").WithDescription("Probe SMTP (body shape: { settings: SmtpSettingsDto, toAddress?: string }).");
                t.AddCommand<SettingsTestLlmCommand>("llm").WithDescription("Probe LLM (body shape: { settings: LlmSettingsDto }).");
            });
        });

        // -- secrets (rotation / re-encrypt sweep) ------------------------------
        config.AddBranch("secrets", s =>
        {
            s.SetDescription("Operate on the secret-protector layer (Admin only).");
            s.AddCommand<SecretsReencryptCommand>("reencrypt").WithDescription("Bulk re-encrypt every credential + secret-flagged global variable.");
        });

        // -- shared-folder (RBAC org tree) --------------------------------------
        config.AddBranch("shared-folder", sf =>
        {
            sf.SetDescription("Manage the org-level shared workflow folder tree (RBAC).");
            sf.AddCommand<SharedFolderListCommand>("list").WithDescription("List the visible folder tree + caller capabilities.");
            sf.AddCommand<SharedFolderCreateCommand>("create").WithDescription("Create a folder.");
            sf.AddCommand<SharedFolderRenameCommand>("rename").WithDescription("Rename a folder.");
            sf.AddCommand<SharedFolderMoveCommand>("move").WithDescription("Move a folder under a new parent (or to Root with --to-root).");
            sf.AddCommand<SharedFolderDeleteCommand>("delete").WithDescription("Delete an empty folder.");
            sf.AddCommand<SharedFolderPermissionsListCommand>("permissions").WithDescription("List permissions on a folder (FolderAdmin only).");
            sf.AddCommand<SharedFolderGrantCommand>("grant").WithDescription("Grant or update a folder permission.");
            sf.AddCommand<SharedFolderRevokeCommand>("revoke").WithDescription("Revoke a permission by id.");
        });

        // -- config (CLI-side configuration, not server-side) -------------------
        config.AddBranch("config", c =>
        {
            c.SetDescription("View / modify CLI configuration.");
            c.AddCommand<ConfigGetCommand>("get").WithDescription("Show the resolved config and profiles.");
            c.AddCommand<ConfigSetCommand>("set").WithDescription("Set a config key (server, default-profile).");
        });
    }
}
