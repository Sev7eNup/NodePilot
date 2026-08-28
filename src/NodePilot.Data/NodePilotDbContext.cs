using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NodePilot.Core.Models;

namespace NodePilot.Data;

public class NodePilotDbContext : DbContext
{
    public NodePilotDbContext(DbContextOptions<NodePilotDbContext> options) : base(options) { }

    public DbSet<Workflow> Workflows => Set<Workflow>();
    public DbSet<WorkflowExecution> WorkflowExecutions => Set<WorkflowExecution>();
    public DbSet<StepExecution> StepExecutions => Set<StepExecution>();
    public DbSet<ManagedMachine> ManagedMachines => Set<ManagedMachine>();
    public DbSet<Credential> Credentials => Set<Credential>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
    public DbSet<SupportEvent> SupportEvents => Set<SupportEvent>();
    public DbSet<User> Users => Set<User>();
    public DbSet<ExternalIdentity> ExternalIdentities => Set<ExternalIdentity>();
    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();
    public DbSet<DirectoryMembership> DirectoryMemberships => Set<DirectoryMembership>();
    public DbSet<ScimGroup> ScimGroups => Set<ScimGroup>();
    public DbSet<OidcLoginTicket> OidcLoginTickets => Set<OidcLoginTicket>();

    public DbSet<RevokedToken> RevokedTokens => Set<RevokedToken>();
    public DbSet<WorkflowVersion> WorkflowVersions => Set<WorkflowVersion>();
    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();
    public DbSet<GlobalVariable> GlobalVariables => Set<GlobalVariable>();
    public DbSet<GlobalVariableFolder> GlobalVariableFolders => Set<GlobalVariableFolder>();
    public DbSet<SystemHealthHeartbeat> SystemHealth => Set<SystemHealthHeartbeat>();
    public DbSet<WorkflowStats> WorkflowStats => Set<WorkflowStats>();
    public DbSet<ClusterLeader> ClusterLeaders => Set<ClusterLeader>();
    public DbSet<SharedWorkflowFolder> SharedWorkflowFolders => Set<SharedWorkflowFolder>();
    public DbSet<SharedFolderPermission> SharedFolderPermissions => Set<SharedFolderPermission>();
    public DbSet<MaintenanceWindow> MaintenanceWindows => Set<MaintenanceWindow>();
    public DbSet<MaintenanceWindowTarget> MaintenanceWindowTargets => Set<MaintenanceWindowTarget>();
    public DbSet<CustomActivityDefinition> CustomActivityDefinitions => Set<CustomActivityDefinition>();
    public DbSet<CustomActivityDefinitionVersion> CustomActivityDefinitionVersions => Set<CustomActivityDefinitionVersion>();
    public DbSet<NotificationRule> NotificationRules => Set<NotificationRule>();
    public DbSet<NotificationRoute> NotificationRoutes => Set<NotificationRoute>();
    public DbSet<NotificationRuleTarget> NotificationRuleTargets => Set<NotificationRuleTarget>();
    public DbSet<NotificationSuppressionState> NotificationSuppressionStates => Set<NotificationSuppressionState>();
    public DbSet<NotificationDeliveryAttempt> NotificationDeliveryAttempts => Set<NotificationDeliveryAttempt>();
    public DbSet<NotificationDispatcherState> NotificationDispatcherStates => Set<NotificationDispatcherState>();
    public DbSet<SystemAlertPolicyState> SystemAlertPolicyStates => Set<SystemAlertPolicyState>();
    public DbSet<SystemAlertSourceState> SystemAlertSourceStates => Set<SystemAlertSourceState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        const string sqlServerExactIdentityCollation = "Latin1_General_100_BIN2";
        var exactIdentityCollation = Database.IsSqlServer()
            ? sqlServerExactIdentityCollation
            : null;
        modelBuilder.Entity<SharedWorkflowFolder>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.Property(x => x.Path).HasMaxLength(800).IsRequired();
            // Sibling-unique name: a folder cannot have two children with the same display
            // name. Root is the only row with ParentFolderId NULL — there's exactly one of
            // those by application invariant (enforced via CreateAsync logic, not the DB).
            e.HasIndex(x => new { x.ParentFolderId, x.Name }).IsUnique();
            e.HasIndex(x => x.ParentFolderId);
            // Self-referencing parent FK with no automatic cascade — the move/delete
            // endpoints have to walk descendants explicitly, so a stray cascade here would
            // mask a coding bug rather than help.
            e.HasOne<SharedWorkflowFolder>()
                .WithMany()
                .HasForeignKey(x => x.ParentFolderId)
                .OnDelete(DeleteBehavior.Restrict);
            // Root folder seed: the migration's InsertData covers production (Migrate
            // path); HasData covers test paths that bypass migrations via EnsureCreated.
            // Both write to the same row, so a fresh DB created either way has the same
            // singleton Root with the well-known sentinel id.
            e.HasData(new
            {
                Id = SharedWorkflowFolder.RootFolderId,
                ParentFolderId = (Guid?)null,
                Name = "Root",
                Path = "/",
                Depth = 0,
                CreatedAt = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                CreatedByUserId = (Guid?)null,
            });
        });

        modelBuilder.Entity<SharedFolderPermission>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.PrincipalType).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.PrincipalAuthority).HasMaxLength(512).HasDefaultValue("").IsRequired();
            // PrincipalKey holds either a User-Guid-as-string (length 36) or an AD-Group-SID
            // (canonical max ~184 chars; size 256 for headroom). Required, no NULL allowed.
            e.Property(x => x.PrincipalKey).HasMaxLength(256).IsRequired();
            if (exactIdentityCollation is not null)
            {
                e.Property(x => x.PrincipalAuthority).UseCollation(exactIdentityCollation);
                e.Property(x => x.PrincipalKey).UseCollation(exactIdentityCollation);
            }
            // One grant per (folder, principal-type, principal-key). A principal can only hold
            // one role per folder; a re-grant updates the role rather than stacking rows.
            e.HasIndex(x => new { x.FolderId, x.PrincipalType, x.PrincipalAuthority, x.PrincipalKey })
                .IsUnique()
                .HasDatabaseName("UX_SharedFolderPermissions_Principal");
            e.HasIndex(x => x.FolderId);
            e.HasOne(x => x.Folder)
                .WithMany(f => f.Permissions)
                .HasForeignKey(x => x.FolderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Workflow>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.DefinitionJson).IsRequired();
            // RBAC folder membership — every workflow belongs to exactly one folder. Not
            // cascade: removing a folder is blocked while it still has workflows so the
            // move-out-first contract is enforced at the schema level.
            e.HasOne(x => x.Folder)
                .WithMany()
                .HasForeignKey(x => x.FolderId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.FolderId);
            // Deliberately no index on IsEnabled: the column is low-cardinality and the table
            // is small, so both providers would pick a sequential scan over the index anyway.
            // An index here would only add insert overhead with no read benefit.
            // Sparse index for "show me every workflow user X has open for editing".
            // Most rows have a null lock — sparse keeps the index small.
            e.HasIndex(x => x.CheckedOutByUserId);
            e.HasMany(x => x.Executions).WithOne(x => x.Workflow).HasForeignKey(x => x.WorkflowId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkflowExecution>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.TraceId).HasMaxLength(32);
            e.Property(x => x.SpanId).HasMaxLength(16);
            e.Property(x => x.OwnerNodeId).HasMaxLength(200);
            e.Property(x => x.CancelledBy).HasMaxLength(32);
            e.HasIndex(x => x.TraceId);
            // Composite + StartedAt-DESC + INCLUDE for the two hot read paths:
            //   1) WorkflowsController.GetAll: ROW_NUMBER OVER (PARTITION BY WorkflowId
            //      ORDER BY StartedAt DESC) for "last N executions per workflow".
            //   2) ExecutionsController.GetStepHealth/GetStepStats: filter by WorkflowId,
            //      newest-first, project Status + CompletedAt.
            // IsDescending avoids a backward scan for "ORDER BY StartedAt DESC"; INCLUDE avoids
            // the extra key lookup back to the heap on both SQL Server and Postgres v11+. The
            // annotations are set directly, not through `IncludeProperties()`, because that call
            // is ambiguous (CS0121) once both the SqlServer and Npgsql providers are registered.
            e.HasIndex(x => new { x.WorkflowId, x.StartedAt })
                .IsDescending(false, true)
                .HasAnnotation("SqlServer:Include", new[]
                {
                    nameof(WorkflowExecution.Status),
                    nameof(WorkflowExecution.CompletedAt),
                })
                .HasAnnotation("Npgsql:IndexInclude", new[]
                {
                    nameof(WorkflowExecution.Status),
                    nameof(WorkflowExecution.CompletedAt),
                });
            // Composite covering index for `GET /executions?activeOnly=true` (Status IN
            // (Running,Pending,Paused) ORDER BY StartedAt DESC LIMIT 500). Without it, SQL
            // Server would need a key lookup back to the heap for every row the live view loads.
            e.HasIndex(x => new { x.Status, x.StartedAt })
                .IsDescending(false, true)
                .HasAnnotation("SqlServer:Include", new[]
                {
                    nameof(WorkflowExecution.WorkflowId),
                    nameof(WorkflowExecution.CompletedAt),
                    nameof(WorkflowExecution.TriggeredBy),
                })
                .HasAnnotation("Npgsql:IndexInclude", new[]
                {
                    nameof(WorkflowExecution.WorkflowId),
                    nameof(WorkflowExecution.CompletedAt),
                    nameof(WorkflowExecution.TriggeredBy),
                });
            // Sub-workflow chain lookup: "give me every child this parent spawned" is the
            // hot query for the execution-detail UI; sparse index because most runs are
            // top-level and have no parent.
            e.HasIndex(x => x.ParentExecutionId);
            // Composite (StartedAt, Status) covers the retention sweep directly —
            // ExecutionRetentionService filters `WHERE StartedAt < cutoff AND Status IN
            // (Succeeded, Failed, Cancelled)` and sorts by StartedAt, so the composite serves
            // both the range and the status filter. WorkflowStatsRefresher and the dashboard
            // still use the left prefix (StartedAt) for their own queries.
            e.HasIndex(x => new { x.StartedAt, x.Status });
            // Keyset index for the notification event poller: ExecutionEventCollector filters
            // WHERE CompletedAt <= cutoff AND Status IN (terminal) and paginates by
            // (CompletedAt, Id). The indexes above only carry CompletedAt as an INCLUDE column,
            // which cannot serve that ORDER BY / keyset seek, so this index carries it as a key
            // column; Status stays an INCLUDE for the terminal-status filter.
            e.HasIndex(x => new { x.CompletedAt, x.Id })
                .HasAnnotation("SqlServer:Include", new[] { nameof(WorkflowExecution.Status) })
                .HasAnnotation("Npgsql:IndexInclude", new[] { nameof(WorkflowExecution.Status) });
            e.HasMany(x => x.Steps).WithOne(x => x.WorkflowExecution).HasForeignKey(x => x.WorkflowExecutionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StepExecution>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.StepId).HasMaxLength(100).IsRequired();
            e.Property(x => x.StepType).HasMaxLength(30);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            // OutputParametersJson is provider-agnostic large text — Postgres maps to text,
            // SqlServer to nvarchar(max). We deliberately do not set HasMaxLength so the
            // provider picks its own unbounded type.
            e.Property(x => x.OutputParametersJson);
            // Composite (WorkflowExecutionId, StartedAt): primary read path is GetSteps —
            // `WHERE WorkflowExecutionId = X ORDER BY StartedAt`. The composite delivers rows
            // in index order and its WorkflowExecutionId prefix still serves the FK lookup.
            e.HasIndex(x => new { x.WorkflowExecutionId, x.StartedAt });
            // Composite (WorkflowExecutionId, Status) for the WorkflowEngine re-run path —
            // `Where(s => s.WorkflowExecutionId == X && s.Status == Failed)`. Status alone is
            // low-selectivity and useless as an index without a leading FK filter, so the
            // composite is what actually serves this query.
            e.HasIndex(x => new { x.WorkflowExecutionId, x.Status });
        });

        modelBuilder.Entity<ManagedMachine>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Hostname).HasMaxLength(500).IsRequired();
            e.HasOne(x => x.DefaultCredential).WithMany().HasForeignKey(x => x.DefaultCredentialId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Credential>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Username).HasMaxLength(200).IsRequired();
            e.Property(x => x.EncryptedPassword).IsRequired();
        });

        // SupportEvents: a structured DB projection of the Serilog stream for entries tagged
        // SupportLog=true. Design principle: copy the AuditLog pattern (composite indexes with
        // IsDescending, no `type:` strings, MaxLength set on the model builder), but this is
        // deliberately NOT audit-grade — dropping an event when the channel is full is
        // acceptable, since the plain-text log file remains the fallback record.
        modelBuilder.Entity<SupportEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.EventType).HasMaxLength(60).IsRequired();
            e.Property(x => x.Message).HasMaxLength(8000).IsRequired();
            e.Property(x => x.WorkflowName).HasMaxLength(200);
            e.Property(x => x.ExecutionShort).HasMaxLength(8);
            e.Property(x => x.StepId).HasMaxLength(120);
            e.Property(x => x.StepLabel).HasMaxLength(200);
            e.Property(x => x.ActivityType).HasMaxLength(60);
            e.Property(x => x.UserName).HasMaxLength(200);
            e.Property(x => x.TraceId).HasMaxLength(32);
            e.Property(x => x.SpanId).HasMaxLength(16);
            e.Property(x => x.PropertiesJson).HasMaxLength(8000);
            // Cursor pagination (Timestamp DESC, Id DESC); this single-column Timestamp index
            // serves the unfiltered "newest first" scan without a follow-up sort.
            e.HasIndex(x => x.Timestamp).IsDescending();
            // EventType filter (e.g. "only USER_LOG", "only EXECUTION_FAILED") — composite with
            // Timestamp DESC so the filtered path doesn't need a separate key-lookup sort.
            e.HasIndex(x => new { x.EventType, x.Timestamp }).IsDescending(false, true);
            // Level filter ("WARN+" / "ERROR" — the top-of-funnel option in the UI filter
            // dropdown).
            e.HasIndex(x => new { x.Level, x.Timestamp }).IsDescending(false, true);
            // Per-execution lookup for "all events from this run" + trace drill-down.
            e.HasIndex(x => new { x.ExecutionId, x.Timestamp });
            // Workflow-name filter ("all events from the Daily-Report workflow") — the name is
            // frozen at write time, so it survives a later rename. Composite DESC, same pattern
            // as the AuditLog IpAddress index below.
            e.HasIndex(x => new { x.WorkflowName, x.Timestamp }).IsDescending(false, true);
        });

        modelBuilder.Entity<AuditLogEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Action).HasMaxLength(100).IsRequired();
            // Username is denormalized and frozen-at-write so the row stays interpretable
            // even if the user is later renamed or deleted.
            e.Property(x => x.Username).HasMaxLength(200);
            // IPv6 string form fits in 45 chars (max canonical length). A dedicated column,
            // not part of the Details JSON blob, so GDPR anonymization can run as a single
            // UPDATE and the "all activity from IP X" filter is an index seek.
            e.Property(x => x.IpAddress).HasMaxLength(45);
            e.HasIndex(x => x.Timestamp);
            // Per-resource + per-actor filter paths are the two hot queries ("show me every
            // change to this workflow" / "what did this user do"). Composite with Timestamp
            // so the index alone can serve the newest-first order without a follow-up sort.
            e.HasIndex(x => new { x.ResourceId, x.Timestamp });
            e.HasIndex(x => new { x.UserId, x.Timestamp });
            // Composite (Action, Timestamp DESC) for the admin filter path
            // `GET /api/audit?action=X` with OrderByDescending(Timestamp). With DESC ordering
            // baked into the index, it serves the newest-first order directly, with no
            // separate sort or key lookup.
            e.HasIndex(x => new { x.Action, x.Timestamp }).IsDescending(false, true);
            // IP-filter path: "show me every action from this source IP". Composite with
            // Timestamp so the same index also serves the newest-first order without a
            // follow-up sort. Used by SIEM forensics and GDPR anonymization sweeps.
            e.HasIndex(x => new { x.IpAddress, x.Timestamp }).IsDescending(false, true);
            // Username-filter path: "what did this person actually do" — auditors filter by
            // the frozen-at-write display name, not the UUID. Symmetric to the IP index;
            // without it the username filter would scan the full AuditLog.
            e.HasIndex(x => new { x.Username, x.Timestamp }).IsDescending(false, true);
        });

        modelBuilder.Entity<User>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Username).HasMaxLength(100).IsRequired();
            e.HasIndex(x => x.Username).IsUnique();
            // PasswordHash is now nullable (LDAP / Windows users authenticate externally).
            // The login path enforces the local-only invariant — a non-Local user with a
            // hash can still never log in via /api/auth/login.
            e.Property(x => x.PasswordHash);
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
            // Auth-provider stored as the enum name for greppability (`local` / `ldap` /
            // `windows`); 20 chars is comfortably above any realistic future provider name.
            e.Property(x => x.Provider).HasConversion<string>().HasMaxLength(20);
            // ExternalId: AD objectGUID is 36 chars canonical; 256 leaves headroom for
            // alternative directory schemes that might prefix with a domain.
            e.Property(x => x.ExternalId).HasMaxLength(256);
            // (Provider, ExternalId) is the lookup key during external-auth login. Non-unique
            // at the schema level — application code asserts uniqueness for non-null
            // ExternalId so we don't need provider-specific filtered-index syntax.
            e.HasIndex(x => new { x.Provider, x.ExternalId });
            e.Property(x => x.DirectorySyncStatus).HasMaxLength(32);
            e.Property(x => x.SecurityStamp).IsConcurrencyToken();
        });

        modelBuilder.Entity<ExternalIdentity>(e =>
        {
            e.HasKey(x => x.Id);
            // Authority accommodates OIDC issuer URIs; AD uses a short well-known URN.
            // Keep the composite unique key below SQL Server's 1,700-byte nonclustered
            // index limit (384 + 384 nvarchar characters = at most 1,536 bytes).
            e.Property(x => x.Authority).HasMaxLength(384).IsRequired();
            e.Property(x => x.Subject).HasMaxLength(384).IsRequired();
            if (exactIdentityCollation is not null)
            {
                e.Property(x => x.Authority).UseCollation(exactIdentityCollation);
                e.Property(x => x.Subject).UseCollation(exactIdentityCollation);
            }
            e.HasIndex(x => new { x.Authority, x.Subject }).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.User)
                .WithMany(x => x.ExternalIdentities)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuthSession>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.AuthenticationMethod).HasMaxLength(32).IsRequired();
            e.Property(x => x.CurrentJti).HasMaxLength(64).IsRequired();
            e.Property(x => x.RefreshGeneration).IsConcurrencyToken();
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.ExpiresAt);
            e.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DirectoryMembership>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Authority).HasMaxLength(384).IsRequired();
            e.Property(x => x.GroupKey).HasMaxLength(256);
            if (exactIdentityCollation is not null)
            {
                e.Property(x => x.Authority).UseCollation(exactIdentityCollation);
                e.Property(x => x.GroupKey).UseCollation(exactIdentityCollation);
            }
            // Nonclustered unique indexes allow up to 1,700 bytes on supported SQL Server
            // versions; this natural key is at most 1,296 bytes including UserId.
            e.HasIndex(x => new { x.UserId, x.Authority, x.GroupKey }).IsUnique();
            e.HasIndex(x => new { x.Authority, x.GroupKey });
            e.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ScimGroup>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Authority).HasMaxLength(384).IsRequired();
            e.Property(x => x.ExternalId).HasMaxLength(384).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(256).IsRequired();
            if (exactIdentityCollation is not null)
            {
                e.Property(x => x.Authority).UseCollation(exactIdentityCollation);
                e.Property(x => x.ExternalId).UseCollation(exactIdentityCollation);
            }
            e.HasIndex(x => new { x.Authority, x.ExternalId }).IsUnique();
            e.HasIndex(x => x.DisplayName);
        });

        modelBuilder.Entity<OidcLoginTicket>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.ProtectedPayload).IsRequired();
            e.HasIndex(x => x.ExpiresAt);
        });


        modelBuilder.Entity<RevokedToken>(e =>
        {
            e.HasKey(x => x.Jti);
            e.Property(x => x.Jti).HasMaxLength(64);
            e.Property(x => x.Reason).HasMaxLength(200);
            e.HasIndex(x => x.ExpiresAt);
        });

        modelBuilder.Entity<GlobalVariable>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Description).HasMaxLength(500);
            // Name stays globally unique — folders are organizational only and never
            // namespace a variable ({{globals.NAME}} resolves by bare Name).
            e.HasIndex(x => x.Name).IsUnique();
            // Organizational folder membership. Restrict (no cascade): a folder can only be
            // deleted once empty, enforced in the folder controller and at the schema level.
            // The DB-level default of Root lets a migration backfill existing variables onto
            // the seeded Root row (a bare Guid.Empty default would violate the FK).
            e.Property(x => x.FolderId).HasDefaultValue(GlobalVariableFolder.RootFolderId);
            e.HasOne<GlobalVariableFolder>()
                .WithMany()
                .HasForeignKey(x => x.FolderId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.FolderId);
        });

        modelBuilder.Entity<GlobalVariableFolder>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.Property(x => x.Path).HasMaxLength(800).IsRequired();
            // Sibling-unique name: a folder cannot have two children with the same display
            // name. Root is the only row with ParentFolderId NULL (one, by application invariant).
            e.HasIndex(x => new { x.ParentFolderId, x.Name }).IsUnique();
            e.HasIndex(x => x.ParentFolderId);
            // Self-referencing parent FK, no automatic cascade — move/delete walk descendants
            // explicitly, so a stray cascade would mask a bug rather than help.
            e.HasOne<GlobalVariableFolder>()
                .WithMany()
                .HasForeignKey(x => x.ParentFolderId)
                .OnDelete(DeleteBehavior.Restrict);
            // Root seed: migration InsertData covers Migrate(); HasData covers EnsureCreated
            // (tests). Both write the same singleton Root row with the sentinel id (…0002).
            e.HasData(new
            {
                Id = GlobalVariableFolder.RootFolderId,
                ParentFolderId = (Guid?)null,
                Name = "Root",
                Path = "/",
                Depth = 0,
                CreatedAt = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                CreatedByUserId = (Guid?)null,
            });
        });

        modelBuilder.Entity<MaintenanceWindow>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Description).HasMaxLength(500);
            // Enums stored as strings (provider-agnostic, readable in raw dumps).
            e.Property(x => x.Mode).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ScopeKind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Recurrence).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.DeferralPolicy).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.CronExpression).HasMaxLength(120);
            e.Property(x => x.TimeZoneId).HasMaxLength(100).IsRequired();
            e.Property(x => x.UpdatedBy).HasMaxLength(100);
            e.HasIndex(x => x.Name).IsUnique();
            // Snapshot reload scans enabled windows.
            e.HasIndex(x => x.IsEnabled);
            e.HasMany(x => x.Targets)
                .WithOne()
                .HasForeignKey(t => t.MaintenanceWindowId)
                // Cascade (NOT the repo-default Restrict): deleting a window cleans up its
                // targets; the target->folder/workflow side is a soft ref with no FK, so
                // deleting a folder/workflow is never dead-ended by a forgotten window.
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MaintenanceWindowTarget>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.TargetKind).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.MaintenanceWindowId, x.TargetKind, x.TargetId }).IsUnique();
            // Inverse lookup: "which windows affect this folder/workflow?"
            e.HasIndex(x => x.TargetId);
        });

        modelBuilder.Entity<NotificationRule>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.EventTypes).HasMaxLength(200).IsRequired();
            e.Property(x => x.FilterExpressionJson); // JSON text, no length cap
            e.Property(x => x.ScopeKind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.DedupKeyTemplate).HasMaxLength(300);
            e.Property(x => x.UpdatedBy).HasMaxLength(100);
            // System-alert policy fields (ADR 0008). Kind defaults to Custom so existing rows
            // backfill Custom.
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20).HasDefaultValue(Core.Enums.NotificationRuleKind.Custom);
            e.Property(x => x.SystemSourceId).HasMaxLength(100);
            e.Property(x => x.SystemPresetId).HasMaxLength(100);
            e.Property(x => x.SourceParametersJson); // JSON text, no length cap
            e.Property(x => x.SeverityOverride).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.IsEnabled); // dispatcher scans enabled rules
            e.HasIndex(x => new { x.Kind, x.IsEnabled }); // system evaluator scans enabled System policies
            e.HasMany(x => x.Routes).WithOne(r => r.Rule)
                .HasForeignKey(r => r.NotificationRuleId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Targets).WithOne()
                .HasForeignKey(t => t.NotificationRuleId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<NotificationRoute>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Channel).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Target).HasMaxLength(1000).IsRequired();
            e.Property(x => x.Secret).HasMaxLength(2000); // base64 of encrypted blob
            e.Property(x => x.ConditionExpressionJson); // JSON text, no length cap
            e.HasIndex(x => x.NotificationRuleId);
        });

        modelBuilder.Entity<NotificationRuleTarget>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.TargetKind).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.NotificationRuleId, x.TargetKind, x.TargetId }).IsUnique();
            e.HasIndex(x => x.TargetId);
        });

        modelBuilder.Entity<NotificationSuppressionState>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.DedupKey).HasMaxLength(300).IsRequired();
            // One suppression row per (rule, dedup key) — the cooldown lookup key.
            e.HasIndex(x => new { x.NotificationRuleId, x.DedupKey }).IsUnique();
        });

        modelBuilder.Entity<NotificationDeliveryAttempt>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.EventKey).HasMaxLength(300).IsRequired();
            e.Property(x => x.DedupKey).HasMaxLength(300).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.Property(x => x.Summary).HasMaxLength(1000);
            // Exactly-once guard: one attempt per (rule, route, occurrence).
            e.HasIndex(x => new { x.NotificationRuleId, x.NotificationRouteId, x.EventKey }).IsUnique();
            e.HasIndex(x => x.CreatedAt); // retention sweep
        });

        modelBuilder.Entity<NotificationDispatcherState>(e =>
        {
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<SystemAlertPolicyState>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.SourceId).HasMaxLength(100).IsRequired();
            e.Property(x => x.InstanceKey).HasMaxLength(300).IsRequired();
            // One row per (policy, source, instance) — the evaluator's transient match/episode
            // state.
            e.HasIndex(x => new { x.NotificationRuleId, x.SourceId, x.InstanceKey }).IsUnique();
            e.HasIndex(x => x.LastObservedAt); // stale-instance retention sweep
        });

        modelBuilder.Entity<SystemAlertSourceState>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.SourceId).HasMaxLength(100).IsRequired();
            e.Property(x => x.StateKey).HasMaxLength(200).IsRequired();
            e.Property(x => x.CursorJson); // JSON text, no length cap
            e.HasIndex(x => new { x.SourceId, x.StateKey }).IsUnique();
        });

        modelBuilder.Entity<SystemHealthHeartbeat>(e =>
        {
            e.HasKey(x => x.ServiceName);
            e.Property(x => x.ServiceName).HasMaxLength(100);
            e.Property(x => x.Status).HasMaxLength(500);
        });

        modelBuilder.Entity<WorkflowStats>(e =>
        {
            e.HasKey(x => x.WorkflowId);
            e.HasOne(x => x.Workflow)
                .WithMany()
                .HasForeignKey(x => x.WorkflowId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<IdempotencyKey>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Key).HasMaxLength(200).IsRequired();
            // Composite unique: external-trigger Key values already digest the authenticated
            // key principal + caller token; WorkflowId keeps the same caller token reusable
            // across runbooks. Other producers use their own domain-separated Key prefix.
            e.HasIndex(x => new { x.Key, x.WorkflowId }).IsUnique();
            e.HasIndex(x => x.ExpiresAt);
        });

        modelBuilder.Entity<WorkflowVersion>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.DefinitionJson).IsRequired();
            e.Property(x => x.CreatedBy).HasMaxLength(100);
            e.Property(x => x.ChangeNote).HasMaxLength(500);
            // A workflow owns its history — purging the live row purges its snapshots too.
            e.HasOne(x => x.Workflow)
                .WithMany()
                .HasForeignKey(x => x.WorkflowId)
                .OnDelete(DeleteBehavior.Cascade);
            // Primary lookup: list a workflow's history newest-first.
            e.HasIndex(x => new { x.WorkflowId, x.Version }).IsUnique();
        });

        modelBuilder.Entity<CustomActivityDefinition>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Key).HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.Icon).HasMaxLength(60).IsRequired();
            e.Property(x => x.Color).HasMaxLength(32);
            e.Property(x => x.ScriptTemplate).IsRequired();
            e.Property(x => x.Engine).HasMaxLength(20).IsRequired();
            e.Property(x => x.SuccessExitCodes).HasMaxLength(100);
            e.Property(x => x.InputParametersJson).IsRequired();
            e.Property(x => x.OutputParametersJson).IsRequired();
            e.Property(x => x.CreatedBy).HasMaxLength(100);
            e.Property(x => x.UpdatedBy).HasMaxLength(100);
            e.Property(x => x.ChangeNote).HasMaxLength(500);
            // Key lookup. Uniqueness among live (non-deleted) rows is enforced in the store's
            // CreateAsync rather than a filtered unique index, since a HasFilter literal would
            // bake provider-specific SQL into the shared migration set (Postgres and SQL Server
            // quote and represent booleans differently).
            e.HasIndex(x => x.Key);
            // Catalog/palette scan reads enabled, non-deleted rows.
            e.HasIndex(x => new { x.IsDeleted, x.IsEnabled });
        });

        modelBuilder.Entity<CustomActivityDefinitionVersion>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.Icon).HasMaxLength(60).IsRequired();
            e.Property(x => x.Color).HasMaxLength(32);
            e.Property(x => x.ScriptTemplate).IsRequired();
            e.Property(x => x.Engine).HasMaxLength(20).IsRequired();
            e.Property(x => x.SuccessExitCodes).HasMaxLength(100);
            e.Property(x => x.InputParametersJson).IsRequired();
            e.Property(x => x.OutputParametersJson).IsRequired();
            e.Property(x => x.CreatedBy).HasMaxLength(100);
            e.Property(x => x.ChangeNote).HasMaxLength(500);
            // A definition owns its history — tombstones keep rows, hard-delete (admin maintenance)
            // cascades.
            e.HasOne(x => x.Definition)
                .WithMany()
                .HasForeignKey(x => x.DefinitionId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.DefinitionId, x.Version }).IsUnique();
        });

        modelBuilder.Entity<ClusterLeader>(e =>
        {
            e.HasKey(x => x.Resource);
            e.Property(x => x.Resource).HasMaxLength(50);
            e.Property(x => x.OwnerNodeId).HasMaxLength(200).IsRequired();
        });

        // Some providers return DateTime with DateTimeKind.Unspecified, causing
        // System.Text.Json to omit the 'Z' suffix — browsers then mis-parse UTC timestamps
        // as local time. This global converter tags every DateTime value as UTC on read so
        // the JSON wire format always carries the 'Z' designator.
        var utcConverter = new ValueConverter<DateTime, DateTime>(
            v => v,
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        var utcNullableConverter = new ValueConverter<DateTime?, DateTime?>(
            v => v,
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : null);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTime) && property.GetValueConverter() is null)
                    property.SetValueConverter(utcConverter);
                else if (property.ClrType == typeof(DateTime?) && property.GetValueConverter() is null)
                    property.SetValueConverter(utcNullableConverter);
            }
        }
    }
}
