using System.Text.Json;
using GrcsBackend.Contracts.Entities;
using GrcsBackend.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GrcsBackend.Modules.Wcs.Infrastructure.Configurations;

public class AutoTemplateRowConfiguration : IEntityTypeConfiguration<AutoTemplateRow>
{
    public void Configure(EntityTypeBuilder<AutoTemplateRow> builder)
    {
        builder.ToTable("auto_templates");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("template_id").IsRequired();
        builder.Property(x => x.Name).HasColumnName("name");
        builder.Property(x => x.Steps).HasColumnName("steps_json")
            .HasConversion(
                v => JsonSerializer.Serialize(v),
                v => string.IsNullOrWhiteSpace(v) ? new List<AutoStepDto>() : JsonSerializer.Deserialize<List<AutoStepDto>>(v) ?? new List<AutoStepDto>())
            .Metadata.SetValueComparer(ValueComparer.CreateDefault<List<AutoStepDto>>(favorStructuralComparisons: false));
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
    }
}