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

    /// <summary>
    /// The classifier used to read a SPACE as command-line evidence, so an ordinary path under
    /// "C:\Program Files\" without separate Parameters became a runScript — which is how a whole
    /// runbook import came back with its program calls turned into script nodes. `filePath` is
    /// handed to the process as a literal and only has to be fully qualified, never space-free.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Program Files\Tools\backup.exe")]
    [InlineData(@"""C:\Program Files\Tools\backup.exe""")]
    [InlineData(@"\\fileserver\tools\backup.exe")]
    public void Map_RunProgram_PathWithSpacesOrQuotesStaysAProgramCall(string program)
    {
        var result = ScorchActivityMapper.Map(Obj("Run Program"), new Dictionary<string, string> { ["Program"] = program });

        result.ActivityType.Should().Be("startProgram");
        result.Config["filePath"].Should().Be(program.Trim('"'));
        result.Config["arguments"].Should().Be("");
    }

    /// <summary>
    /// A real command line in <c>Program</c> is split: everything up to the executable extension is
    /// the path, the rest are arguments.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Windows\System32\cmd.exe /c dir", @"C:\Windows\System32\cmd.exe", "/c dir")]
    [InlineData(@"""C:\Program Files\7-Zip\7z.exe"" a out.zip", @"C:\Program Files\7-Zip\7z.exe", "a out.zip")]
    [InlineData(@"C:\Tools\import.bat -quiet", @"C:\Tools\import.bat", "-quiet")]
    public void Map_RunProgram_SplitsAnEmbeddedCommandLineIntoPathAndArguments(string program, string path, string args)
    {
        var result = ScorchActivityMapper.Map(Obj("Run Program"), new Dictionary<string, string> { ["Program"] = program });

        result.ActivityType.Should().Be("startProgram");
        result.Config["filePath"].Should().Be(path);
        result.Config["arguments"].Should().Be(args);
    }

    /// <summary>
    /// The whole point of the builder: the export already says whether an activity is an embedded
    /// script or an external call, so the node type comes from the type name and is never overridden
    /// by the shape of the value. Two earlier heuristics did override it — first a space (every path
    /// under "C:\Program Files\"), then a shell metacharacter, which fired on the '&amp;' of an
    /// ordinary powershell -Command call and on SCOrch's own field separator — and turned program
    /// calls into script nodes across whole imports.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Windows\System32\cmd.exe /c attrib -h C:\x | find ""y""")]              // pipe
    [InlineData(@"C:\Windows\System32\ipconfig.exe > C:\temp\ip.txt")]                       // redirect
    [InlineData("cmd /c dir & echo done")]                                                    // chaining
    [InlineData(@"powershell.exe -ExecutionPolicy Bypass -Command ""& 'C:\S\D.ps1'""")]       // PS call operator
    [InlineData(@"""C:\unterminated")]                                                        // unterminated quote
    [InlineData("cmd /C | attrib -h -r /s /d \"C:\\x\\*.*\"")]                                // SCOrch separator
    public void Map_RunProgram_IsNeverDegradedToARunScriptNode(string program)
    {
        var result = ScorchActivityMapper.Map(Obj("Run Program"), new Dictionary<string, string> { ["Program"] = program });

        result.ActivityType.Should().Be("startProgram");
        result.Config["filePath"].Should().NotBe("");
    }

    /// <summary>
    /// A value with no identifiable executable at its head still has to run. cmd.exe expresses
    /// pipes, redirects and chaining, and it is how SCOrch runs a command-line-mode activity itself —
    /// so the call stays a program call AND keeps working.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Windows\System32\ipconfig.exe > C:\temp\ip.txt")]   // redirect the exe cannot do
    [InlineData(@"C:\Tools\a.exe | C:\Tools\b.exe")]                      // pipe between two programs
    [InlineData(@"""C:\unterminated")]                                    // nothing delimitable
    public void Map_RunProgram_WrapsAnUnsplittableCommandLineInCmdExe(string program)
    {
        var result = ScorchActivityMapper.Map(Obj("Run Program"), new Dictionary<string, string> { ["Program"] = program });

        result.ActivityType.Should().Be("startProgram");
        result.Config["filePath"].Should().Be(@"C:\Windows\System32\cmd.exe");
        result.Config["arguments"].Should().Be($"/C {program}");
        result.Note.Should().Contain("cmd.exe /C");
    }

    /// <summary>
    /// cmd already IS the shell: everything after /c is its own command line, so a pipe there needs
    /// no second wrap. Same for a metacharacter inside a quoted argument — PowerShell's call
    /// operator is not a chain, and reading it as one is what degraded these calls before.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Windows\System32\cmd.exe /c attrib -h C:\x | find ""y""",
                @"C:\Windows\System32\cmd.exe", @"/c attrib -h C:\x | find ""y""")]
    [InlineData(@"powershell.exe -ExecutionPolicy Bypass -Command ""& 'C:\S\D.ps1'""",
                @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                @"-ExecutionPolicy Bypass -Command ""& 'C:\S\D.ps1'""")]
    public void Map_RunProgram_LeavesAShellsOwnCommandLineAlone(string program, string path, string args)
    {
        var result = ScorchActivityMapper.Map(Obj("Run Program"), new Dictionary<string, string> { ["Program"] = program });

        result.ActivityType.Should().Be("startProgram");
        result.Config["filePath"].Should().Be(path);
        result.Config["arguments"].Should().Be(args);
        result.Note.Should().NotContain("cmd.exe /C");
    }

    /// <summary>
    /// Verified against a real export: in command-line mode SCOrch writes "&lt;launcher&gt; | &lt;command&gt;",
    /// where the bar separates the two fields. As a pipe "cmd /C | attrib" would not even be valid
    /// shell syntax; read as a pipe it degraded the activity to a script node.
    /// </summary>
    [Theory]
    [InlineData("1")]      // ProgramMode says command-line mode
    [InlineData(null)]     // absent — the launcher-shaped head corroborates on its own
    public void Map_RunProgram_DropsTheScorchFieldSeparator(string? programMode)
    {
        var props = new Dictionary<string, string>
        {
            ["Program"] = @"cmd /C | attrib -h -r /s /d ""C:\Packages\*.*""",
        };
        if (programMode is not null) props["ProgramMode"] = programMode;

        var result = ScorchActivityMapper.Map(Obj("Run Program"), props);

        result.ActivityType.Should().Be("startProgram");
        result.Config["filePath"].Should().Be(@"C:\Windows\System32\cmd.exe");
        result.Config["arguments"].Should().Be(@"/C attrib -h -r /s /d ""C:\Packages\*.*""");
        result.Note.Should().Contain("field separator");
    }

    /// <summary>A genuine pipe is not a separator: the head is a program, not a launcher, so the
    /// value is left intact and runs through the cmd.exe wrap — which is what a pipe needs.</summary>
    [Fact]
    public void Map_RunProgram_DoesNotMistakeARealPipeForTheSeparator()
    {
        var result = ScorchActivityMapper.Map(
            Obj("Run Program"),
            new Dictionary<string, string> { ["Program"] = @"C:\Tools\a.exe | C:\Tools\b.exe" });

        result.ActivityType.Should().Be("startProgram");
        result.Config["arguments"].Should().Be(@"/C C:\Tools\a.exe | C:\Tools\b.exe");
        result.Note.Should().NotContain("field separator");
    }

    /// <summary>
    /// The engine rejects a relative filePath and does not search PATH, so a bare launcher name
    /// would import as a node that cannot run. Only the handful of names with exactly one right
    /// answer are completed.
    /// </summary>
    [Theory]
    [InlineData("cmd /c dir", @"C:\Windows\System32\cmd.exe", "/c dir")]
    [InlineData(@"powershell -File C:\S\D.ps1", @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", @"-File C:\S\D.ps1")]
    public void Map_RunProgram_CompletesABareLauncherToItsAbsolutePath(string program, string path, string args)
    {
        var result = ScorchActivityMapper.Map(Obj("Run Program"), new Dictionary<string, string> { ["Program"] = program });

        result.Config["filePath"].Should().Be(path);
        result.Config["arguments"].Should().Be(args);
        result.Note.Should().Contain("without a path");
    }

    /// <summary>
    /// A path that still holds a template placeholder resolves only at run time, so there is nothing
    /// to judge statically — complaining about it would put a false warning on every SCOrch call
    /// whose program path came from a runbook variable. This is the real export's robocopy activity.
    /// </summary>
    /// <remarks>
    /// The raw SCOrch marker is the case that actually occurs: the mapper runs BEFORE Published-Data
    /// is rewritten to <c>{{…}}</c>, so a program path built from a runbook variable still looks like
    /// <c>\`d.T.~Vb/{GUID}\`d.T.~Vb/\robocopy</c> here. Both forms must be exempt.
    /// </remarks>
    [Theory]
    [InlineData(@"{{globals.ToolDir}}\robocopy")]
    [InlineData(@"\`d.T.~Vb/{0ECBC87C-C745-4829-B05E-338ABCD130D9}\`d.T.~Vb/\robocopy")]
    public void Map_RunProgram_DoesNotDemandAnAbsolutePathFromAnUnresolvedReference(string program)
    {
        var props = new Dictionary<string, string>
        {
            ["Program"] = program,
            ["Parameters"] = @"C:\src D:\dst /MOVE /S",
        };

        var result = ScorchActivityMapper.Map(Obj("Run Program"), props);

        result.ActivityType.Should().Be("startProgram");
        result.Config["filePath"].Should().Be(program);
        result.Note.Should().BeNull();
    }

    /// <summary>The counterpart that must NOT move: an embedded script body stays a script node.
    /// The export distinguishes the two, and that distinction is the whole contract here.</summary>
    [Fact]
    public void Map_RunDotNetScript_WithAnEmbeddedBodyStaysARunScriptNode()
    {
        var props = new Dictionary<string, string>
        {
            ["ScriptBody"] = "$dir = 'C:\\Packages'\nRemove-Item $dir -Force",
            ["ScriptType"] = "PowerShell",
        };

        var result = ScorchActivityMapper.Map(Obj("Run .Net Script"), props);

        result.ActivityType.Should().Be("runScript");
        result.Config["script"].Should().Be("$dir = 'C:\\Packages'\nRemove-Item $dir -Force");
    }

    /// <summary>
    /// A name without a directory is still a program call, so it keeps the activity type the
    /// runbook meant — burying it in a script is the confusion this builder exists to avoid. The
    /// engine requires an absolute path, and the import report says so rather than letting the
    /// operator discover it at run time.
    /// </summary>
    [Fact]
    public void Map_RunProgram_RelativeExecutableStaysAProgramCallAndIsReported()
    {
        var result = ScorchActivityMapper.Map(Obj("Run Program"), new Dictionary<string, string> { ["Program"] = "tool.exe" });

        result.ActivityType.Should().Be("startProgram");
        result.Config["filePath"].Should().Be("tool.exe");
        result.Note.Should().Contain("fully qualified path");
    }

    /// <summary>Explicit Parameters are authoritative — the value in Program is the path, whatever
    /// it looks like, and must not be re-split behind the author's back.</summary>
    [Fact]
    public void Map_RunProgram_DoesNotResplitWhenParametersAreSetExplicitly()
    {
        var props = new Dictionary<string, string>
        {
            ["Program"] = @"""C:\Program Files\Tools\backup.exe""",
            ["Parameters"] = "/full /quiet",
        };

        var result = ScorchActivityMapper.Map(Obj("Run Program"), props);

        result.ActivityType.Should().Be("startProgram");
        result.Config["filePath"].Should().Be(@"C:\Program Files\Tools\backup.exe");
        result.Config["arguments"].Should().Be("/full /quiet");
    }

    /// <summary>
    /// The SCOrch activity carries its own relay, sender and TLS flag; emailNotification reads none
    /// of them, because NodePilot's SMTP settings are installation-wide. Writing them onto the node
    /// produced four keys the engine ignores, so the imported mail silently went somewhere else than
    /// the runbook said. They are now reported in the note instead of faked in the config.
    /// </summary>
    [Fact]
    public void Map_SendEmail_KeepsOnlyTheKeysTheActivityReads_AndReportsTheRelay()
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
        result.Config["subject"].Should().Be("Disk full alert");
        result.Config["body"].Should().Be("Server X is at 95%");
        result.Config["isHtml"].Should().Be(true);

        result.Config.Keys.Should().NotContain(["from", "smtpServer", "smtpPort", "smtpUseSsl"]);
        result.Note.Should().Contain("smtp.example.com").And.Contain("noreply@example.com");
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
        // Not "*/1" on day-of-month: an increment there restarts the cycle every month, so a
        // multi-day interval is not expressible and degrades to a plain daily fire.
        result.Config["cronExpression"].Should().Be("0 0 0 * * ?");
    }

    [Fact]
    public void Map_MonitorDateTime_HourlyInterval_BuildsCron()
    {
        var result = ScorchActivityMapper.Map(Obj("Monitor Date/Time"), new()
        {
            ["Type"] = "interval",
            ["EveryHourValue"] = "6",
        });

        result.Config["cronExpression"].Should().Be("0 0 0/6 * * ?");
    }

    [Fact]
    public void Map_MonitorDateTime_MinuteInterval_BuildsCron()
    {
        var result = ScorchActivityMapper.Map(Obj("Monitor Date/Time"), new()
        {
            ["Type"] = "interval",
            ["EveryMinuteValue"] = "15",
        });

        result.Config["cronExpression"].Should().Be("0 0/15 * * * ?");
    }

    /// <summary>
    /// An increment above 59 in a minute field makes Quartz throw when the trigger is armed, so a
    /// SCOrch interval that does not fit a minute field has to move up to the hour field. The old
    /// builder emitted "0 */90 * * * ?" here and the schedule silently never started.
    /// </summary>
    [Fact]
    public void Map_MonitorDateTime_IntervalLongerThanAnHour_StaysAValidCron()
    {
        var result = ScorchActivityMapper.Map(Obj("Monitor Date/Time"), new()
        {
            ["Type"] = "interval",
            ["EveryMinuteValue"] = "90",
        });

        var cron = result.Config["cronExpression"]!.ToString()!;
        cron.Should().Be("0 30 0/1 * * ?");
        var act = () => new Quartz.CronExpression(cron);
        act.Should().NotThrow("every emitted cron must be armable");
        result.Note.Should().Contain("approximated");
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
        result.Config["query"].Should().Be("SELECT TOP 1 * FROM Orders");

        // The SCOrch connection string routinely embeds a password, and SqlActivity requires a named
        // connectionRef unless the deployment opts out — so copying it would both persist a secret
        // in the workflow definition and fail on a default install.
        result.Config.Should().NotContainKey("connectionString");
        result.Note.Should().Contain("connectionRef");
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
    public void Map_CompareValues_BecomesADecisionWithATrueCase()
    {
        var result = ScorchActivityMapper.Map(Obj("Compare Values"), new()
        {
            ["StringToCompare"] = "{{step.param.state}}",
            ["StringTestOption"] = "2",
            ["StringToCompareTo"] = "READY",
        });

        result.ActivityType.Should().Be("decision");
        result.Disabled.Should().BeFalse();
        result.Config["defaultCaseName"].Should().Be("false");

        var cases = result.Config["cases"].Should().BeAssignableTo<List<object>>().Subject;
        var single = cases.Single().Should().BeAssignableTo<Dictionary<string, object?>>().Subject;
        single["name"].Should().Be("true");

        var condition = single["condition"].Should().BeAssignableTo<Dictionary<string, object?>>().Subject;
        condition["op"].Should().Be("==");
    }

    /// <summary>
    /// SCOrch's "matches pattern" takes a glob; NodePilot's `matches` runs a .NET regex, where
    /// <c>V9*</c> would mean "V followed by any number of 9s" rather than "starts with V9".
    /// </summary>
    [Fact]
    public void Map_CompareValues_TranslatesTheWildcardPatternIntoARegex()
    {
        var result = ScorchActivityMapper.Map(Obj("Compare Values"), new()
        {
            ["StringToCompare"] = "{{step.param.name}}",
            ["StringTestOption"] = "7",
            ["StringToCompareTo"] = "V9*",
        });

        var cases = (List<object>)result.Config["cases"]!;
        var condition = (Dictionary<string, object?>)((Dictionary<string, object?>)cases[0])["condition"]!;
        condition["op"].Should().Be("matches");
        ((Dictionary<string, object?>)condition["right"]!)["value"].Should().Be(@"^V9.*$");
    }

    /// <summary>
    /// Only options 2 and 7 are attested by a real export. Guessing another one would silently
    /// reverse a branch, so the node arrives with its operands filled in but disabled.
    /// </summary>
    [Fact]
    public void Map_CompareValues_WithAnUndecodableOperator_ArrivesDisabled()
    {
        var result = ScorchActivityMapper.Map(Obj("Compare Values"), new()
        {
            ["StringToCompare"] = "{{step.param.count}}",
            ["StringTestOption"] = "4",
            ["StringToCompareTo"] = "10",
        });

        result.ActivityType.Should().Be("decision");
        result.Disabled.Should().BeTrue();
        result.Note.Should().Contain("'4'").And.Contain("set the real operator");
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
