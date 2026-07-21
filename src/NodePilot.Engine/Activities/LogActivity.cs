using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Security;

namespace NodePilot.Engine.Activities;

/// <summary>
/// Writes a user-authored log entry at the chosen level (info/warning/error) through the
/// standard <see cref="ILogger"/> pipeline — so entries land in the Rolling-File sink,
/// the Console sink, and (when configured) the OTLP log exporter. Enriched with
/// workflow_execution_id / step_id / activity scope so Serilog's <c>{Properties:j}</c>
/// token renders them.
///
/// Additionally mirrors the message into <c>ActivityResult.Output</c> so the UI's
/// Execution Panel shows it next to the step.
///
/// Config:
///   level    "info" | "warning" | "error"   (default "info")
///   message  string, required               (supports {{templates}} — resolved by engine)
/// </summary>
public class LogActivity : IActivityExecutor
{
    private readonly ILogger<LogActivity> _logger;
    private readonly OutputRedactor _redactor;

    public string ActivityType => "log";

    // Caps on a user-authored log line: the message lands in the rolling log file and in
    // downstream variables, so an unbounded string is both a log-volume and a variable-
    // propagation risk.
    private const int MaxMessageChars = 8 * 1024;

    public LogActivity(ILogger<LogActivity> logger, OutputRedactor redactor)
    {
        _logger = logger;
        _redactor = redactor;
    }

    public Task<ActivityResult> ExecuteAsync(StepExecutionContext context, JsonElement config, CancellationToken ct)
        => ActivityExecution.RunAsync(() =>
        {
            var level = config.GetStringOrNull("level")?.ToLowerInvariant() ?? "info";
            var rawMessage = config.GetStringOrNull("message");

            if (string.IsNullOrWhiteSpace(rawMessage))
            {
                return Task.FromResult(new ActivityResult
                {
                    Success = false,
                    ErrorOutput = "Log: 'message' is required",
                });
            }

            // Sanitize before the message touches any logger: strip CR/LF (log-forging), drop
            // ANSI escape sequences (parser-confusion), trim to MaxMessageChars, then run
            // through the secret redactor so a `message: "pw={{step.param.pw}}"` won't leak.
            var sanitized = SanitizeForLog(rawMessage);
            if (sanitized.Length > MaxMessageChars)
                sanitized = sanitized[..MaxMessageChars] + "…(truncated)";
            var message = _redactor.Redact(sanitized) ?? sanitized;

            // SupportLog=true: user-authored log lines are by definition operator-relevant —
            // this scope property is filtered on by the sub-sink in LoggingSetup, which writes
            // the entry additionally into nodepilot-support-*.log.
            //
            // The workflow and step identifiers are placed directly in the message template
            // (not just in the scope dictionary) so the plain-text SupportLogFormatter renders
            // them into the line — without these fields the support log wouldn't show which
            // workflow wrote the entry.
            var execShort = context.WorkflowExecutionId.ToString("N")[..8];
            var workflowName = string.IsNullOrEmpty(context.WorkflowName) ? "-" : context.WorkflowName;
            var stepLabel = string.IsNullOrEmpty(context.StepLabel) ? context.StepId : context.StepLabel;
            using (_logger.BeginScope(new Dictionary<string, object>
            {
                ["workflow_execution_id"] = context.WorkflowExecutionId,
                ["workflow_name"] = workflowName,
                ["step_id"] = context.StepId,
                ["step_label"] = stepLabel,
                ["activity"] = "log",
                ["activity_type"] = "log",
                ["user_log_level"] = level,
                ["SupportLog"] = true,
                // Event-type discriminator for the DB sink — it projects this into the
                // `EventType` column for indexed filtering.
                ["support.event_type"] = "USER_LOG",
                // Clean message for the DB projection: ONLY the wording, without the
                // workflow/exec/step prefix (those already land in their own columns).
                // The plain-text file sink still renders the full template from the
                // LogInformation call below.
                ["support.message"] = message,
            }))
            {
                switch (level)
                {
                    case "warning":
                        _logger.LogWarning("USER-LOG workflow={WorkflowName} exec={ExecutionShort} step={StepLabel} | {Message}",
                            workflowName, execShort, stepLabel, message);
                        break;
                    case "error":
                        _logger.LogError("USER-LOG workflow={WorkflowName} exec={ExecutionShort} step={StepLabel} | {Message}",
                            workflowName, execShort, stepLabel, message);
                        break;
                    default:
                        _logger.LogInformation("USER-LOG workflow={WorkflowName} exec={ExecutionShort} step={StepLabel} | {Message}",
                            workflowName, execShort, stepLabel, message);
                        break;
                }
            }

            return Task.FromResult(new ActivityResult
            {
                Success = true,
                Output = message,
                OutputParameters = new Dictionary<string, string>
                {
                    ["level"] = level,
                    ["message"] = message,
                },
            });
        }, ex => $"Log error: {ex.Message}");

    // Replaces CR/LF with spaces (prevents log-forging / fake multi-line entries) and
    // strips ANSI CSI escape sequences (ESC [ ... letter) that would otherwise render
    // as control characters in log viewers or reset a terminal watching tail -f.
    private static string SanitizeForLog(string s)
    {
        var sb = new StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '\r' || c == '\n') { sb.Append(' '); i++; continue; }
            // ESC (0x1B) introduces ANSI CSI — consume until a letter terminator
            if (c == 0x1B && i + 1 < s.Length && s[i + 1] == '[')
            {
                i += 2;
                while (i < s.Length && !(s[i] >= '@' && s[i] <= '~')) i++;
                if (i < s.Length) i++; // skip the terminator letter itself
                continue;
            }
            // Drop other C0 control chars except tab
            if (c != '\t' && c < 0x20) { i++; continue; }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }
}
