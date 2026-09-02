using System.Text.Json;
using GrcsBackend.Contracts.Entities;
using GrcsBackend.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GrcsBackend.Modules.Wcs.Infrastructure.Configurations;

public class MockRuleRowConfiguration : IEntityTypeConfiguration<MockRuleRow>
{
    public void Configure(EntityTypeBuilder<MockRuleRow> builder)
    {
        builder.ToTable("mock_rules");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("rule_id").IsRequired();
        builder.Property(x => x.Name).HasColumnName("name");
        builder.Property(x => x.Method).HasColumnName("method");
        builder.Property(x => x.PathPattern).HasColumnName("path_pattern");
        builder.Property(x => x.Matchers).HasColumnName("matchers_json")
            .HasConversion(
                v => JsonSerializer.Serialize(v),
                v => string.IsNullOrWhiteSpace(v) ? new List<MockMatcher>() : JsonSerializer.Deserialize<List<MockMatcher>>(v) ?? new List<MockMatcher>())
            .Metadata.SetValueComparer(ValueComparer.CreateDefault<List<MockMatcher>>(favorStructuralComparisons: false));
        builder.Property(x => x.ResponseCode).HasColumnName("response_code");
        builder.Property(x => x.ResponseBody).HasColumnName("response_body");
        builder.Property(x => x.Enabled).HasColumnName("enabled");
        builder.Property(x => x.Priority).HasColumnName("priority");
        builder.Property(x => x.Description).HasColumnName("description");
        builder.Property(x => x.AlsoRecord).HasColumnName("also_record");
        builder.Property(x => x.BoardSync).HasColumnName("board_sync");
        builder.Property(x => x.RequiresApproval).HasColumnName("requires_approval");
        builder.Property(x => x.ApprovalVariable).HasColumnName("approval_variable");
        builder.Property(x => x.ApprovalTrueValue).HasColumnName("approval_true_value");
        builder.Property(x => x.ApprovalFalseValue).HasColumnName("approval_false_value");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
    }
}