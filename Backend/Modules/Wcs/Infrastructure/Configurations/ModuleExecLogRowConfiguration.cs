using GrcsBackend.Contracts.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GrcsBackend.Modules.Wcs.Infrastructure.Configurations;

public class ModuleExecLogRowConfiguration : IEntityTypeConfiguration<ModuleExecLogRow>
{
    public void Configure(EntityTypeBuilder<ModuleExecLogRow> builder)
    {
        builder.ToTable("module_exec_logs");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(x => x.TaskId).HasColumnName("task_id");
        builder.Property(x => x.Point).HasColumnName("point");
        builder.Property(x => x.Module).HasColumnName("module");
        builder.Property(x => x.Ok).HasColumnName("ok");
        builder.Property(x => x.HttpCode).HasColumnName("http_code");
        builder.Property(x => x.Detail).HasColumnName("detail");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
    }
}