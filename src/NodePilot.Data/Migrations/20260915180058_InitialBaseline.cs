using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodePilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var identityCollation = migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.SqlServer"
                ? "Latin1_General_100_BIN2" : null;

            migrationBuilder.CreateTable(
                name: "AuditLog",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    Timestamp = table.Column<DateTime>(nullable: false),
                    UserId = table.Column<Guid>(nullable: true),
                    Username = table.Column<string>(maxLength: 200, nullable: true),
                    Action = table.Column<string>(maxLength: 100, nullable: false),
                    ResourceType = table.Column<string>(nullable: true),
                    ResourceId = table.Column<Guid>(nullable: true),
                    Details = table.Column<string>(nullable: true),
                    IpAddress = table.Column<string>(maxLength: 45, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClusterLeaders",
                columns: table => new
                {
                    Resource = table.Column<string>(maxLength: 50, nullable: false),
                    OwnerNodeId = table.Column<string>(maxLength: 200, nullable: false),
                    AcquiredAt = table.Column<DateTime>(nullable: false),
                    ExpiresAt = table.Column<DateTime>(nullable: false),
                    LastRenewedAt = table.Column<DateTime>(nullable: false),
                    LeaseEpoch = table.Column<long>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClusterLeaders", x => x.Resource);
                });

            migrationBuilder.CreateTable(
                name: "Credentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    Name = table.Column<string>(maxLength: 200, nullable: false),
                    Username = table.Column<string>(maxLength: 200, nullable: false),
                    EncryptedPassword = table.Column<byte[]>(nullable: false),
                    Domain = table.Column<string>(nullable: true),
                    ExpiresAt = table.Column<DateTime>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Credentials", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CustomActivityDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    Key = table.Column<string>(maxLength: 64, nullable: false),
                    Name = table.Column<string>(maxLength: 200, nullable: false),
                    Description = table.Column<string>(maxLength: 500, nullable: true),
                    Icon = table.Column<string>(maxLength: 60, nullable: false),
                    Color = table.Column<string>(maxLength: 32, nullable: true),
                    ScriptTemplate = table.Column<string>(nullable: false),
                    Engine = table.Column<string>(maxLength: 20, nullable: false),
                    RunsRemote = table.Column<bool>(nullable: false),
                    Isolated = table.Column<bool>(nullable: false),
                    MemoryLimitMb = table.Column<int>(nullable: true),
                    MaxProcesses = table.Column<int>(nullable: true),
                    DefaultTimeoutSeconds = table.Column<int>(nullable: true),
                    SuccessExitCodes = table.Column<string>(maxLength: 100, nullable: true),
                    InputParametersJson = table.Column<string>(nullable: false),
                    OutputParametersJson = table.Column<string>(nullable: false),
                    IsEnabled = table.Column<bool>(nullable: false),
                    Version = table.Column<int>(nullable: false),
                    IsDeleted = table.Column<bool>(nullable: false),
                    DeletedAt = table.Column<DateTime>(nullable: true),
                    ConcurrencyToken = table.Column<Guid>(nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false),
                    UpdatedAt = table.Column<DateTime>(nullable: false),
                    CreatedBy = table.Column<string>(maxLength: 100, nullable: true),
                    UpdatedBy = table.Column<string>(maxLength: 100, nullable: true),
                    ChangeNote = table.Column<string>(maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomActivityDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExecutionStatsRollupStates",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false),
                    CoverageStartUtc = table.Column<DateTime>(nullable: true),
                    CoverageEndUtc = table.Column<DateTime>(nullable: true),
                    BackfillComplete = table.Column<bool>(nullable: false),
                    UpdatedAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionStatsRollupStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GlobalVariableFolders",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    ParentFolderId = table.Column<Guid>(nullable: true),
                    Name = table.Column<string>(maxLength: 120, nullable: false),
                    Path = table.Column<string>(maxLength: 800, nullable: false),
                    Depth = table.Column<int>(nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false),
                    CreatedByUserId = table.Column<Guid>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlobalVariableFolders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GlobalVariableFolders_GlobalVariableFolders_ParentFolderId",
                        column: x => x.ParentFolderId,
                        principalTable: "GlobalVariableFolders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IdempotencyKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    Key = table.Column<string>(maxLength: 200, nullable: false),
                    WorkflowId = table.Column<Guid>(nullable: false),
                    ExecutionId = table.Column<Guid>(nullable: false),
                    FirstSeenAt = table.Column<DateTime>(nullable: false),
                    ExpiresAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdempotencyKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MaintenanceWindows",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    Name = table.Column<string>(maxLength: 100, nullable: false),
                    Description = table.Column<string>(maxLength: 500, nullable: true),
                    IsEnabled = table.Column<bool>(nullable: false),
                    Mode = table.Column<string>(maxLength: 20, nullable: false),
                    ScopeKind = table.Column<string>(maxLength: 20, nullable: false),
                    Recurrence = table.Column<string>(maxLength: 20, nullable: false),
                    OneTimeStartUtc = table.Column<DateTime>(nullable: true),
                    OneTimeEndUtc = table.Column<DateTime>(nullable: true),
                    WeeklyDaysMask = table.Column<int>(nullable: false),
                    WeeklyStartMinuteOfDay = table.Column<int>(nullable: true),
                    WeeklyEndMinuteOfDay = table.Column<int>(nullable: true),
                    CronExpression = table.Column<string>(maxLength: 120, nullable: true),
                    DurationMinutes = table.Column<int>(nullable: true),
                    TimeZoneId = table.Column<string>(maxLength: 100, nullable: false),
                    DeferralPolicy = table.Column<string>(maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false),
                    UpdatedAt = table.Column<DateTime>(nullable: false),
                    UpdatedBy = table.Column<string>(maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceWindows", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationDeliveryAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    NotificationRuleId = table.Column<Guid>(nullable: false),
                    NotificationRouteId = table.Column<Guid>(nullable: false),
                    EventKey = table.Column<string>(maxLength: 300, nullable: false),
                    DedupKey = table.Column<string>(maxLength: 300, nullable: false),
                    Status = table.Column<string>(maxLength: 20, nullable: false),
                    Attempt = table.Column<int>(nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false),
                    SentAt = table.Column<DateTime>(nullable: true),
                    Error = table.Column<string>(maxLength: 2000, nullable: true),
                    IsTest = table.Column<bool>(nullable: false),
                    Summary = table.Column<string>(maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationDeliveryAttempts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationDispatcherStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    LastCompletedAtSeen = table.Column<DateTime>(nullable: true),
                    LastIdSeen = table.Column<Guid>(nullable: true),
                    UpdatedAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationDispatcherStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    Kind = table.Column<string>(maxLength: 20, nullable: false, defaultValue: "Custom"),
                    SystemSourceId = table.Column<string>(maxLength: 100, nullable: true),
                    SystemPresetId = table.Column<string>(maxLength: 100, nullable: true),
                    SourceParametersJson = table.Column<string>(nullable: true),
                    SustainForSeconds = table.Column<int>(nullable: false),
                    SeverityOverride = table.Column<string>(maxLength: 20, nullable: true),
                    ActivatedAt = table.Column<DateTime>(nullable: true),
                    Name = table.Column<string>(maxLength: 100, nullable: false),
                    Description = table.Column<string>(maxLength: 500, nullable: true),
                    IsEnabled = table.Column<bool>(nullable: false),
                    EventTypes = table.Column<string>(maxLength: 200, nullable: false),
                    FilterExpressionJson = table.Column<string>(nullable: true),
                    ScopeKind = table.Column<string>(maxLength: 20, nullable: false),
                    CooldownMinutes = table.Column<int>(nullable: false),
                    DedupKeyTemplate = table.Column<string>(maxLength: 300, nullable: true),
                    MinOccurrences = table.Column<int>(nullable: false),
                    OccurrenceWindowMinutes = table.Column<int>(nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false),
                    UpdatedAt = table.Column<DateTime>(nullable: false),
                    UpdatedBy = table.Column<string>(maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationSuppressionStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    NotificationRuleId = table.Column<Guid>(nullable: false),
                    DedupKey = table.Column<string>(maxLength: 300, nullable: false),
                    LastFiredAt = table.Column<DateTime>(nullable: true),
                    OccurrenceCount = table.Column<int>(nullable: false),
                    WindowStartedAt = table.Column<DateTime>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationSuppressionStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OidcLoginTickets",
                columns: table => new
                {
                    Id = table.Column<string>(maxLength: 64, nullable: false),
                    ProtectedPayload = table.Column<byte[]>(nullable: false),
                    ExpiresAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OidcLoginTickets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RevokedTokens",
                columns: table => new
                {
                    Jti = table.Column<string>(maxLength: 64, nullable: false),
                    UserId = table.Column<Guid>(nullable: false),
                    RevokedAt = table.Column<DateTime>(nullable: false),
                    ExpiresAt = table.Column<DateTime>(nullable: false),
                    Reason = table.Column<string>(maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RevokedTokens", x => x.Jti);
                });

            migrationBuilder.CreateTable(
                name: "ScimGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    Authority = table.Column<string>(maxLength: 384, nullable: false, collation: identityCollation),
                    ExternalId = table.Column<string>(maxLength: 384, nullable: false, collation: identityCollation),
                    DisplayName = table.Column<string>(maxLength: 256, nullable: false),
                    IsActive = table.Column<bool>(nullable: false),
                    IsTombstoned = table.Column<bool>(nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false),
                    UpdatedAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScimGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SharedWorkflowFolders",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    ParentFolderId = table.Column<Guid>(nullable: true),
                    Name = table.Column<string>(maxLength: 120, nullable: false),
                    Path = table.Column<string>(maxLength: 800, nullable: false),
                    Depth = table.Column<int>(nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false),
                    CreatedByUserId = table.Column<Guid>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharedWorkflowFolders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SharedWorkflowFolders_SharedWorkflowFolders_ParentFolderId",
                        column: x => x.ParentFolderId,
                        principalTable: "SharedWorkflowFolders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupportEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    Timestamp = table.Column<DateTime>(nullable: false),
                    Level = table.Column<int>(nullable: false),
                    EventType = table.Column<string>(maxLength: 60, nullable: false),
                    Message = table.Column<string>(maxLength: 8000, nullable: false),
                    WorkflowId = table.Column<Guid>(nullable: true),
                    WorkflowName = table.Column<string>(maxLength: 200, nullable: true),
                    ExecutionId = table.Column<Guid>(nullable: true),
                    ExecutionShort = table.Column<string>(maxLength: 8, nullable: true),
                    StepId = table.Column<string>(maxLength: 120, nullable: true),
                    StepLabel = table.Column<string>(maxLength: 200, nullable: true),
                    ActivityType = table.Column<string>(maxLength: 60, nullable: true),
                    UserName = table.Column<string>(maxLength: 200, nullable: true),
                    UserId = table.Column<Guid>(nullable: true),
                    TraceId = table.Column<string>(maxLength: 32, nullable: true),
                    SpanId = table.Column<string>(maxLength: 16, nullable: true),
                    PropertiesJson = table.Column<string>(maxLength: 8000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupportEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SystemAlertPolicyStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    NotificationRuleId = table.Column<Guid>(nullable: false),
                    SourceId = table.Column<string>(maxLength: 100, nullable: false),
                    InstanceKey = table.Column<string>(maxLength: 300, nullable: false),
                    IsMatching = table.Column<bool>(nullable: false),
                    MatchStartedAt = table.Column<DateTime>(nullable: true),
                    EpisodeStartedAt = table.Column<DateTime>(nullable: true),
                    LastObservedAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemAlertPolicyStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SystemAlertSourceStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    SourceId = table.Column<string>(maxLength: 100, nullable: false),
                    StateKey = table.Column<string>(maxLength: 200, nullable: false),
                    CursorJson = table.Column<string>(nullable: true),
                    UpdatedAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemAlertSourceStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SystemHealth",
                columns: table => new
                {
                    ServiceName = table.Column<string>(maxLength: 100, nullable: false),
                    LastHeartbeatAt = table.Column<DateTime>(nullable: false),
                    ExpectedIntervalSeconds = table.Column<int>(nullable: false),
                    Status = table.Column<string>(maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemHealth", x => x.ServiceName);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    Username = table.Column<string>(maxLength: 100, nullable: false),
                    PasswordHash = table.Column<string>(nullable: true),
                    Role = table.Column<string>(maxLength: 20, nullable: false),
                    Provider = table.Column<string>(maxLength: 20, nullable: false),
                    ExternalId = table.Column<string>(maxLength: 256, nullable: true),
                    KnownGroupSidsJson = table.Column<string>(nullable: true),
                    IsActive = table.Column<bool>(nullable: false),
                    IsBreakGlass = table.Column<bool>(nullable: false),
                    IsTombstoned = table.Column<bool>(nullable: false),
                    LastDirectorySyncAt = table.Column<DateTime>(nullable: true),
                    DirectorySyncStatus = table.Column<string>(maxLength: 32, nullable: true),
                    CreatedAt = table.Column<DateTime>(nullable: false),
                    PasswordChangedAt = table.Column<DateTime>(nullable: false),
                    FailedLoginCount = table.Column<int>(nullable: false),
                    LockedUntil = table.Column<DateTime>(nullable: true),
                    SecurityStamp = table.Column<int>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ManagedMachines",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    Name = table.Column<string>(maxLength: 200, nullable: false),
                    Hostname = table.Column<string>(maxLength: 500, nullable: false),
                    WinRmPort = table.Column<int>(nullable: false),
                    UseSsl = table.Column<bool>(nullable: false),
                    DefaultCredentialId = table.Column<Guid>(nullable: true),
                    Tags = table.Column<string>(nullable: true),
                    LastConnectivityCheck = table.Column<DateTime>(nullable: true),
                    IsReachable = table.Column<bool>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManagedMachines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ManagedMachines_Credentials_DefaultCredentialId",
                        column: x => x.DefaultCredentialId,
                        principalTable: "Credentials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "CustomActivityDefinitionVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    DefinitionId = table.Column<Guid>(nullable: false),
                    Version = table.Column<int>(nullable: false),
                    Name = table.Column<string>(maxLength: 200, nullable: false),
                    Description = table.Column<string>(maxLength: 500, nullable: true),
                    Icon = table.Column<string>(maxLength: 60, nullable: false),
                    Color = table.Column<string>(maxLength: 32, nullable: true),
                    ScriptTemplate = table.Column<string>(nullable: false),
                    Engine = table.Column<string>(maxLength: 20, nullable: false),
                    RunsRemote = table.Column<bool>(nullable: false),
                    Isolated = table.Column<bool>(nullable: false),
                    MemoryLimitMb = table.Column<int>(nullable: true),
                    MaxProcesses = table.Column<int>(nullable: true),
                    DefaultTimeoutSeconds = table.Column<int>(nullable: true),
                    SuccessExitCodes = table.Column<string>(maxLength: 100, nullable: true),
                    InputParametersJson = table.Column<string>(nullable: false),
                    OutputParametersJson = table.Column<string>(nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false),
                    CreatedBy = table.Column<string>(maxLength: 100, nullable: true),
                    ChangeNote = table.Column<string>(maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomActivityDefinitionVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomActivityDefinitionVersions_CustomActivityDefinitions_~",
                        column: x => x.DefinitionId,
                        principalTable: "CustomActivityDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GlobalVariables",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    Name = table.Column<string>(maxLength: 100, nullable: false),
                    Value = table.Column<string>(nullable: false),
                    IsSecret = table.Column<bool>(nullable: false),
                    Description = table.Column<string>(maxLength: 500, nullable: true),
                    FolderId = table.Column<Guid>(nullable: false, defaultValue: new Guid("00000000-0000-0000-0000-000000000002")),
                    CreatedAt = table.Column<DateTime>(nullable: false),
                    UpdatedAt = table.Column<DateTime>(nullable: false),
                    UpdatedBy = table.Column<string>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlobalVariables", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GlobalVariables_GlobalVariableFolders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "GlobalVariableFolders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MaintenanceWindowTargets",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    MaintenanceWindowId = table.Column<Guid>(nullable: false),
                    TargetKind = table.Column<string>(maxLength: 20, nullable: false),
                    TargetId = table.Column<Guid>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceWindowTargets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaintenanceWindowTargets_MaintenanceWindows_MaintenanceWind~",
                        column: x => x.MaintenanceWindowId,
                        principalTable: "MaintenanceWindows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NotificationRoutes",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    NotificationRuleId = table.Column<Guid>(nullable: false),
                    Channel = table.Column<string>(maxLength: 20, nullable: false),
                    Target = table.Column<string>(maxLength: 1000, nullable: false),
                    Secret = table.Column<string>(maxLength: 2000, nullable: true),
                    ConditionExpressionJson = table.Column<string>(nullable: true),
                    Order = table.Column<int>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationRoutes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationRoutes_NotificationRules_NotificationRuleId",
                        column: x => x.NotificationRuleId,
                        principalTable: "NotificationRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NotificationRuleTargets",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    NotificationRuleId = table.Column<Guid>(nullable: false),
                    TargetKind = table.Column<string>(maxLength: 20, nullable: false),
                    TargetId = table.Column<Guid>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationRuleTargets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationRuleTargets_NotificationRules_NotificationRuleId",
                        column: x => x.NotificationRuleId,
                        principalTable: "NotificationRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SharedFolderPermissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    FolderId = table.Column<Guid>(nullable: false),
                    PrincipalType = table.Column<string>(maxLength: 20, nullable: false),
                    PrincipalAuthority = table.Column<string>(maxLength: 512, nullable: false, defaultValue: "", collation: identityCollation),
                    PrincipalKey = table.Column<string>(maxLength: 256, nullable: false, collation: identityCollation),
                    Role = table.Column<string>(maxLength: 20, nullable: false),
                    GrantedAt = table.Column<DateTime>(nullable: false),
                    GrantedByUserId = table.Column<Guid>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharedFolderPermissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SharedFolderPermissions_SharedWorkflowFolders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "SharedWorkflowFolders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Workflows",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    Name = table.Column<string>(maxLength: 200, nullable: false),
                    Description = table.Column<string>(nullable: true),
                    DefinitionJson = table.Column<string>(nullable: false),
                    Version = table.Column<int>(nullable: false),
                    IsEnabled = table.Column<bool>(nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false),
                    UpdatedAt = table.Column<DateTime>(nullable: false),
                    CreatedBy = table.Column<string>(nullable: true),
                    UpdatedBy = table.Column<string>(nullable: true),
                    MaxConcurrentExecutions = table.Column<int>(nullable: true),
                    PublishedByUserId = table.Column<Guid>(nullable: true),
                    TriggerTypesJson = table.Column<string>(nullable: true),
                    ActivityCount = table.Column<int>(nullable: false),
                    CheckedOutByUserId = table.Column<Guid>(nullable: true),
                    CheckedOutAt = table.Column<DateTime>(nullable: true),
                    FolderId = table.Column<Guid>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workflows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Workflows_SharedWorkflowFolders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "SharedWorkflowFolders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AuthSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    UserId = table.Column<Guid>(nullable: false),
                    AuthenticationMethod = table.Column<string>(maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false),
                    LastSeenAt = table.Column<DateTime>(nullable: false),
                    ExpiresAt = table.Column<DateTime>(nullable: false),
                    RevokedAt = table.Column<DateTime>(nullable: true),
                    AuthorizationVersion = table.Column<int>(nullable: false),
                    CurrentJti = table.Column<string>(maxLength: 64, nullable: false),
                    RefreshGeneration = table.Column<int>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuthSessions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DirectoryMemberships",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    UserId = table.Column<Guid>(nullable: false),
                    Authority = table.Column<string>(maxLength: 384, nullable: false, collation: identityCollation),
                    GroupKey = table.Column<string>(maxLength: 256, nullable: false, collation: identityCollation),
                    LastSeenAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectoryMemberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DirectoryMemberships_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExternalIdentities",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    UserId = table.Column<Guid>(nullable: false),
                    Authority = table.Column<string>(maxLength: 384, nullable: false, collation: identityCollation),
                    Subject = table.Column<string>(maxLength: 384, nullable: false, collation: identityCollation),
                    CreatedAt = table.Column<DateTime>(nullable: false),
                    LastSeenAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalIdentities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExternalIdentities_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExecutionHourlyStats",
                columns: table => new
                {
                    HourUtc = table.Column<DateTime>(nullable: false),
                    WorkflowId = table.Column<Guid>(nullable: false),
                    TotalCount = table.Column<int>(nullable: false),
                    SucceededCount = table.Column<int>(nullable: false),
                    FailedCount = table.Column<int>(nullable: false),
                    CancelledCount = table.Column<int>(nullable: false),
                    RunningCount = table.Column<int>(nullable: false),
                    RetriedCount = table.Column<int>(nullable: false),
                    FinishedCount = table.Column<int>(nullable: false),
                    DurationMsSum = table.Column<long>(nullable: false),
                    DurationMsCount = table.Column<int>(nullable: false),
                    IsFinal = table.Column<bool>(nullable: false),
                    ComputedAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionHourlyStats", x => new { x.HourUtc, x.WorkflowId });
                    table.ForeignKey(
                        name: "FK_ExecutionHourlyStats_Workflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "Workflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FailureCauseHourlyStats",
                columns: table => new
                {
                    HourUtc = table.Column<DateTime>(nullable: false),
                    WorkflowId = table.Column<Guid>(nullable: false),
                    MessageHash = table.Column<string>(maxLength: 64, nullable: false),
                    Message = table.Column<string>(nullable: true),
                    Count = table.Column<int>(nullable: false),
                    LatestExecutionId = table.Column<Guid>(nullable: false),
                    LatestStartedAt = table.Column<DateTime>(nullable: false),
                    IsFinal = table.Column<bool>(nullable: false),
                    ComputedAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FailureCauseHourlyStats", x => new { x.HourUtc, x.WorkflowId, x.MessageHash });
                    table.ForeignKey(
                        name: "FK_FailureCauseHourlyStats_Workflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "Workflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TriggerDeliveryCheckpoints",
                columns: table => new
                {
                    WorkflowId = table.Column<Guid>(nullable: false),
                    TriggerNodeId = table.Column<string>(maxLength: 200, nullable: false),
                    TriggerType = table.Column<string>(maxLength: 100, nullable: false),
                    ConfigurationHash = table.Column<string>(maxLength: 128, nullable: false),
                    Position = table.Column<string>(nullable: false),
                    Version = table.Column<string>(maxLength: 500, nullable: false),
                    UpdatedAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TriggerDeliveryCheckpoints", x => new { x.WorkflowId, x.TriggerNodeId });
                    table.ForeignKey(
                        name: "FK_TriggerDeliveryCheckpoints_Workflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "Workflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TriggerDeliveryReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    WorkflowId = table.Column<Guid>(nullable: false),
                    TriggerNodeId = table.Column<string>(maxLength: 200, nullable: false),
                    TriggerType = table.Column<string>(maxLength: 100, nullable: false),
                    EventKey = table.Column<string>(maxLength: 500, nullable: false),
                    Outcome = table.Column<string>(maxLength: 40, nullable: false),
                    ExecutionId = table.Column<Guid>(nullable: true),
                    ReceivedAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TriggerDeliveryReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TriggerDeliveryReceipts_Workflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "Workflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    WorkflowId = table.Column<Guid>(nullable: false),
                    Status = table.Column<string>(maxLength: 20, nullable: false),
                    StartedAt = table.Column<DateTime>(nullable: false),
                    CompletedAt = table.Column<DateTime>(nullable: true),
                    TriggeredBy = table.Column<string>(nullable: true),
                    ErrorMessage = table.Column<string>(nullable: true),
                    StartedByUserId = table.Column<Guid>(nullable: true),
                    TraceId = table.Column<string>(maxLength: 32, nullable: true),
                    SpanId = table.Column<string>(maxLength: 16, nullable: true),
                    ParentExecutionId = table.Column<Guid>(nullable: true),
                    CallDepth = table.Column<int>(nullable: false),
                    ReturnData = table.Column<string>(nullable: true),
                    InputParametersJson = table.Column<string>(nullable: true),
                    OwnerNodeId = table.Column<string>(maxLength: 200, nullable: true),
                    CancelledBy = table.Column<string>(maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowExecutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowExecutions_Workflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "Workflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowStats",
                columns: table => new
                {
                    WorkflowId = table.Column<Guid>(nullable: false),
                    TotalExecutions = table.Column<int>(nullable: false),
                    SucceededWindow = table.Column<int>(nullable: false),
                    FailedWindow = table.Column<int>(nullable: false),
                    CancelledWindow = table.Column<int>(nullable: false),
                    WindowDays = table.Column<int>(nullable: false),
                    AvgDurationMsWindow = table.Column<double>(nullable: true),
                    P50DurationMsWindow = table.Column<double>(nullable: true),
                    P95DurationMsWindow = table.Column<double>(nullable: true),
                    LastExecutionAt = table.Column<DateTime>(nullable: true),
                    LastSuccessAt = table.Column<DateTime>(nullable: true),
                    LastFailureAt = table.Column<DateTime>(nullable: true),
                    RefreshedAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowStats", x => x.WorkflowId);
                    table.ForeignKey(
                        name: "FK_WorkflowStats_Workflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "Workflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    WorkflowId = table.Column<Guid>(nullable: false),
                    Version = table.Column<int>(nullable: false),
                    Name = table.Column<string>(maxLength: 200, nullable: false),
                    Description = table.Column<string>(nullable: true),
                    DefinitionJson = table.Column<string>(nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false),
                    CreatedBy = table.Column<string>(maxLength: 100, nullable: true),
                    ChangeNote = table.Column<string>(maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowVersions_Workflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "Workflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExecutionDispatchOutbox",
                columns: table => new
                {
                    ExecutionId = table.Column<Guid>(nullable: false),
                    WorkflowId = table.Column<Guid>(nullable: false),
                    TriggeredBy = table.Column<string>(maxLength: 100, nullable: false),
                    ProtectedParameters = table.Column<byte[]>(nullable: true),
                    TimeoutSeconds = table.Column<int>(nullable: true),
                    DebugEnabled = table.Column<bool>(nullable: false),
                    StartedByUserId = table.Column<Guid>(nullable: true),
                    ParentExecutionId = table.Column<Guid>(nullable: true),
                    CallDepth = table.Column<int>(nullable: false),
                    RequireWorkflowEnabled = table.Column<bool>(nullable: false),
                    MissingWorkflowMessage = table.Column<string>(maxLength: 2000, nullable: false),
                    PreOwnershipFailurePrefix = table.Column<string>(maxLength: 1000, nullable: false),
                    Priority = table.Column<int>(nullable: false),
                    RequireMaintenanceWindowCheck = table.Column<bool>(nullable: false),
                    BypassMaintenanceWindow = table.Column<bool>(nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false),
                    AvailableAt = table.Column<DateTime>(nullable: false),
                    LeaseOwner = table.Column<string>(maxLength: 240, nullable: true),
                    LeaseExpiresAt = table.Column<DateTime>(nullable: true),
                    AttemptCount = table.Column<int>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionDispatchOutbox", x => x.ExecutionId);
                    table.ForeignKey(
                        name: "FK_ExecutionDispatchOutbox_WorkflowExecutions_ExecutionId",
                        column: x => x.ExecutionId,
                        principalTable: "WorkflowExecutions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StepExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    WorkflowExecutionId = table.Column<Guid>(nullable: false),
                    StepId = table.Column<string>(maxLength: 100, nullable: false),
                    StepName = table.Column<string>(nullable: true),
                    StepType = table.Column<string>(maxLength: 30, nullable: false),
                    TargetMachine = table.Column<string>(nullable: true),
                    Status = table.Column<string>(maxLength: 20, nullable: false),
                    StartedAt = table.Column<DateTime>(nullable: true),
                    CompletedAt = table.Column<DateTime>(nullable: true),
                    Output = table.Column<string>(nullable: true),
                    ErrorOutput = table.Column<string>(nullable: true),
                    AttemptCount = table.Column<int>(nullable: false),
                    PausedAt = table.Column<DateTime>(nullable: true),
                    VariablesSnapshot = table.Column<string>(nullable: true),
                    TraceOutput = table.Column<string>(nullable: true),
                    OutputParametersJson = table.Column<string>(nullable: true),
                    CustomActivityKey = table.Column<string>(nullable: true),
                    CustomActivityVersion = table.Column<int>(nullable: true),
                    CustomActivityHash = table.Column<string>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StepExecutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StepExecutions_WorkflowExecutions_WorkflowExecutionId",
                        column: x => x.WorkflowExecutionId,
                        principalTable: "WorkflowExecutions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "GlobalVariableFolders",
                columns: new[] { "Id", "CreatedAt", "CreatedByUserId", "Depth", "Name", "ParentFolderId", "Path" },
                values: new object[] { new Guid("00000000-0000-0000-0000-000000000002"), new DateTime(2020, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, 0, "Root", null, "/" });

            migrationBuilder.InsertData(
                table: "SharedWorkflowFolders",
                columns: new[] { "Id", "CreatedAt", "CreatedByUserId", "Depth", "Name", "ParentFolderId", "Path" },
                values: new object[] { new Guid("00000000-0000-0000-0000-000000000001"), new DateTime(2020, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, 0, "Root", null, "/" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_Action_Timestamp",
                table: "AuditLog",
                columns: new[] { "Action", "Timestamp" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_IpAddress_Timestamp",
                table: "AuditLog",
                columns: new[] { "IpAddress", "Timestamp" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_ResourceId_Timestamp",
                table: "AuditLog",
                columns: new[] { "ResourceId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_Timestamp",
                table: "AuditLog",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_UserId_Timestamp",
                table: "AuditLog",
                columns: new[] { "UserId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_Username_Timestamp",
                table: "AuditLog",
                columns: new[] { "Username", "Timestamp" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_AuthSessions_ExpiresAt",
                table: "AuthSessions",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_AuthSessions_UserId",
                table: "AuthSessions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomActivityDefinitions_IsDeleted_IsEnabled",
                table: "CustomActivityDefinitions",
                columns: new[] { "IsDeleted", "IsEnabled" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomActivityDefinitions_Key",
                table: "CustomActivityDefinitions",
                column: "Key");

            migrationBuilder.CreateIndex(
                name: "IX_CustomActivityDefinitionVersions_DefinitionId_Version",
                table: "CustomActivityDefinitionVersions",
                columns: new[] { "DefinitionId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DirectoryMemberships_Authority_GroupKey",
                table: "DirectoryMemberships",
                columns: new[] { "Authority", "GroupKey" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectoryMemberships_UserId_Authority_GroupKey",
                table: "DirectoryMemberships",
                columns: new[] { "UserId", "Authority", "GroupKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionDispatchOutbox_AvailableAt_Priority",
                table: "ExecutionDispatchOutbox",
                columns: new[] { "AvailableAt", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionDispatchOutbox_LeaseExpiresAt",
                table: "ExecutionDispatchOutbox",
                column: "LeaseExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionHourlyStats_IsFinal_HourUtc",
                table: "ExecutionHourlyStats",
                columns: new[] { "IsFinal", "HourUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionHourlyStats_WorkflowId",
                table: "ExecutionHourlyStats",
                column: "WorkflowId");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalIdentities_Authority_Subject",
                table: "ExternalIdentities",
                columns: new[] { "Authority", "Subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalIdentities_UserId",
                table: "ExternalIdentities",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_FailureCauseHourlyStats_IsFinal_HourUtc",
                table: "FailureCauseHourlyStats",
                columns: new[] { "IsFinal", "HourUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_FailureCauseHourlyStats_WorkflowId",
                table: "FailureCauseHourlyStats",
                column: "WorkflowId");

            migrationBuilder.CreateIndex(
                name: "IX_GlobalVariableFolders_ParentFolderId",
                table: "GlobalVariableFolders",
                column: "ParentFolderId");

            migrationBuilder.CreateIndex(
                name: "IX_GlobalVariableFolders_ParentFolderId_Name",
                table: "GlobalVariableFolders",
                columns: new[] { "ParentFolderId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GlobalVariables_FolderId",
                table: "GlobalVariables",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_GlobalVariables_Name",
                table: "GlobalVariables",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdempotencyKeys_ExpiresAt",
                table: "IdempotencyKeys",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_IdempotencyKeys_Key_WorkflowId",
                table: "IdempotencyKeys",
                columns: new[] { "Key", "WorkflowId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceWindows_IsEnabled",
                table: "MaintenanceWindows",
                column: "IsEnabled");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceWindows_Name",
                table: "MaintenanceWindows",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceWindowTargets_MaintenanceWindowId_TargetKind_Tar~",
                table: "MaintenanceWindowTargets",
                columns: new[] { "MaintenanceWindowId", "TargetKind", "TargetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceWindowTargets_TargetId",
                table: "MaintenanceWindowTargets",
                column: "TargetId");

            migrationBuilder.CreateIndex(
                name: "IX_ManagedMachines_DefaultCredentialId",
                table: "ManagedMachines",
                column: "DefaultCredentialId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveryAttempts_CreatedAt",
                table: "NotificationDeliveryAttempts",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveryAttempts_NotificationRuleId_Notificatio~",
                table: "NotificationDeliveryAttempts",
                columns: new[] { "NotificationRuleId", "NotificationRouteId", "EventKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRoutes_NotificationRuleId",
                table: "NotificationRoutes",
                column: "NotificationRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRules_IsEnabled",
                table: "NotificationRules",
                column: "IsEnabled");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRules_Kind_IsEnabled",
                table: "NotificationRules",
                columns: new[] { "Kind", "IsEnabled" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRules_Name",
                table: "NotificationRules",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRuleTargets_NotificationRuleId_TargetKind_Targe~",
                table: "NotificationRuleTargets",
                columns: new[] { "NotificationRuleId", "TargetKind", "TargetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRuleTargets_TargetId",
                table: "NotificationRuleTargets",
                column: "TargetId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationSuppressionStates_NotificationRuleId_DedupKey",
                table: "NotificationSuppressionStates",
                columns: new[] { "NotificationRuleId", "DedupKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OidcLoginTickets_ExpiresAt",
                table: "OidcLoginTickets",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_RevokedTokens_ExpiresAt",
                table: "RevokedTokens",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ScimGroups_Authority_ExternalId",
                table: "ScimGroups",
                columns: new[] { "Authority", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScimGroups_DisplayName",
                table: "ScimGroups",
                column: "DisplayName");

            migrationBuilder.CreateIndex(
                name: "IX_SharedFolderPermissions_FolderId",
                table: "SharedFolderPermissions",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "UX_SharedFolderPermissions_Principal",
                table: "SharedFolderPermissions",
                columns: new[] { "FolderId", "PrincipalType", "PrincipalAuthority", "PrincipalKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SharedWorkflowFolders_ParentFolderId",
                table: "SharedWorkflowFolders",
                column: "ParentFolderId");

            migrationBuilder.CreateIndex(
                name: "IX_SharedWorkflowFolders_ParentFolderId_Name",
                table: "SharedWorkflowFolders",
                columns: new[] { "ParentFolderId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StepExecutions_Running",
                table: "StepExecutions",
                column: "Status",
                filter: "\"Status\" = 'Running'");

            migrationBuilder.CreateIndex(
                name: "IX_StepExecutions_WorkflowExecutionId_StartedAt",
                table: "StepExecutions",
                columns: new[] { "WorkflowExecutionId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StepExecutions_WorkflowExecutionId_Status",
                table: "StepExecutions",
                columns: new[] { "WorkflowExecutionId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_SupportEvents_EventType_Timestamp",
                table: "SupportEvents",
                columns: new[] { "EventType", "Timestamp" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_SupportEvents_ExecutionId_Timestamp",
                table: "SupportEvents",
                columns: new[] { "ExecutionId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_SupportEvents_Level_Timestamp",
                table: "SupportEvents",
                columns: new[] { "Level", "Timestamp" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_SupportEvents_Timestamp",
                table: "SupportEvents",
                column: "Timestamp",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_SupportEvents_WorkflowName_Timestamp",
                table: "SupportEvents",
                columns: new[] { "WorkflowName", "Timestamp" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_SystemAlertPolicyStates_LastObservedAt",
                table: "SystemAlertPolicyStates",
                column: "LastObservedAt");

            migrationBuilder.CreateIndex(
                name: "IX_SystemAlertPolicyStates_NotificationRuleId_SourceId_Instanc~",
                table: "SystemAlertPolicyStates",
                columns: new[] { "NotificationRuleId", "SourceId", "InstanceKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SystemAlertSourceStates_SourceId_StateKey",
                table: "SystemAlertSourceStates",
                columns: new[] { "SourceId", "StateKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TriggerDeliveryReceipts_ReceivedAt",
                table: "TriggerDeliveryReceipts",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TriggerDeliveryReceipts_WorkflowId_TriggerNodeId_EventKey",
                table: "TriggerDeliveryReceipts",
                columns: new[] { "WorkflowId", "TriggerNodeId", "EventKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Provider_ExternalId",
                table: "Users",
                columns: new[] { "Provider", "ExternalId" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowExecutions_CompletedAt_Id",
                table: "WorkflowExecutions",
                columns: new[] { "CompletedAt", "Id" })
                .Annotation("SqlServer:Include", new[] { "Status" })
                .Annotation("Npgsql:IndexInclude", new[] { "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowExecutions_ParentExecutionId",
                table: "WorkflowExecutions",
                column: "ParentExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowExecutions_StartedAt_Status",
                table: "WorkflowExecutions",
                columns: new[] { "StartedAt", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowExecutions_Status_StartedAt",
                table: "WorkflowExecutions",
                columns: new[] { "Status", "StartedAt" },
                descending: new[] { false, true })
                .Annotation("SqlServer:Include", new[] { "WorkflowId", "CompletedAt", "TriggeredBy" })
                .Annotation("Npgsql:IndexInclude", new[] { "WorkflowId", "CompletedAt", "TriggeredBy" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowExecutions_TraceId",
                table: "WorkflowExecutions",
                column: "TraceId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowExecutions_WorkflowId_StartedAt",
                table: "WorkflowExecutions",
                columns: new[] { "WorkflowId", "StartedAt" },
                descending: new[] { false, true })
                .Annotation("SqlServer:Include", new[] { "Status", "CompletedAt" })
                .Annotation("Npgsql:IndexInclude", new[] { "Status", "CompletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Workflows_CheckedOutByUserId",
                table: "Workflows",
                column: "CheckedOutByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Workflows_FolderId",
                table: "Workflows",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowVersions_WorkflowId_Version",
                table: "WorkflowVersions",
                columns: new[] { "WorkflowId", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditLog");

            migrationBuilder.DropTable(
                name: "AuthSessions");

            migrationBuilder.DropTable(
                name: "ClusterLeaders");

            migrationBuilder.DropTable(
                name: "CustomActivityDefinitionVersions");

            migrationBuilder.DropTable(
                name: "DirectoryMemberships");

            migrationBuilder.DropTable(
                name: "ExecutionDispatchOutbox");

            migrationBuilder.DropTable(
                name: "ExecutionHourlyStats");

            migrationBuilder.DropTable(
                name: "ExecutionStatsRollupStates");

            migrationBuilder.DropTable(
                name: "ExternalIdentities");

            migrationBuilder.DropTable(
                name: "FailureCauseHourlyStats");

            migrationBuilder.DropTable(
                name: "GlobalVariables");

            migrationBuilder.DropTable(
                name: "IdempotencyKeys");

            migrationBuilder.DropTable(
                name: "MaintenanceWindowTargets");

            migrationBuilder.DropTable(
                name: "ManagedMachines");

            migrationBuilder.DropTable(
                name: "NotificationDeliveryAttempts");

            migrationBuilder.DropTable(
                name: "NotificationDispatcherStates");

            migrationBuilder.DropTable(
                name: "NotificationRoutes");

            migrationBuilder.DropTable(
                name: "NotificationRuleTargets");

            migrationBuilder.DropTable(
                name: "NotificationSuppressionStates");

            migrationBuilder.DropTable(
                name: "OidcLoginTickets");

            migrationBuilder.DropTable(
                name: "RevokedTokens");

            migrationBuilder.DropTable(
                name: "ScimGroups");

            migrationBuilder.DropTable(
                name: "SharedFolderPermissions");

            migrationBuilder.DropTable(
                name: "StepExecutions");

            migrationBuilder.DropTable(
                name: "SupportEvents");

            migrationBuilder.DropTable(
                name: "SystemAlertPolicyStates");

            migrationBuilder.DropTable(
                name: "SystemAlertSourceStates");

            migrationBuilder.DropTable(
                name: "SystemHealth");

            migrationBuilder.DropTable(
                name: "TriggerDeliveryCheckpoints");

            migrationBuilder.DropTable(
                name: "TriggerDeliveryReceipts");

            migrationBuilder.DropTable(
                name: "WorkflowStats");

            migrationBuilder.DropTable(
                name: "WorkflowVersions");

            migrationBuilder.DropTable(
                name: "CustomActivityDefinitions");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "GlobalVariableFolders");

            migrationBuilder.DropTable(
                name: "MaintenanceWindows");

            migrationBuilder.DropTable(
                name: "Credentials");

            migrationBuilder.DropTable(
                name: "NotificationRules");

            migrationBuilder.DropTable(
                name: "WorkflowExecutions");

            migrationBuilder.DropTable(
                name: "Workflows");

            migrationBuilder.DropTable(
                name: "SharedWorkflowFolders");
        }
    }
}
