namespace RCSBackend.Modules.Rcs.Infrastructure.Entities;

/// <summary>Single persisted row for the RCS path algorithm's runtime settings.</summary>
public sealed class RcsAlgorithmSettingsRow
{
    public int Id { get; set; }
    public int SegmentPointCount { get; set; } = 4;
    public int AdvanceAfterPoints { get; set; } = 1;
    public DateTime UpdatedAt { get; set; }
}
