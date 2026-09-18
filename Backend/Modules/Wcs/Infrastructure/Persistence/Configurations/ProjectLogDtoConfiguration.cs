using Contracts.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace WCSBackend.Modules.Wcs.Infrastructure.Configurations;

public class ProjectLogDtoConfiguration : IEntityTypeConfiguration<ProjectLogDto>
{
    public void Configure(EntityTypeBuilder<ProjectLogDto> builder)
    {
        builder.ToTable("project_logs");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(x => x.LogDate).HasColumnName("log_date").IsRequired();
        builder.Property(x => x.Content).HasColumnName("content").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status");
        builder.Property(x => x.Project).HasColumnName("project");
        builder.Property(x => x.Remark).HasColumnName("remark");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
    }
}