using System.Text.Json;
using GrcsBackend.Contracts.Entities;
using GrcsBackend.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GrcsBackend.Modules.Wcs.Infrastructure.Configurations;

public class FeatureModuleRowConfiguration : IEntityTypeConfiguration<FeatureModuleRow>
{
    public void Configure(EntityTypeBuilder<FeatureModuleRow> builder)
    {
        builder.ToTable("feature_modules");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("module_id").IsRequired();
        builder.Property(x => x.Name).HasColumnName("name");
        builder.Property(x => x.ApiUrl).HasColumnName("api_url");
        builder.Property(x => x.Params).HasColumnName("params_json")
            .HasConversion(
                v => JsonSerializer.Serialize(v),
                v => string.IsNullOrWhiteSpace(v) ? new List<WorkParamDto>() : JsonSerializer.Deserialize<List<WorkParamDto>>(v) ?? new List<WorkParamDto>())
            .Metadata.SetValueComparer(ValueComparer.CreateDefault<List<WorkParamDto>>(favorStructuralComparisons: false));
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
    }
}