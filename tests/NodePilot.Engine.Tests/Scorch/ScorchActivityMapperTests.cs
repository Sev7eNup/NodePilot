using System.Xml.Linq;
using FluentAssertions;
using NodePilot.Engine.Scorch;
using Xunit;

namespace NodePilot.Engine.Tests.Scorch;

/// <summary>
/// Coverage for the SCOrch -> NodePilot activity mapping. Each branch corresponds
/// to a distinct SCOrch activity type the importer must recognise; the heuristic
/// fall-throughs and the final placeholder also need to round-trip cleanly so an
/// operator can fix unrecognised activities without losing the original metadata.
/// </summary>
public class ScorchActivityMapperTests
{
    private static XElement Obj(string typeName, string name = "TestActivity")
    {
        return new XElement("Object",
            new XElement("Name", name),
            new XElement("ObjectTypeName", typeName));
    }

    [Fact]
    public void Map_RunDotNetScript_BuildsRunScript()
    {
        var props = new Dictionary<string, string>
        {
            ["ScriptBody"] = "Get-Process | Select-Object -First 5",
            ["ScriptType"] = "PowerShell",
        };

        var result = ScorchActivityMapper.Map(Obj("Run .Net Script"), props);

        result.ActivityType.Should().Be("runScript");
        result.Config["script"].Should().Be("Get-Process | Select-Object -First 5");
        result.Config["timeoutSeconds"].Should().Be(300);
        result.Fallback.Should().BeFalse();
    }

    [Fact]
    public void Map_RunProgram_PicksUpFilePathArgsAndWorkingDir()
    {
        var props = new Dictionary<string, string>
        {
            ["FilePath"] = @"C:\Windows\System32\ipconfig.exe",
            ["Arguments"] = "/all",
            ["WorkingDirectory"] = @"C:\Temp",
        };

        var result = ScorchActivityMapper.Map(Obj("Run Program"), props);

        result.ActivityType.Should().Be("startProgram");
        result.Config["filePath"].Should().Be(@"C:\Windows\System32\ipconfig.exe");
        result.Config["arguments"].Should().Be("/all");
        result.Config["workingDirectory"].Should().Be(@"C:\Temp");
        result.Config["waitForExit"].Should().Be(true);
    }

    [Fact]
    public void Map_SendEmail_CapturesAllSmtpFields()
    {
        var props = new Dictionary<string, string>
        {
            ["Recipients"] = "ops@example.com",
            ["SenderAddress"] = "noreply@example.com",
            ["Subject"] = "Disk full alert",
            ["MessageContent"] = "Server X is at 95%",
            ["OutgoingServer"] = "smtp.example.com",
            ["OutgoingServerPort"] = "587",
            ["OutgoingServerEnableSsl"] = "1",
            ["MailFormat"] = "1",
        };

        var result = ScorchActivityMapper.Map(Obj("Send Email"), props);

        result.ActivityType.Should().Be("emailNotification");
        result.Config["to"].Should().Be("ops@example.com");
        result.Config["from"].Should().Be("noreply@example.com");
        result.Config["subject"].Should().Be("Disk full alert");
        result.Config["body"].Should().Be("Server X is at 95%");
        result.Config["smtpServer"].Should().Be("smtp.example.com");
        result.Config["smtpPort"].Should().Be(587);
        result.Config["smtpUseSsl"].Should().Be(true);
        result.Config["isHtml"].Should().Be(true);
    }

    [Fact]
    public void Map_MonitorDateTime_DailyInterval_BuildsCron()
    {
        var props = new Dictionary<string, string>
        {
            ["Type"] = "interval",
            ["EveryDayValue"] = "1",
        };

        var result = ScorchActivityMapper.Map(Obj("Monitor Date/Time"), props);

        result.ActivityType.Should().Be("scheduleTrigger");
        result.Config["cronExpression"].Should().Be("0 0 0 */1 * ?");
    }

    [Fact]
    public void Map_MonitorDateTime_HourlyInterval_BuildsCron()
    {
        var result = ScorchActivityMapper.Map(Obj("Monitor Date/Time"), new()
        {
            ["Type"] = "interval",
            ["EveryHourValue"] = "6",
        });

        result.Config["cronExpression"].Should().Be("0 0 */6 * * ?");
    }

    [Fact]
    public void Map_MonitorDateTime_MinuteInterval_BuildsCron()
    {
        var result = ScorchActivityMapper.Map(Obj("Monitor Date/Time"), new()
        {
            ["Type"] = "interval",
            ["EveryMinuteValue"] = "15",
        });

        result.Config["cronExpression"].Should().Be("0 */15 * * * ?");
    }

    [Fact]
    public void Map_MonitorDateTime_NoInterval_FallsBackToHourly()
    {
        var result = ScorchActivityMapper.Map(Obj("Monitor Date/Time"), new());

        result.Config["cronExpression"].Should().Be("0 0 * * * ?");
    }

    [Fact]
    public void Map_MonitorFile_ResolvesWatchType()
    {
        var props = new Dictionary<string, string>
        {
            ["DirectoryToMonitor"] = @"C:\Inbox",
            ["FileFilter"] = "*.csv",
            ["WatchType"] = "modified",
            ["IncludeSubfolders"] = "TRUE",
        };

        var result = ScorchActivityMapper.Map(Obj("Monitor File"), props);

        result.ActivityType.Should().Be("fileWatcherTrigger");
        result.Config["directory"].Should().Be(@"C:\Inbox");
        result.Config["filter"].Should().Be("*.csv");
        result.Config["watchType"].Should().Be("changed");
        result.Config["includeSubdirectories"].Should().Be(true);
    }

    [Theory]
    [InlineData("created", "created")]
    [InlineData("added", "created")]
    [InlineData("changed", "changed")]
    [InlineData("modified", "changed")]
    [InlineData("deleted", "deleted")]
    [InlineData("removed", "deleted")]
    [InlineData("garbage", "created")] // unknown defaults to created
    public void Map_MonitorFile_WatchTypeNormalisation(string raw, string expected)
    {
        var result = ScorchActivityMapper.Map(Obj("Monitor File"), new()
        {
            ["DirectoryToMonitor"] = @"C:\Inbox",
            ["WatchType"] = raw,
        });

        result.Config["watchType"].Should().Be(expected);
    }

    [Fact]
    public void Map_GetFileStatus_BuildsFileOperationExists()
    {
        var result = ScorchActivityMapper.Map(Obj("Get File Status"), new()
        {
            ["SourcePath"] = @"C:\Logs\app.log",
        });

        result.ActivityType.Should().Be("fileOperation");
        result.Config["operation"].Should().Be("exists");
        result.Config["path"].Should().Be(@"C:\Logs\app.log");
        result.Note.Should().Contain("fileOperation(exists)");
    }

    [Fact]
    public void Map_QueryDatabase_BuildsSqlActivity()
    {
        var result = ScorchActivityMapper.Map(Obj("Query Database"), new()
        {
            ["ConnectionString"] = "Server=db;Database=ops",
            ["Query"] = "SELECT TOP 1 * FROM Orders",
        });

        result.ActivityType.Should().Be("sql");
        result.Config["provider"].Should().Be("sqlserver");
        result.Config["connectionString"].Should().Be("Server=db;Database=ops");
        result.Config["query"].Should().Be("SELECT TOP 1 * FROM Orders");
    }

    [Fact]
    public void Map_WriteToDatabase_AddsMutationNote()
    {
        var result = ScorchActivityMapper.Map(Obj("Write to Database"), new()
        {
            ["Query"] = "UPDATE Orders SET ProcessedAt=GETDATE()",
        });

        result.ActivityType.Should().Be("sql");
        result.Note.Should().Contain("verify");
        result.Note.Should().Contain("mutation");
    }

    [Fact]
    public void Map_InvokeWebServices_BuildsRestApi()
    {
        var result = ScorchActivityMapper.Map(Obj("Invoke Web Services"), new()
        {
            ["URL"] = "https://api.example.com/orders",
            ["Method"] = "post",
            ["Body"] = "{}",
        });

        result.ActivityType.Should().Be("restApi");
        result.Config["url"].Should().Be("https://api.example.com/orders");
        result.Config["method"].Should().Be("POST");
        result.Config["body"].Should().Be("{}");
    }

    [Fact]
    public void Map_InvokeWebServices_DefaultMethodGet()
    {
        var result = ScorchActivityMapper.Map(Obj("Invoke Web Services"), new()
        {
            ["URL"] = "https://api.example.com/orders",
        });

        result.Config["method"].Should().Be("GET");
    }

    [Theory]
    [InlineData("start", "start")]
    [InlineData("STOP", "stop")]
    [InlineData("restart", "restart")]
    [InlineData("query", "status")] // unknown -> status
    public void Map_StartStopService_NormalisesAction(string raw, string expected)
    {
        var result = ScorchActivityMapper.Map(Obj("Start/Stop Service"), new()
        {
            ["ServiceName"] = "Spooler",
            ["Action"] = raw,
        });

        result.ActivityType.Should().Be("serviceManagement");
        result.Config["serviceName"].Should().Be("Spooler");
        result.Config["action"].Should().Be(expected);
    }

    [Fact]
    public void Map_Junction_WaitForAllTrue_BuildsWaitAll()
    {
        var result = ScorchActivityMapper.Map(Obj("Junction"), new() { ["WaitForAll"] = "TRUE" });

        result.ActivityType.Should().Be("junction");
        result.Config["mode"].Should().Be("waitAll");
    }

    [Fact]
    public void Map_Junction_WaitForAllFalse_BuildsWaitAny()
    {
        var result = ScorchActivityMapper.Map(Obj("Junction"), new() { ["WaitForAll"] = "0" });

        result.Config["mode"].Should().Be("waitAny");
    }

    [Fact]
    public void Map_InvokeRunbook_BuildsStartWorkflow()
    {
        var result = ScorchActivityMapper.Map(Obj("Invoke Runbook"), new()
        {
            ["RunbookName"] = "Cleanup-OrphanRecords",
        });

        result.ActivityType.Should().Be("startWorkflow");
        result.Config["workflowNameOrId"].Should().Be("Cleanup-OrphanRecords");
        result.Config["waitForCompletion"].Should().Be(true);
    }

    [Fact]
    public void Map_CompareValues_PreservesOperandsInLogMessage()
    {
        var result = ScorchActivityMapper.Map(Obj("Compare Values"), new()
        {
            ["ValueA"] = "{{count}}",
            ["ComparisonOperator"] = "GreaterThan",
            ["ValueB"] = "10",
        });

        result.ActivityType.Should().Be("log");
        result.Config["level"].Should().Be("info");
        var msg = result.Config["message"]?.ToString() ?? "";
        msg.Should().Contain("{{count}}");
        msg.Should().Contain("GreaterThan");
        msg.Should().Contain("10");
    }

    // ---------- heuristic fallbacks ----------

    [Fact]
    public void Map_HeuristicScript_WhenObjectTypeUnknown()
    {
        var result = ScorchActivityMapper.Map(
            Obj("CustomActivityX"),
            new() { ["ScriptBody"] = "Write-Host hi" });

        result.ActivityType.Should().Be("runScript");
        result.UsedHeuristic.Should().BeTrue();
    }

    [Fact]
    public void Map_HeuristicEmail_WhenToAndSubjectPresent()
    {
        var result = ScorchActivityMapper.Map(
            Obj("Notify"),
            new() { ["To"] = "ops@example.com", ["Subject"] = "Hi" });

        result.ActivityType.Should().Be("emailNotification");
        result.UsedHeuristic.Should().BeTrue();
    }

    [Fact]
    public void Map_HeuristicProgram_WhenFilePathPresent()
    {
        var result = ScorchActivityMapper.Map(
            Obj("Custom"),
            new() { ["FilePath"] = "tool.exe" });

        result.ActivityType.Should().Be("startProgram");
        result.UsedHeuristic.Should().BeTrue();
    }

    [Fact]
    public void Map_HeuristicRestApi_WhenUrlKeyPresent()
    {
        var result = ScorchActivityMapper.Map(
            Obj("Custom"),
            new() { ["EndpointUrl"] = "https://api.example.com" });

        result.ActivityType.Should().Be("restApi");
        result.UsedHeuristic.Should().BeTrue();
    }

    [Fact]
    public void Map_HeuristicSql_WhenQueryKeyPresent()
    {
        var result = ScorchActivityMapper.Map(
            Obj("Custom"),
            new() { ["sqlquery"] = "SELECT 1" });

        result.ActivityType.Should().Be("sql");
        result.UsedHeuristic.Should().BeTrue();
    }

    [Fact]
    public void Map_HeuristicService_WhenServiceNameKeyPresent()
    {
        var result = ScorchActivityMapper.Map(
            Obj("Custom"),
            new() { ["ServiceName"] = "Spooler" });

        result.ActivityType.Should().Be("serviceManagement");
        result.UsedHeuristic.Should().BeTrue();
    }

    // ---------- final-fallback placeholder ----------

    [Fact]
    public void Map_UnknownTypeAndProps_FallsBackToLogPlaceholder()
    {
        var result = ScorchActivityMapper.Map(
            Obj("Mystery Activity", "What Was This"),
            new() { ["UnknownPropX"] = "1" });

        result.ActivityType.Should().Be("log");
        result.Fallback.Should().BeTrue();
        result.Config["level"].Should().Be("warning");
        var fbMsg = result.Config["message"]?.ToString() ?? "";
        fbMsg.Should().Contain("Mystery Activity");
        fbMsg.Should().Contain("What Was This");
        result.Note.Should().Contain("Unrecognised SCOrch");
    }
}
