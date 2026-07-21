using FluentAssertions;
using NodePilot.Engine.Telemetry;
using Xunit;

namespace NodePilot.Engine.Tests.Telemetry;

/// <summary>
/// Allow-list contract for which output-parameters may surface as OTel span tags.
/// The list is the safety boundary that prevents user-supplied parameter values
/// (which can contain secrets) from leaking via telemetry exporters.
/// </summary>
public class ActivityTelemetryAllowListTests
{
    [Theory]
    [InlineData("startProgram", "exitCode")]
    [InlineData("startProgram", "processId")]
    [InlineData("restApi", "status")]
    [InlineData("restApi", "statusCode")]
    [InlineData("restApi", "method")]
    [InlineData("restApi", "proxyMode")]
    [InlineData("sql", "rowCount")]
    [InlineData("sql", "rowsAffected")]
    [InlineData("sql", "provider")]
    [InlineData("xmlQuery", "count")]
    [InlineData("jsonQuery", "count")]
    [InlineData("fileHash", "algorithm")]
    [InlineData("fileHash", "match")]
    [InlineData("decision", "case")]
    [InlineData("decision", "matched")]
    [InlineData("decision", "reason")]
    [InlineData("scheduledTask", "state")]
    [InlineData("scheduledTask", "lastTaskResult")]
    [InlineData("scheduledTask", "action")]
    [InlineData("zipOperation", "sizeBytes")]
    [InlineData("zipOperation", "operation")]
    [InlineData("fileOperation", "operation")]
    [InlineData("fileOperation", "exists")]
    [InlineData("folderOperation", "count")]
    [InlineData("serviceManagement", "status")]
    [InlineData("registryOperation", "exists")]
    [InlineData("registryOperation", "type")]
    [InlineData("powerManagement", "action")]
    [InlineData("startWorkflow", "waited")]
    [InlineData("startWorkflow", "__status")]
    [InlineData("junction", "mode")]
    [InlineData("junction", "satisfied")]
    public void IsExposable_AllowedPair_ReturnsTrue(string activityType, string parameter)
    {
        ActivityTelemetryAllowList.IsExposable(activityType, parameter).Should().BeTrue();
    }

    [Theory]
    [InlineData("STARTPROGRAM", "EXITCODE")]
    [InlineData("RestApi", "Status")]
    public void IsExposable_CaseInsensitive(string activityType, string parameter)
    {
        ActivityTelemetryAllowList.IsExposable(activityType, parameter).Should().BeTrue();
    }

    [Theory]
    // Free-text body / output is the prime target the allow-list must keep out.
    [InlineData("restApi", "body")]
    [InlineData("restApi", "headers")]
    [InlineData("sql", "query")]
    [InlineData("sql", "rows")]
    [InlineData("startProgram", "stdout")]
    [InlineData("startProgram", "stderr")]
    [InlineData("runScript", "output")]      // entire activity-type intentionally absent
    [InlineData("runScript", "exitCode")]
    [InlineData("emailNotification", "body")] // entire activity-type intentionally absent
    [InlineData("emailNotification", "to")]
    public void IsExposable_NonAllowed_ReturnsFalse(string activityType, string parameter)
    {
        ActivityTelemetryAllowList.IsExposable(activityType, parameter).Should().BeFalse();
    }

    [Theory]
    [InlineData("", "exitCode")]
    [InlineData("startProgram", "")]
    [InlineData(null, "exitCode")]
    [InlineData("startProgram", null)]
    public void IsExposable_NullOrEmpty_ReturnsFalse(string? activityType, string? parameter)
    {
        ActivityTelemetryAllowList.IsExposable(activityType!, parameter!).Should().BeFalse();
    }

    [Fact]
    public void IsExposable_UnknownActivityType_ReturnsFalse()
    {
        ActivityTelemetryAllowList.IsExposable("unknownActivity", "anything").Should().BeFalse();
    }
}
