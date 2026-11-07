using Contracts.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace WCSBackend.Modules.Wcs.Infrastructure.Configurations;

public sealed class ProjectWorkspaceConfiguration : IEntityTypeConfiguration<ProjectWorkspaceDto>
{
    public void Configure(EntityTypeBuilder<ProjectWorkspaceDto> builder)
    {
        builder.ToTable("project_workspaces");
        builder.HasKey(x => x.Name);
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(191).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasMaxLength(40).IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasMaxLength(40).IsRequired();
    }
}
