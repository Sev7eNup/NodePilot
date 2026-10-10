using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Agents;

namespace NodePilot.Data;

internal static class AgentModelConfiguration
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<AgentRun>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.StepId).HasMaxLength(256).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.HasIndex(x => new { x.WorkflowExecutionId, x.StepId });
            e.HasOne(x => x.WorkflowExecution).WithMany().HasForeignKey(x => x.WorkflowExecutionId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<AgentRunEvent>(e =>
        {
            e.HasKey(x => new { x.AgentRunId, x.Sequence });
            e.Property(x => x.Kind).HasMaxLength(40).IsRequired();
            e.Property(x => x.MemberId).HasMaxLength(64);
            e.Property(x => x.ToolName).HasMaxLength(128);
            e.HasOne(x => x.AgentRun).WithMany().HasForeignKey(x => x.AgentRunId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<AgentMcpServer>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.Transport).HasMaxLength(20).IsRequired();
            e.Property(x => x.Command).HasMaxLength(1024);
            e.Property(x => x.Endpoint).HasMaxLength(2048);
            e.Property(x => x.SecretProvider).HasMaxLength(40);
            e.Property(x => x.UpdatedAt).IsConcurrencyToken();
        });
        model.Entity<AgentSkillPackage>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.Version).HasMaxLength(64).IsRequired();
            e.Property(x => x.Description).HasMaxLength(1024).IsRequired();
            e.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            e.HasIndex(x => new { x.Name, x.Version }).IsUnique();
        });
    }
}
