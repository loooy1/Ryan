using System.Text.Json;
using Contracts.Entities;
using Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace WCSBackend.Modules.Wcs.Infrastructure.Configurations;

public class TaskTemplateRowConfiguration : IEntityTypeConfiguration<TaskTemplateRow>
{
    private static TaskPointDto ReadPoint(string json) =>
        string.IsNullOrWhiteSpace(json) ? new() : JsonSerializer.Deserialize<TaskPointDto>(json)!;

    public void Configure(EntityTypeBuilder<TaskTemplateRow> builder)
    {
        builder.ToTable("task_templates");
        builder.HasKey(x => x.Value);
        builder.Property(x => x.Value).HasColumnName("value").IsRequired();
        builder.Property(x => x.Label).HasColumnName("label");
        builder.Property(x => x.Description).HasColumnName("description");
        builder.Property(x => x.Category).HasColumnName("category");
        builder.Property(x => x.NeedsContainer).HasColumnName("needs_container");
        builder.Property(x => x.ContainerPrefix).HasColumnName("container_prefix");
        builder.Property(x => x.RandomContainer).HasColumnName("random_container");
        builder.Property(x => x.Start).HasColumnName("start_json")
            .HasConversion(v => JsonSerializer.Serialize(v), v => ReadPoint(v));
        builder.Property(x => x.End).HasColumnName("end_json")
            .HasConversion(v => JsonSerializer.Serialize(v), v => ReadPoint(v));
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
    }
}