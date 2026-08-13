using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using NodePilot.Core.Telemetry;

namespace NodePilot.Telemetry;

internal sealed record TelemetryResourceIdentity(
    string ServiceName,
    string ServiceVersion,
    string ServiceInstanceId,
    string DeploymentEnvironment,
    string Hostname,
    bool RedactHostnames);

internal static class TelemetryResourceIdentityFactory
{
    private static readonly Lazy<string> RedactedProcessInstanceId = new(
        static () => $"nodepilot-{Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}",
        LazyThreadSafetyMode.ExecutionAndPublication);

    internal static TelemetryResourceIdentity Create(
        NodePilotTelemetryOptions options,
        IHostEnvironment environment,
        string hostname,
        int processId)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        var serviceName = string.IsNullOrWhiteSpace(options.ServiceName)
            ? TelemetryConstants.ServiceName
            : options.ServiceName;
        var serviceVersion = typeof(OpenTelemetryExtensions).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        var deploymentEnvironment = options.Environment ?? environment.EnvironmentName;
        var serviceInstanceId = options.RedactHostnames
            ? RedactedProcessInstanceId.Value
            : $"{hostname}:{processId}";

        return new TelemetryResourceIdentity(
            serviceName,
            serviceVersion,
            serviceInstanceId,
            deploymentEnvironment,
            options.RedactHostnames ? string.Empty : hostname,
            options.RedactHostnames);
    }
}
