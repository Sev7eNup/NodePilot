using FluentAssertions;
using Moq;
using NodePilot.Scheduler.Sources;
using Quartz;
using Xunit;

namespace NodePilot.Engine.Tests.Triggers;

/// <summary>
/// Tests for the Quartz IJob shim that bridges between Quartz fires and the
/// ITriggerSource OnFire callback. Specifically: callback dispatch by key,
/// missing-callback short-circuit, and the parameter contract that downstream
/// activities rely on (firedAt / nextFireAt manual.* keys).
/// </summary>
public class ScheduleJobTests
{
    [Fact]
    public async Task Execute_InvokesCallback_WithFiredAtAndNextFireAt()
    {
        var key = $"test-job-{Guid.NewGuid()}";
        Dictionary<string, string>? captured = null;
        ScheduleJob.Register(key, p =>
        {
            captured = new Dictionary<string, string>(p);
            return Task.CompletedTask;
        });

        try
        {
            var fireTime = new DateTime(2026, 5, 9, 12, 0, 0, DateTimeKind.Utc);
            var next = new DateTime(2026, 5, 9, 12, 5, 0, DateTimeKind.Utc);
            var ctx = BuildContext(key, fireTime, next);

            await new ScheduleJob().Execute(ctx);

            captured.Should().NotBeNull();
            captured!.Should().ContainKey("firedAt");
            captured.Should().ContainKey("nextFireAt");
            captured["firedAt"].Should().Be(fireTime.ToString("O"));
            captured["nextFireAt"].Should().Be(next.ToString("O"));
        }
        finally
        {
            ScheduleJob.Unregister(key);
        }
    }

    [Fact]
    public async Task Execute_NoCallbackKey_SilentlyReturns()
    {
        // After unschedule the entry is removed from the Callbacks dict — Quartz can still
        // fire one last time depending on timing, and the job must no-op rather than throw.
        var ctx = BuildContext("not-registered", DateTime.UtcNow, DateTime.UtcNow.AddMinutes(5));

        var act = () => new ScheduleJob().Execute(ctx);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Execute_NextFireNull_LeavesEmptyString()
    {
        var key = $"test-job-{Guid.NewGuid()}";
        Dictionary<string, string>? captured = null;
        ScheduleJob.Register(key, p =>
        {
            captured = new Dictionary<string, string>(p);
            return Task.CompletedTask;
        });

        try
        {
            var ctx = BuildContext(key, DateTime.UtcNow, nextFire: null);

            await new ScheduleJob().Execute(ctx);

            captured.Should().NotBeNull();
            captured!["nextFireAt"].Should().Be("");
        }
        finally
        {
            ScheduleJob.Unregister(key);
        }
    }

    private static IJobExecutionContext BuildContext(string callbackKey, DateTime fireTime, DateTime? nextFire)
    {
        var jobDataMap = new JobDataMap();
        jobDataMap.Put("callbackKey", callbackKey);

        var jobDetail = new Mock<IJobDetail>();
        jobDetail.SetupGet(d => d.JobDataMap).Returns(jobDataMap);

        var ctx = new Mock<IJobExecutionContext>();
        ctx.SetupGet(c => c.JobDetail).Returns(jobDetail.Object);
        ctx.SetupGet(c => c.FireTimeUtc).Returns(new DateTimeOffset(DateTime.SpecifyKind(fireTime, DateTimeKind.Utc)));
        ctx.SetupGet(c => c.NextFireTimeUtc).Returns(
            nextFire.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(nextFire.Value, DateTimeKind.Utc)) : (DateTimeOffset?)null);
        return ctx.Object;
    }
}
