namespace NodePilot.Core.Audit;

/// <summary>
/// The single authoritative registry of audit-event action codes (the <c>VERB_NOMEN</c> string
/// stored in <c>AuditLog.Action</c>). Previously these were free-form string literals scattered
/// across ~21 controllers with the full list existing only as prose in docs/claude-reference.md.
///
/// <para>New audit calls MUST reference a constant here rather than a raw literal — the guard test
/// <c>AuditActionsCatalogTests</c> (Api.Tests) fails CI if any <c>LogAsync("LITERAL")</c> remains
/// at a call site, or if a code is emitted that isn't registered here, or if a registered code is
/// never used. Codes are grouped by resource; values are the exact strings persisted + audited.</para>
/// </summary>
public static class AuditActions
{
    public const string AiKnowledgeAsked = "AI_KNOWLEDGE_ASKED";
    public const string AiProposalApplied = "AI_PROPOSAL_APPLIED";
    public const string AiScriptGenerated = "AI_SCRIPT_GENERATED";
    public const string AiWorkflowExplained = "AI_WORKFLOW_EXPLAINED";
    public const string AiWorkflowGenerated = "AI_WORKFLOW_GENERATED";

    public const string AlertRuleCreated = "ALERT_RULE_CREATED";
    public const string AlertRuleDeleted = "ALERT_RULE_DELETED";
    public const string AlertRuleDisabled = "ALERT_RULE_DISABLED";
    public const string AlertRuleEnabled = "ALERT_RULE_ENABLED";
    public const string AlertRuleTestFired = "ALERT_RULE_TEST_FIRED";
    public const string AlertRuleUpdated = "ALERT_RULE_UPDATED";

    public const string SystemAlertPolicyCreated = "SYSTEM_ALERT_POLICY_CREATED";
    public const string SystemAlertPolicyDeleted = "SYSTEM_ALERT_POLICY_DELETED";
    public const string SystemAlertPolicyDisabled = "SYSTEM_ALERT_POLICY_DISABLED";
    public const string SystemAlertPolicyEnabled = "SYSTEM_ALERT_POLICY_ENABLED";
    public const string SystemAlertPolicyTestFired = "SYSTEM_ALERT_POLICY_TEST_FIRED";
    public const string SystemAlertPolicyUpdated = "SYSTEM_ALERT_POLICY_UPDATED";

    public const string BackupExported = "BACKUP_EXPORTED";
    public const string BackupRestored = "BACKUP_RESTORED";

    public const string CredentialCreated = "CREDENTIAL_CREATED";
    public const string CredentialDecrypted = "CREDENTIAL_DECRYPTED";
    public const string CredentialDecryptFailed = "CREDENTIAL_DECRYPT_FAILED";
    public const string CredentialDeleted = "CREDENTIAL_DELETED";
    public const string CredentialUpdated = "CREDENTIAL_UPDATED";

    public const string CustomActivityCreated = "CUSTOM_ACTIVITY_CREATED";
    public const string CustomActivityDeleted = "CUSTOM_ACTIVITY_DELETED";
    public const string CustomActivityDisabled = "CUSTOM_ACTIVITY_DISABLED";
    public const string CustomActivityEnabled = "CUSTOM_ACTIVITY_ENABLED";
    public const string CustomActivityExported = "CUSTOM_ACTIVITY_EXPORTED";
    public const string CustomActivityImported = "CUSTOM_ACTIVITY_IMPORTED";
    public const string CustomActivityRolledBack = "CUSTOM_ACTIVITY_ROLLED_BACK";
    public const string CustomActivityUpdated = "CUSTOM_ACTIVITY_UPDATED";

    public const string ExecutionBlockedMaintenanceWindow = "EXECUTION_BLOCKED_MAINTENANCE_WINDOW";
    public const string ExecutionCancelled = "EXECUTION_CANCELLED";
    public const string ExecutionDebugStop = "EXECUTION_DEBUG_STOP";
    public const string ExecutionRecoveredFailover = "EXECUTION_RECOVERED_FAILOVER";
    public const string ExecutionResumed = "EXECUTION_RESUMED";
    public const string ExecutionRetried = "EXECUTION_RETRIED";
    public const string ExecutionStepOver = "EXECUTION_STEP_OVER";
    public const string ExecutionStarted = "EXECUTION_STARTED";

    public const string ExternalTriggerFired = "EXTERNAL_TRIGGER_FIRED";

    public const string FolderCreated = "FOLDER_CREATED";
    public const string FolderDeleted = "FOLDER_DELETED";
    public const string FolderMoved = "FOLDER_MOVED";
    public const string FolderPermissionGranted = "FOLDER_PERMISSION_GRANTED";
    public const string FolderPermissionRevoked = "FOLDER_PERMISSION_REVOKED";
    public const string FolderPermissionUpdated = "FOLDER_PERMISSION_UPDATED";
    public const string FolderUpdated = "FOLDER_UPDATED";

    public const string GlobalVariableCreated = "GLOBAL_VARIABLE_CREATED";
    public const string GlobalVariableDeleted = "GLOBAL_VARIABLE_DELETED";
    public const string GlobalVariableFolderCreated = "GLOBAL_VARIABLE_FOLDER_CREATED";
    public const string GlobalVariableFolderDeleted = "GLOBAL_VARIABLE_FOLDER_DELETED";
    public const string GlobalVariableFolderMoved = "GLOBAL_VARIABLE_FOLDER_MOVED";
    public const string GlobalVariableFolderUpdated = "GLOBAL_VARIABLE_FOLDER_UPDATED";
    public const string GlobalVariableMoved = "GLOBAL_VARIABLE_MOVED";
    public const string GlobalVariableUpdated = "GLOBAL_VARIABLE_UPDATED";

    public const string LoginFailed = "LOGIN_FAILED";
    public const string LoginLocked = "LOGIN_LOCKED";
    public const string LoginSuccess = "LOGIN_SUCCESS";
    public const string BreakGlassLoginSuccess = "BREAK_GLASS_LOGIN_SUCCESS";

    public const string Logout = "LOGOUT";

    public const string MachineConnectionTestFailed = "MACHINE_CONNECTION_TEST_FAILED";
    public const string MachineConnectionTested = "MACHINE_CONNECTION_TESTED";
    public const string MachineCreated = "MACHINE_CREATED";
    public const string MachineDeleted = "MACHINE_DELETED";
    public const string MachineUpdated = "MACHINE_UPDATED";

    public const string MaintenanceWindowCreated = "MAINTENANCE_WINDOW_CREATED";
    public const string MaintenanceWindowDeleted = "MAINTENANCE_WINDOW_DELETED";
    public const string MaintenanceWindowOverridden = "MAINTENANCE_WINDOW_OVERRIDDEN";
    public const string MaintenanceWindowUpdated = "MAINTENANCE_WINDOW_UPDATED";

    public const string SecretsReencrypted = "SECRETS_REENCRYPTED";

    public const string SettingsAiKnowledgeUpdated = "SETTINGS_AIKNOWLEDGE_UPDATED";
    public const string SettingsAuthenticationUpdated = "SETTINGS_AUTHENTICATION_UPDATED";
    public const string SettingsAuthenticationTested = "SETTINGS_AUTHENTICATION_TESTED";
    public const string SettingsDbadminUpdated = "SETTINGS_DBADMIN_UPDATED";
    public const string SettingsEngineUpdated = "SETTINGS_ENGINE_UPDATED";
    public const string SettingsExecutionDispatchUpdated = "SETTINGS_EXECUTIONDISPATCH_UPDATED";
    public const string SettingsExternalTriggerUpdated = "SETTINGS_EXTERNALTRIGGER_UPDATED";
    public const string SettingsFilesystemOperationUpdated = "SETTINGS_FILESYSTEMOPERATION_UPDATED";
    public const string SettingsLlmTested = "SETTINGS_LLM_TESTED";
    public const string SettingsLlmUpdated = "SETTINGS_LLM_UPDATED";
    public const string SettingsLoggingUpdated = "SETTINGS_LOGGING_UPDATED";
    public const string SettingsOpentelemetryUpdated = "SETTINGS_OPENTELEMETRY_UPDATED";
    public const string SettingsRemoteUpdated = "SETTINGS_REMOTE_UPDATED";
    public const string SettingsRestApiUpdated = "SETTINGS_RESTAPI_UPDATED";
    public const string SettingsRetentionUpdated = "SETTINGS_RETENTION_UPDATED";
    public const string SettingsSecurityUpdated = "SETTINGS_SECURITY_UPDATED";
    public const string SettingsSmtpTested = "SETTINGS_SMTP_TESTED";
    public const string SettingsSmtpUpdated = "SETTINGS_SMTP_UPDATED";
    public const string SettingsStatsUpdated = "SETTINGS_STATS_UPDATED";
    public const string SettingsSqlActivityUpdated = "SETTINGS_SQLACTIVITY_UPDATED";
    public const string SettingsStartProgramUpdated = "SETTINGS_STARTPROGRAM_UPDATED";
    public const string SettingsThreadingUpdated = "SETTINGS_THREADING_UPDATED";
    public const string SettingsWebhookUpdated = "SETTINGS_WEBHOOK_UPDATED";

    public const string TokenRefreshed = "TOKEN_REFRESHED";

    public const string TriggerFireSuppressed = "TRIGGER_FIRE_SUPPRESSED";

    public const string UserCreated = "USER_CREATED";
    public const string UserCreatedBootstrap = "USER_CREATED_BOOTSTRAP";
    public const string UserActivated = "USER_ACTIVATED";
    public const string UserDeactivated = "USER_DEACTIVATED";
    public const string UserDeleted = "USER_DELETED";
    public const string UserPasswordReset = "USER_PASSWORD_RESET";
    public const string UserRoleChanged = "USER_ROLE_CHANGED";
    public const string UserBreakGlassChanged = "USER_BREAK_GLASS_CHANGED";

    public const string UserLdapJitCreated = "USER_LDAP_JIT_CREATED";
    public const string UserLdapJitUpdated = "USER_LDAP_JIT_UPDATED";
    public const string UserLdapRefusedBootstrap = "USER_LDAP_REFUSED_BOOTSTRAP";
    public const string UserLdapRefusedCollision = "USER_LDAP_REFUSED_COLLISION";
    public const string UserLdapRefusedLastAdmin = "USER_LDAP_REFUSED_LAST_ADMIN";
    public const string UserWindowsJitCreated = "USER_WINDOWS_JIT_CREATED";
    public const string UserWindowsJitUpdated = "USER_WINDOWS_JIT_UPDATED";
    public const string UserWindowsRefusedBootstrap = "USER_WINDOWS_REFUSED_BOOTSTRAP";
    public const string UserWindowsRefusedCollision = "USER_WINDOWS_REFUSED_COLLISION";
    public const string UserWindowsRefusedLastAdmin = "USER_WINDOWS_REFUSED_LAST_ADMIN";
    public const string UserDirectoryAccessRefused = "USER_DIRECTORY_ACCESS_REFUSED";
    public const string UserDirectorySynced = "USER_DIRECTORY_SYNCED";
    public const string UserDirectoryDeprovisioned = "USER_DIRECTORY_DEPROVISIONED";
    public const string UserAuthorizationStale = "USER_AUTHORIZATION_STALE";
    public const string UserExternalIdentityResolved = "USER_EXTERNAL_IDENTITY_RESOLVED";
    public const string UserScimProvisioned = "USER_SCIM_PROVISIONED";
    public const string UserScimUpdated = "USER_SCIM_UPDATED";
    public const string UserScimDeprovisioned = "USER_SCIM_DEPROVISIONED";
    public const string ScimGroupProvisioned = "SCIM_GROUP_PROVISIONED";
    public const string ScimGroupUpdated = "SCIM_GROUP_UPDATED";
    public const string ScimGroupDeprovisioned = "SCIM_GROUP_DEPROVISIONED";
    public const string ScimGroupReactivated = "SCIM_GROUP_REACTIVATED";

    public const string DbAdminRowDeleted = "DBADMIN_ROW_DELETED";
    public const string DbAdminRowUpdated = "DBADMIN_ROW_UPDATED";
    public const string DbAdminRowsViewed = "DBADMIN_ROWS_VIEWED";
    public const string DbAdminSqlExecuted = "DBADMIN_SQL_EXECUTED";
    public const string DbAdminSqlWrite = "DBADMIN_SQL_WRITE";
    public const string DbAdminSqlWriteAttempted = "DBADMIN_SQL_WRITE_ATTEMPTED";

    public const string AuditLogExported = "AUDIT_LOG_EXPORTED";
    public const string ClusterLeadershipAcquired = "CLUSTER_LEADERSHIP_ACQUIRED";
    public const string SupportEventsExported = "SUPPORT_EVENTS_EXPORTED";
    public const string SupportLogDownloaded = "SUPPORT_LOG_DOWNLOADED";

    public const string WebhookTriggered = "WEBHOOK_TRIGGERED";

    public const string WorkflowCancelAll = "WORKFLOW_CANCEL_ALL";
    public const string WorkflowCreated = "WORKFLOW_CREATED";
    public const string WorkflowDeleted = "WORKFLOW_DELETED";
    public const string WorkflowDisabled = "WORKFLOW_DISABLED";
    public const string WorkflowDuplicated = "WORKFLOW_DUPLICATED";
    public const string WorkflowEnabled = "WORKFLOW_ENABLED";
    public const string WorkflowExported = "WORKFLOW_EXPORTED";
    public const string WorkflowExportedBulk = "WORKFLOW_EXPORTED_BULK";
    public const string WorkflowForceUnlocked = "WORKFLOW_FORCE_UNLOCKED";
    public const string WorkflowImported = "WORKFLOW_IMPORTED";
    public const string WorkflowImportedScorch = "WORKFLOW_IMPORTED_SCORCH";
    public const string WorkflowLocked = "WORKFLOW_LOCKED";
    public const string WorkflowMoved = "WORKFLOW_MOVED";
    public const string WorkflowPublished = "WORKFLOW_PUBLISHED";
    public const string WorkflowRolledBack = "WORKFLOW_ROLLED_BACK";
    public const string WorkflowStepTested = "WORKFLOW_STEP_TESTED";
    public const string WorkflowUnlocked = "WORKFLOW_UNLOCKED";
    public const string WorkflowUpdated = "WORKFLOW_UPDATED";
}
