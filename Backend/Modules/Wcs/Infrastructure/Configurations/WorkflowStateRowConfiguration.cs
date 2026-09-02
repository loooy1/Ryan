using GrcsBackend.Contracts.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GrcsBackend.Modules.Wcs.Infrastructure.Configurations;

public class WorkflowStateRowConfiguration : IEntityTypeConfiguration<WorkflowStateRow>
{
    public void Configure(EntityTypeBuilder<WorkflowStateRow> builder)
    {
        builder.ToTable("workflow_state");
        builder.HasKey(x => new { x.Kind, x.TaskId });
        builder.Property(x => x.Kind).HasColumnName("kind").IsRequired();
        builder.Property(x => x.TaskId).HasColumnName("task_id").IsRequired();
        builder.Property(x => x.Value).HasColumnName("value");
        builder.Property(x => x.Time).HasColumnName("time");
    }
}