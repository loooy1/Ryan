namespace WCSBackend.Modules.CommonTools.Models;

public sealed class NestedCargoRequest
{
    public string? SceneName { get; set; }
    public string? RackCode { get; set; }
    public string? CargoCode { get; set; }
}

public sealed record NestedCargoResult(long RackId, long CargoId, string SceneName, string RackCode, string CargoCode);
