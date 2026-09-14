namespace Contracts.Dtos;

/// <summary>保存一个人工分拣台与若干实际分拣台之间的 WCS 账本关联。</summary>
public class SortingStationAssociationRequest
{
    public string ParentStationCode { get; set; } = "";
    public List<string> ChildStationCodes { get; set; } = [];
}

public class SortingStationAssociationResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string ParentStationCode { get; set; } = "";
    public List<string> ChildStationCodes { get; set; } = [];
}
