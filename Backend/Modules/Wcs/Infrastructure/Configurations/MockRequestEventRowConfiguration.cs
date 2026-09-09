using Contracts.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace WCSBackend.Modules.Wcs.Infrastructure.Configurations;

public class MockRequestEventRowConfiguration : IEntityTypeConfiguration<MockRequestEventRow>
{
    public void Configure(EntityTypeBuilder<MockRequestEventRow> builder)
    {
        builder.ToTable("mock_request_events");
        builder.HasKey(x => x.Key);
        builder.Property(x => x.EventId).HasColumnName("event_id");
        builder.Property(x => x.Key).HasColumnName("event_key").IsRequired();
        builder.Property(x => x.PathPattern).HasColumnName("path_pattern");
        builder.Property(x => x.Method).HasColumnName("method");
        builder.Property(x => x.BodyJson).HasColumnName("body_json");
        builder.Property(x => x.QueryString).HasColumnName("query_string");
        builder.Property(x => x.Time).HasColumnName("time");
        builder.Property(x => x.DecidedAt).HasColumnName("decided_at");
        builder.Property(x => x.Status).HasColumnName("status");
        builder.Property(x => x.Attempts).HasColumnName("attempts");
        builder.Property(x => x.MockRuleId).HasColumnName("mock_rule_id");
        builder.Property(x => x.MockRuleDescription).HasColumnName("mock_rule_desc");
        builder.Property(x => x.RuleJson).HasColumnName("rule_json");
    }
}