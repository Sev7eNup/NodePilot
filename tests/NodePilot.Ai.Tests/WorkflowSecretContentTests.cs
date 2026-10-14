using FluentAssertions;
using NodePilot.Core.WorkflowDefinitions;
using Xunit;

namespace NodePilot.Ai.Tests;

/// <summary>
/// Content-based inline-secret detection used by the definition redactor for values whose config
/// key is not itself secret-named (restApi headers string, body, runScript script).
/// </summary>
public sealed class WorkflowSecretContentTests
{
    [Theory]
    // Credential header lines with a literal value.
    [InlineData("Authorization: Bearer sk-live-abc123")]
    [InlineData("X-Api-Key: 0123456789")]
    [InlineData("Content-Type: application/json\nAuthorization: Basic dXNlcjpwYXNz")]
    // Provider token shapes anywhere.
    [InlineData("prefix sk_live_0123456789abcdef suffix")]
    [InlineData("AKIA0123456789ABCDEF")]
    [InlineData("ghp_0123456789abcdefghijklmnopqrstuvwxyz")]
    [InlineData("xoxb-0123456789-abcdef")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abcDEFghiJKL")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----")]
    // Quoted / JSON secret-name assignments.
    [InlineData("$apiToken = \"sk-live-9f8e7d6c5b4a\"")]
    [InlineData("{\"client_secret\":\"abcdef123456\"}")]
    [InlineData("password: 'hunter2xyz'")]
    public void LooksSecret_True_ForInlineSecrets(string value)
        => WorkflowSecretContent.LooksSecret(value).Should().BeTrue();

    [Theory]
    // The steered pattern: a globals reference is not a literal secret.
    [InlineData("Content-Type: application/json\nAuthorization: Bearer {{globals.API_TOKEN}}")]
    [InlineData("Authorization: {{globals.AUTH}}")]
    [InlineData("{\"api_key\":\"{{globals.KEY}}\"}")]
    // Benign, secret-free content.
    [InlineData("Content-Type: application/json")]
    [InlineData("Accept: application/json\nUser-Agent: NodePilot")]
    [InlineData("Get-Service | Where-Object Status -eq Running")]
    [InlineData("{\"name\":\"disk-check\",\"count\":3}")]
    [InlineData("https://api.example.com/v1/data")]
    [InlineData("Please reset the password by clicking the link.")]
    [InlineData("")]
    [InlineData("short")]
    public void LooksSecret_False_ForReferencesAndBenignContent(string value)
        => WorkflowSecretContent.LooksSecret(value).Should().BeFalse();

    [Theory]
    // Short secrets: no minimum length on the script patterns.
    [InlineData("$pw = 'abc'")]
    [InlineData("$cred = \"x\"")]
    [InlineData("$key='k'")]
    [InlineData("$dbPassword = 'abc'")]
    // SQL.
    [InlineData("CREATE LOGIN app WITH PASSWORD = 'abc'")]
    [InlineData("ALTER LOGIN app WITH PASSWORD = N'x', CHECK_POLICY = OFF")]
    [InlineData("CREATE USER app IDENTIFIED BY \"s3\"")]
    [InlineData("ALTER USER app IDENTIFIED BY tiger")]
    // Connection strings with an unquoted value.
    [InlineData("$cs = \"Server=db01;Database=x;User Id=sa;Password=abc;\"")]
    [InlineData("Data Source=db01;Pwd=x")]
    // PowerShell credential handling and parameters.
    [InlineData("$s = ConvertTo-SecureString 'P@ss' -AsPlainText -Force")]
    [InlineData("ConvertTo-SecureString -String \"x\" -AsPlainText -Force")]
    [InlineData("New-LocalUser -Name svc -Password 'abc'")]
    [InlineData("Connect-Thing -Token:\"t\"")]
    // Mixed template and literal: the literal part remains.
    [InlineData("Connect-Thing -Password 'abc{{globals.SUFFIX}}'")]
    // Everything LooksSecret catches still counts.
    [InlineData("Authorization: Bearer sk-live-abc123")]
    public void LooksSecretInScript_True_ForScriptSecrets(string value)
        => WorkflowSecretContent.LooksSecretInScript(value).Should().BeTrue();

    [Theory]
    [InlineData("Get-PSDrive C")]
    [InlineData("SELECT * FROM dbo.Jobs WHERE Status = 'Failed'")]
    [InlineData("Restart-Service -Name Spooler -Force")]
    [InlineData("$password = Read-Host -AsSecureString")]
    [InlineData("$cred = Get-Credential")]
    [InlineData("$keyPath = 'HKLM:\\SOFTWARE\\Vendor'")]
    [InlineData("ALTER LOGIN app WITH PASSWORD = @newPassword")]
    [InlineData("Connect-Thing -Password $pw")]
    [InlineData("Connect-Thing -Password '{{globals.PW}}'")]
    [InlineData("$cs = \"Server=db01;Password={{globals.DB_PW}};\"")]
    [InlineData("/c robocopy C:\\src D:\\dst /MIR")]
    [InlineData("")]
    public void LooksSecretInScript_False_ForReferencesAndBenignScripts(string value)
        => WorkflowSecretContent.LooksSecretInScript(value).Should().BeFalse();

    [Fact]
    public void CredentialHeaderNames_CoverTheKnownAuthHeaders()
        => WorkflowSecretContent.CredentialHeaderNames.Should().Contain(new[]
        {
            "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie",
            "X-Api-Key", "X-Auth-Token", "X-Webhook-Secret",
        });
}
