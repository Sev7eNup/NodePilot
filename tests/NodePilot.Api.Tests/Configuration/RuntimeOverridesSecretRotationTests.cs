using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Api.Configuration;
using NodePilot.Core.Interfaces;
using NodePilot.Data.Security;
using Xunit;

namespace NodePilot.Api.Tests.Configuration;

public sealed class RuntimeOverridesSecretRotationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nodepilot-rotation-" + Guid.NewGuid().ToString("N"));
    private readonly AesGcmSecretProtector _old = new(Enumerable.Repeat((byte)19, 32).ToArray());
    private readonly AesGcmSecretProtector _current = new(Enumerable.Repeat((byte)27, 32).ToArray());
    private readonly RuntimeOverridesWriter _writer;

    public RuntimeOverridesSecretRotationTests()
    {
        Directory.CreateDirectory(_directory);
        _writer = new RuntimeOverridesWriter(Path.Combine(_directory, "runtime.json"), NullLogger<RuntimeOverridesWriter>.Instance);
    }

    [Fact]
    public void Rotation_ResealsActiveAndRollbackFiles_IncludingArrayValues()
    {
        var backup = _writer.OverridesPath + ".bak.20260101";
        var json = new JsonObject
        {
            ["Smtp"] = new JsonObject { ["Password"] = Encrypt("password") },
            ["Profiles"] = new JsonArray(new JsonObject { ["ApiKey"] = Encrypt("api-key") }),
            ["Unrelated"] = 42,
        }.ToJsonString();
        File.WriteAllText(_writer.OverridesPath, json);
        File.WriteAllText(backup, json);
        var backupTime = DateTime.UtcNow.AddDays(-4);
        File.SetLastWriteTimeUtc(backup, backupTime);

        var result = _writer.ReencryptSecrets(new MigratingSecretProtector(_current, _old), CancellationToken.None);

        result.Rewritten.Should().Be(2);
        result.Skipped.Should().Be(0);
        Directory.GetFiles(_directory).Should().HaveCount(2, "rotation must not create an old-key rollback file");
        foreach (var file in Directory.GetFiles(_directory))
        {
            var root = JsonNode.Parse(File.ReadAllText(file))!;
            Decrypt(root["Smtp"]!["Password"]!.GetValue<string>()).Should().Be("password");
            Decrypt(root["Profiles"]![0]!["ApiKey"]!.GetValue<string>()).Should().Be("api-key");
            root["Unrelated"]!.GetValue<int>().Should().Be(42);
        }
        File.GetLastWriteTimeUtc(backup).Should().Be(backupTime);
    }

    [Fact]
    public void Rotation_BadBackupReportsSafeSkip_AndPreservesThatWholeFile()
    {
        File.WriteAllText(_writer.OverridesPath, new JsonObject { ["secret"] = Encrypt("good") }.ToJsonString());
        var backup = _writer.OverridesPath + ".bak.bad";
        var broken = new JsonObject { ["first"] = Encrypt("first"), ["second"] = "enc:v1:not-base64" }.ToJsonString();
        File.WriteAllText(backup, broken);

        var result = _writer.ReencryptSecrets(new MigratingSecretProtector(_current, _old), CancellationToken.None);

        result.Rewritten.Should().Be(1);
        result.Skipped.Should().Be(1);
        result.SkippedDetails.Should().ContainSingle(x => x.Name == "runtime.json.bak.bad" && x.Reason == "FormatException");
        File.ReadAllText(backup).Should().Be(broken);
        Directory.GetFiles(_directory, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void Rotation_CancellationAfterDecrypt_DoesNotWritePartialFile()
    {
        var original = new JsonObject { ["secret"] = Encrypt("good") }.ToJsonString();
        File.WriteAllText(_writer.OverridesPath, original);
        using var cts = new CancellationTokenSource();
        var protector = new Mock<ISecretProtector>();
        protector.Setup(x => x.Unprotect(It.IsAny<byte[]>())).Returns((byte[] bytes) => { cts.Cancel(); return _old.Unprotect(bytes); });
        protector.Setup(x => x.Protect(It.IsAny<string>())).Returns((string value) => _current.Protect(value));

        var act = () => _writer.ReencryptSecrets(protector.Object, cts.Token);

        act.Should().Throw<OperationCanceledException>();
        File.ReadAllText(_writer.OverridesPath).Should().Be(original);
    }

    [Fact]
    public async Task Rotation_ConcurrentSettingsSaveWaits_ThenUsesResealedDocumentAndBackup()
    {
        File.WriteAllText(_writer.OverridesPath, new JsonObject { ["secret"] = Encrypt("good") }.ToJsonString());
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        using var saveStarted = new ManualResetEventSlim();
        var protector = new Mock<ISecretProtector>();
        protector.Setup(x => x.Unprotect(It.IsAny<byte[]>())).Returns((byte[] bytes) =>
        {
            entered.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("test barrier");
            return _old.Unprotect(bytes);
        });
        protector.Setup(x => x.Protect(It.IsAny<string>())).Returns((string value) => _current.Protect(value));
        var rotation = Task.Run(() => _writer.ReencryptSecrets(protector.Object, CancellationToken.None));
        Task? save = null;
        try
        {
            entered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            save = Task.Run(() => { saveStarted.Set(); _writer.MutateAndWrite(root => root["Unrelated"] = 99); });
            saveStarted.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            resume.Set();
            (await rotation.WaitAsync(TimeSpan.FromSeconds(10))).Skipped.Should().Be(0);
            await save.WaitAsync(TimeSpan.FromSeconds(10));
            _writer.ReadOrEmpty()["Unrelated"]!.GetValue<int>().Should().Be(99);
            foreach (var file in Directory.GetFiles(_directory))
                Decrypt(JsonNode.Parse(File.ReadAllText(file))!["secret"]!.GetValue<string>()).Should().Be("good");
        }
        finally
        {
            resume.Set();
            await rotation.WaitAsync(TimeSpan.FromSeconds(10));
            if (save is not null) await save.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private string Encrypt(string value) => EncryptingJsonConfigurationProvider.EncryptForPersist(value, _old);
    private string Decrypt(string value) => _current.Unprotect(Convert.FromBase64String(
        value[EncryptingJsonConfigurationProvider.EncryptedValuePrefix.Length..]));

    public void Dispose()
    {
        foreach (var file in Directory.GetFiles(_directory)) File.Delete(file);
        Directory.Delete(_directory);
    }
}
