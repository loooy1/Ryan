using Contracts.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace WCSBackend.Modules.Wcs.Infrastructure.Configurations;

public class ModuleExecLogRowConfiguration : IEntityTypeConfiguration<ModuleExecLogRow>
{
    public void Configure(EntityTypeBuilder<ModuleExecLogRow> builder)
    {
        builder.ToTable("module_exec_logs");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(x => x.TaskId).HasColumnName("task_id").IsRequired();
        builder.Property(x => x.ModuleId).HasColumnName("module_id").IsRequired();
        builder.Property(x => x.ModuleName).HasColumnName("module_name").IsRequired();
        builder.Property(x => x.ExecutionPhase).HasColumnName("execution_phase").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").IsRequired();
        builder.Property(x => x.HttpStatus).HasColumnName("http_status");
        builder.Property(x => x.DetailJson).HasColumnName("detail_json").IsRequired();
        builder.Property(x => x.StartedAt).HasColumnName("started_at").IsRequired();
        builder.Property(x => x.FinishedAt).HasColumnName("finished_at");
        builder.HasIndex(x => new { x.TaskId, x.StartedAt }).HasDatabaseName("idx_module_exec_logs_task_time");
        builder.HasIndex(x => x.StartedAt).HasDatabaseName("idx_module_exec_logs_started_at");
    }
}
