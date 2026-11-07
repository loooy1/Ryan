using Contracts.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace WCSBackend.Modules.Wcs.Infrastructure.Configurations;

public sealed class ProjectSectionConfiguration : IEntityTypeConfiguration<ProjectSectionDto>
{
    public void Configure(EntityTypeBuilder<ProjectSectionDto> builder)
    {
        builder.ToTable("project_sections");
        builder.HasKey(x => new { x.ProjectName, x.Key });
        builder.Property(x => x.ProjectName).HasColumnName("project_name").HasMaxLength(191).IsRequired();
        builder.Property(x => x.Key).HasColumnName("section_key").HasMaxLength(64).IsRequired();
        builder.Property(x => x.Title).HasColumnName("title").HasMaxLength(128).IsRequired();
        builder.Property(x => x.MarkdownContent).HasColumnName("markdown_content").HasColumnType("longtext").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasMaxLength(40).IsRequired();
    }
}
