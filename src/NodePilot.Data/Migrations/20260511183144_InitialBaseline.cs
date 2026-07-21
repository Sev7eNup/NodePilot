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
            migrationBuilder.CreateTable(
                name: "AuditLog",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    Timestamp = table.Column<DateTime>(nullable: false),
                    UserId = table.Column<Guid>(nullable: true),
                    Action = table.Column<string>(maxLength: 100, nullable: false),
                    ResourceType = table.Column<string>(nullable: true),
                    ResourceId = table.Column<Guid>(nullable: true),
                    Details = table.Column<string>(nullable: true)
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
                    Domain = table.Column<string>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Credentials", x => x.Id);
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
                    CreatedAt = table.Column<DateTime>(nullable: false),
                    UpdatedAt = table.Column<DateTime>(nullable: false),
                    UpdatedBy = table.Column<string>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlobalVariables", x => x.Id);
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
                    CreatedAt = table.Column<DateTime>(nullable: false),
                    PasswordChangedAt = table.Column<DateTime>(nullable: false),
                    FailedLoginCount = table.Column<int>(nullable: false),
                    LockedUntil = table.Column<DateTime>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserWorkflowFolders",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    UserId = table.Column<Guid>(nullable: false),
                    Name = table.Column<string>(maxLength: 120, nullable: false),
                    ParentFolderId = table.Column<Guid>(nullable: true),
                    SortOrder = table.Column<int>(nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserWorkflowFolders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowFolderAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    UserId = table.Column<Guid>(nullable: false),
                    WorkflowId = table.Column<Guid>(nullable: false),
                    FolderId = table.Column<Guid>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowFolderAssignments", x => x.Id);
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
                name: "SharedFolderPermissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    FolderId = table.Column<Guid>(nullable: false),
                    PrincipalType = table.Column<string>(maxLength: 20, nullable: false),
                    PrincipalKey = table.Column<string>(maxLength: 256, nullable: false),
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
                    OwnerNodeId = table.Column<string>(maxLength: 200, nullable: true)
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
                    OutputParametersJson = table.Column<string>(nullable: true)
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
                table: "SharedWorkflowFolders",
                columns: new[] { "Id", "CreatedAt", "CreatedByUserId", "Depth", "Name", "ParentFolderId", "Path" },
                values: new object[] { new Guid("00000000-0000-0000-0000-000000000001"), new DateTime(2020, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, 0, "Root", null, "/" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_Action_Timestamp",
                table: "AuditLog",
                columns: new[] { "Action", "Timestamp" },
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
                name: "IX_ManagedMachines_DefaultCredentialId",
                table: "ManagedMachines",
                column: "DefaultCredentialId");

            migrationBuilder.CreateIndex(
                name: "IX_RevokedTokens_ExpiresAt",
                table: "RevokedTokens",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_SharedFolderPermissions_FolderId",
                table: "SharedFolderPermissions",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_SharedFolderPermissions_FolderId_PrincipalType_PrincipalKey",
                table: "SharedFolderPermissions",
                columns: new[] { "FolderId", "PrincipalType", "PrincipalKey" },
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
                name: "IX_StepExecutions_WorkflowExecutionId_StartedAt",
                table: "StepExecutions",
                columns: new[] { "WorkflowExecutionId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StepExecutions_WorkflowExecutionId_Status",
                table: "StepExecutions",
                columns: new[] { "WorkflowExecutionId", "Status" });

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
                name: "IX_UserWorkflowFolders_UserId_ParentFolderId",
                table: "UserWorkflowFolders",
                columns: new[] { "UserId", "ParentFolderId" });

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
                name: "IX_WorkflowFolderAssignments_FolderId",
                table: "WorkflowFolderAssignments",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowFolderAssignments_UserId_WorkflowId",
                table: "WorkflowFolderAssignments",
                columns: new[] { "UserId", "WorkflowId" },
                unique: true);

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
                name: "ClusterLeaders");

            migrationBuilder.DropTable(
                name: "GlobalVariables");

            migrationBuilder.DropTable(
                name: "IdempotencyKeys");

            migrationBuilder.DropTable(
                name: "ManagedMachines");

            migrationBuilder.DropTable(
                name: "RevokedTokens");

            migrationBuilder.DropTable(
                name: "SharedFolderPermissions");

            migrationBuilder.DropTable(
                name: "StepExecutions");

            migrationBuilder.DropTable(
                name: "SystemHealth");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "UserWorkflowFolders");

            migrationBuilder.DropTable(
                name: "WorkflowFolderAssignments");

            migrationBuilder.DropTable(
                name: "WorkflowStats");

            migrationBuilder.DropTable(
                name: "WorkflowVersions");

            migrationBuilder.DropTable(
                name: "Credentials");

            migrationBuilder.DropTable(
                name: "WorkflowExecutions");

            migrationBuilder.DropTable(
                name: "Workflows");

            migrationBuilder.DropTable(
                name: "SharedWorkflowFolders");
        }
    }
}
