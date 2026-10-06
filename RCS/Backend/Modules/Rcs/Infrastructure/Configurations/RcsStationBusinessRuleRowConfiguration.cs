using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RCSBackend.Modules.Rcs.Infrastructure.Entities;

namespace RCSBackend.Modules.Rcs.Infrastructure.Configurations;

public sealed class RcsStationBusinessRuleRowConfiguration : IEntityTypeConfiguration<RcsStationBusinessRuleRow>
{
    public void Configure(EntityTypeBuilder<RcsStationBusinessRuleRow> builder)
    {
        builder.ToTable("rcs_station_business_rules");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(x => x.MapCode).HasColumnName("map_code").HasMaxLength(128).IsRequired();
        builder.Property(x => x.PointCode).HasColumnName("point_code").HasMaxLength(128).IsRequired();
        builder.Property(x => x.WaitPointCode).HasColumnName("wait_point_code").HasMaxLength(128).IsRequired();
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(128).IsRequired();
        builder.Property(x => x.Event).HasColumnName("event").HasMaxLength(32).IsRequired();
        builder.Property(x => x.ActionType).HasColumnName("action_type").HasMaxLength(32).IsRequired();
        builder.Property(x => x.ExecutionMode).HasColumnName("execution_mode").HasMaxLength(32).IsRequired();
        builder.Property(x => x.HttpMethod).HasColumnName("http_method").HasMaxLength(16).IsRequired();
        builder.Property(x => x.Url).HasColumnName("url").HasMaxLength(2048).IsRequired();
        builder.Property(x => x.HeadersJson).HasColumnName("headers_json").HasColumnType("json").IsRequired();
        builder.Property(x => x.RequestBodyTemplate).HasColumnName("request_body_template").HasColumnType("json").IsRequired();
        builder.Property(x => x.PermitResponsePath).HasColumnName("permit_response_path").HasMaxLength(256).IsRequired();
        builder.Property(x => x.DenyMessagePath).HasColumnName("deny_message_path").HasMaxLength(256).IsRequired();
        builder.Property(x => x.TimeoutMs).HasColumnName("timeout_ms");
        builder.Property(x => x.RetryCount).HasColumnName("retry_count");
        builder.Property(x => x.RetryDelayMs).HasColumnName("retry_delay_ms");
        builder.Property(x => x.SortOrder).HasColumnName("sort_order");
        builder.Property(x => x.IsEnabled).HasColumnName("is_enabled");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.HasIndex(x => new { x.MapCode, x.PointCode, x.Event, x.SortOrder });
    }
}
