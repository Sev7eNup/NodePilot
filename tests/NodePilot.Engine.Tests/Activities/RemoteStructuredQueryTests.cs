using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Engine.Activities;
using NodePilot.Engine.Tests.Helpers;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

public sealed class RemoteStructuredQueryTests
{
    private static readonly IConfiguration EmptyConfig = new ConfigurationBuilder().Build();

    [Theory]
    [InlineData("jsonQuery", "{\"name\":\"O'Brien\"}", "$.name", "single", "O'Brien", 1)]
    [InlineData("jsonQuery", "{\"n\":9.99}", "$.n", "single", "9.99", 1)]
    [InlineData("jsonQuery", "{\"b\":true}", "$.b", "single", "true", 1)]
    [InlineData("jsonQuery", "{\"n\":null}", "$.n", "single", "", 1)]
    [InlineData("jsonQuery", "{}", "$.missing", "single", "", 0)]
    [InlineData("jsonQuery", "{}", "$.missing", "all", "[]", 0)]
    [InlineData("jsonQuery", "{\"x\":\"2026-10-20T12:00:00Z\"}", "$.x", "single", "2026-10-20T12:00:00Z", 1)]
    [InlineData("jsonQuery", "[{\"n\":1},{\"n\":2}]", "$[?(@.n > 1)].n", "all", "[2]", 1)]
    [InlineData("jsonQuery", "[1,2]", "$", "single", "[1,2]", 1)]
    [InlineData("jsonQuery", "{\"obj\":{\"a\":1}}", "$.obj", "single", "{\"a\":1}", 1)]
    [InlineData("jsonQuery", "{\"obj\":{\"Type\":\"Null\"}}", "$.obj", "single", "{\"Type\":\"Null\"}", 1)]
    [InlineData("jsonQuery", "[1,2]", "$[*]", "all", "[1,2]", 2)]
    [InlineData("jsonQuery", "{\"x\":[]}", "$.x", "single", "[]", 1)]
    [InlineData("jsonQuery", "{\"x\":[null,{\"a\":1},[2,3]]}", "$.x[*]", "all", "[null,{\"a\":1},[2,3]]", 3)]
    [InlineData("xmlQuery", "<r><n>a</n><n>b</n></r>", "/r/n", "single", "a", 1)]
    [InlineData("xmlQuery", "<r><n>a</n><n>b</n></r>", "/r/n", "all", "[\"a\",\"b\"]", 2)]
    [InlineData("xmlQuery", "<r><n>a</n></r>", "/r/n", "all", "[\"a\"]", 1)]
    [InlineData("xmlQuery", "<r/>", "/r/n", "all", "[]", 0)]
    [InlineData("xmlQuery", "<r/>", "/r/n", "single", "", 0)]
    [InlineData("xmlQuery", "<r><n>9.99</n></r>", "sum(/r/n)", "single", "9.99", 1)]
    [InlineData("xmlQuery", "<r/>", "boolean(/r)", "single", "true", 1)]
    [InlineData("xmlQuery", "<r xmlns='urn:test'><n>x</n></r>", "/t:r/t:n", "single", "x", 1)]
    public async Task WindowsPowerShell51_PreservesQueryContract(
        string type, string content, string query, string mode, string expected, int count)
    {
        var config = JsonSerializer.SerializeToElement(new
        {
            source = "inline", content, jsonPath = query, xpath = query, resultMode = mode,
            namespaces = new Dictionary<string, string> { ["t"] = "urn:test" },
        });
        var result = await RunScript(RemoteStructuredQuery.BuildScript(config, type, EmptyConfig));
        result.ExitCode.Should().Be(0, result.Error);
        using var document = JsonDocument.Parse(result.Output);
        document.RootElement.GetProperty("result").GetString().Should().Be(expected);
        document.RootElement.GetProperty("count").GetInt32().Should().Be(count);
    }

    [Theory]
    [InlineData("jsonQuery", "{broken", "$")]
    [InlineData("jsonQuery", "[1,2]", "$[*]")]
    [InlineData("xmlQuery", "<!DOCTYPE r [<!ENTITY e SYSTEM 'file:///C:/Windows/win.ini'>]><r>&e;</r>", "/r")]
    [InlineData("xmlQuery", "<r/>", "[[invalid")]
    public async Task WindowsPowerShell51_RejectsInvalidQueriesAndDtd(string type, string content, string query)
    {
        var config = JsonSerializer.SerializeToElement(new { content, jsonPath = query, xpath = query });
        var result = await RunScript(RemoteStructuredQuery.BuildScript(config, type, EmptyConfig));
        result.ExitCode.Should().NotBe(0);
    }

    [Theory]
    [InlineData("jsonQuery", "{\"name\":\"from-file\"}", "$.name")]
    [InlineData("xmlQuery", "<r>from-file</r>", "/r")]
    public async Task WindowsPowerShell51_ReadsTargetFileAndEnforcesAllowedRoots(string type, string content, string query)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, content);
            var config = JsonSerializer.SerializeToElement(new { source = "file", path, jsonPath = query, xpath = query });
            var result = await RunScript(RemoteStructuredQuery.BuildScript(config, type, EmptyConfig));
            result.ExitCode.Should().Be(0, result.Error);
            using var document = JsonDocument.Parse(result.Output);
            document.RootElement.GetProperty("result").GetString().Should().Be("from-file");

            var restricted = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["FileSystemOperation:AllowedRoots:0"] = @"C:\NotThisRoot" }).Build();
            var denied = await RunScript(RemoteStructuredQuery.BuildScript(config, type, restricted));
            denied.ExitCode.Should().NotBe(0);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("jsonQuery")]
    [InlineData("xmlQuery")]
    public async Task Activity_UsesTargetDefaultCredentialTimeoutAndCancellation(string type)
    {
        using var db = TestDbContext.Create();
        var credential = new Credential { Id = Guid.NewGuid(), Name = "query", Username = "operator", EncryptedPassword = [1] };
        var machine = new ManagedMachine { Id = Guid.NewGuid(), Name = "target", Hostname = "192.0.2.10", DefaultCredentialId = credential.Id };
        db.ManagedMachines.Add(machine);
        db.Credentials.Add(credential);
        await db.SaveChangesAsync();
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var credentials = new Mock<ICredentialStore>();
        credentials.Setup(c => c.GetAsync(credential.Id, token)).ReturnsAsync(credential);
        var session = new Mock<IRemoteSession>();
        session.Setup(s => s.ExecuteScriptAsync(It.IsAny<string>(), 17, token)).ReturnsAsync(new RemoteExecutionResult
        { Success = true, Output = "{\"result\":\"remote\",\"count\":1}" });
        session.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var factory = new Mock<IRemoteSessionFactory>();
        factory.Setup(f => f.CreateSessionAsync(machine, credential, token)).ReturnsAsync(session.Object);
        var remote = new RemoteStructuredQuery(factory.Object, credentials.Object, db, EmptyConfig);
        IActivityExecutor activity = type == "jsonQuery" ? new JsonQueryActivity(EmptyConfig, remote) : new XmlQueryActivity(EmptyConfig, remote);
        var config = JsonSerializer.SerializeToElement(new { source = "file", path = @"C:\RemoteOnly\data.txt", jsonPath = "$", xpath = "/r", timeoutSeconds = 17 });
        var result = await activity.ExecuteAsync(new StepExecutionContext { TargetMachineId = machine.Id }, config, token);
        result.Success.Should().BeTrue(result.ErrorOutput);
        result.OutputParameters.Should().Contain("result", "remote").And.Contain("count", "1");
        factory.VerifyAll();
        session.VerifyAll();
        credentials.VerifyAll();
    }

    [Fact]
    public async Task WindowsPowerShell51_RepeatedQueriesReuseBundledAssembly()
    {
        var config = JsonSerializer.SerializeToElement(new { content = "{\"x\":1}", jsonPath = "$.x" });
        var query = RemoteStructuredQuery.BuildScript(config, "jsonQuery", EmptyConfig);
        var script = "$null = & {\n" + query + "\n}\n$null = & {\n" + query +
            "\n}\n@([AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -eq 'Newtonsoft.Json' }).Count";
        var result = await RunScript(script);
        result.ExitCode.Should().Be(0, result.Error);
        result.Output.Trim().Should().Be("1");
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunScript(string script)
    {
        var path = Path.Combine(Path.GetTempPath(), $"np-query-{Guid.NewGuid():N}.ps1");
        try
        {
            await File.WriteAllTextAsync(path, "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n[System.Threading.Thread]::CurrentThread.CurrentCulture = 'de-DE'\n" + script, new UTF8Encoding(false));
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe", UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path },
            }) ?? throw new InvalidOperationException("PowerShell did not start");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            return (process.ExitCode, await output, await error);
        }
        finally { File.Delete(path); }
    }
}
