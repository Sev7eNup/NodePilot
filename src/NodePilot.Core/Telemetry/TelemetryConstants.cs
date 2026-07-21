namespace NodePilot.Core.Telemetry;

/// <summary>
/// Central registry of ActivitySource / Meter names and attribute keys used across NodePilot.
/// Lives in Core (constants only, zero dependencies) so that EVERY project — including
/// Remote and Data, which must not reference NodePilot.Telemetry — imports the same
/// identifiers instead of maintaining "keep in sync" string literals.
/// </summary>
public static class TelemetryConstants
{
    public const string ServiceName = "nodepilot-api";

    public static class Sources
    {
        public const string Engine = "NodePilot.Engine";
        public const string EngineActivities = "NodePilot.Engine.Activities";
        public const string Remote = "NodePilot.Remote";
        public const string Scheduler = "NodePilot.Scheduler";
        public const string Api = "NodePilot.Api";
    }

    public static class Meters
    {
        public const string Engine = "NodePilot.Engine";
        public const string Remote = "NodePilot.Remote";
        public const string Scheduler = "NodePilot.Scheduler";
        public const string Api = "NodePilot.Api";
        public const string Data = "NodePilot.Data";
    }

    public static class Attributes
    {
        public const string WorkflowId = "nodepilot.workflow.id";
        public const string WorkflowName = "nodepilot.workflow.name";
        public const string WorkflowNodeCount = "nodepilot.workflow.node.count";
        public const string WorkflowEdgeCount = "nodepilot.workflow.edge.count";
        public const string WorkflowCallDepth = "nodepilot.workflow.call_depth";
        public const string WorkflowParentExecutionId = "nodepilot.workflow.parent_execution_id";

        public const string ExecutionId = "nodepilot.execution.id";
        public const string ExecutionTrigger = "nodepilot.execution.trigger";
        public const string ExecutionInitiator = "nodepilot.execution.initiator";
        public const string ExecutionStatus = "nodepilot.execution.status";

        public const string DatabaseProvider = "nodepilot.db.provider";

        public const string SubWorkflowChildId = "nodepilot.subworkflow.child_workflow_id";
        public const string SubWorkflowChildExecutionId = "nodepilot.subworkflow.child_execution_id";
        public const string SubWorkflowWaitMode = "nodepilot.subworkflow.wait_mode";

        public const string StepId = "nodepilot.step.id";
        public const string StepName = "nodepilot.step.name";
        public const string StepActivityType = "nodepilot.step.activity_type";
        public const string StepTargetMachine = "nodepilot.step.target_machine";
        public const string StepHasCredential = "nodepilot.step.has_credential";
        public const string StepOutputVariable = "nodepilot.step.output_variable";
        public const string StepStatus = "nodepilot.step.status";
        public const string StepExitCode = "nodepilot.step.exit_code";

        public const string RemoteTarget = "nodepilot.remote.target";
        public const string RemoteTransport = "nodepilot.remote.transport";
        public const string RemoteAuth = "nodepilot.remote.auth";
        public const string RemoteScriptBytes = "nodepilot.remote.script.bytes";
        public const string RemoteTimeoutSec = "nodepilot.remote.timeout_sec";
        public const string RemoteStdoutBytes = "nodepilot.remote.stdout.bytes";
        public const string RemoteStderrBytes = "nodepilot.remote.stderr.bytes";

        public const string TriggerType = "nodepilot.trigger.type";

        // AI/LLM
        public const string LlmModel = "nodepilot.llm.model";
        public const string LlmKind = "nodepilot.llm.kind";
        public const string LlmErrorKind = "nodepilot.llm.error_kind";

        // Rate limiting
        public const string RateLimitPolicy = "nodepilot.rate_limit.policy";

        // Credential DPAPI
        public const string CredentialOperation = "nodepilot.credential.operation";
        public const string CredentialResult = "nodepilot.credential.result";

        // Retention
        public const string RetentionService = "nodepilot.retention.service";

        // Workflow lifecycle
        public const string WorkflowOperation = "nodepilot.workflow.operation";

        // Import/export
        public const string ImportExportOperation = "nodepilot.import_export.operation";
        public const string ImportExportResult = "nodepilot.import_export.result";

        // Machine test
        public const string MachineTestResult = "nodepilot.machine.test.result";

        // Dispatch
        public const string DispatchResult = "nodepilot.dispatch.result";
    }
}
