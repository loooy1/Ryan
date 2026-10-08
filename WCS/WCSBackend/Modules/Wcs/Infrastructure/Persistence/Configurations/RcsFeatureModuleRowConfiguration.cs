using System.Text.Json;
using Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WCSBackend.Modules.Wcs.Infrastructure.Persistence.Entities;

namespace WCSBackend.Modules.Wcs.Infrastructure.Configurations;

public sealed class RcsFeatureModuleRowConfiguration : IEntityTypeConfiguration<RcsFeatureModuleRow>
{
    public void Configure(EntityTypeBuilder<RcsFeatureModuleRow> builder)
    {
        builder.ToTable("feature_modules");
        builder.HasKey(x => new { x.RcsType, x.Id });
        builder.Property(x => x.RcsType).HasColumnName("rcs_type").HasMaxLength(64).IsRequired();
        builder.Property(x => x.Id).HasColumnName("module_id").HasMaxLength(255).IsRequired();
        builder.Property(x => x.ProfileName).HasColumnName("profile_name").HasMaxLength(255).HasDefaultValue("").IsRequired();
        builder.Property(x => x.BaseUrl).HasColumnName("base_url").HasMaxLength(2048).HasDefaultValue("").IsRequired();
        builder.Property(x => x.IsActive).HasColumnName("is_active");
        builder.Property(x => x.Name).HasColumnName("name");
        builder.Property(x => x.ApiUrl).HasColumnName("api_url");
        builder.Property(x => x.HttpMethod).HasColumnName("http_method").HasMaxLength(16).HasDefaultValue("POST").IsRequired();
        builder.Property(x => x.RequestBodyTemplate).HasColumnName("request_body_template").HasDefaultValue("").IsRequired();
        builder.Property(x => x.SuccessProperty).HasColumnName("success_property").HasDefaultValue("").IsRequired();
        builder.Property(x => x.SuccessValue).HasColumnName("success_value").HasMaxLength(255).HasDefaultValue("true").IsRequired();
        builder.Property(x => x.TimeoutSeconds).HasColumnName("timeout_seconds").HasDefaultValue(10);
        builder.Property(x => x.PreExecutionEffect).HasColumnName("pre_execution_effect");
        builder.Property(x => x.InventoryEffect).HasColumnName("inventory_effect");
        builder.Property(x => x.Params).HasColumnName("params_json")
            .HasConversion(v => JsonSerializer.Serialize(v), v => string.IsNullOrWhiteSpace(v) ? new List<WorkParamDto>() : JsonSerializer.Deserialize<List<WorkParamDto>>(v) ?? new List<WorkParamDto>())
            .Metadata.SetValueComparer(ValueComparer.CreateDefault<List<WorkParamDto>>(favorStructuralComparisons: false));
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
    }
}
