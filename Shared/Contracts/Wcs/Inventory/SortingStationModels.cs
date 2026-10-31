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

/// <summary>在 WCS 本地储位新增一个托盘或货物实例。</summary>
public class InventoryUnitMutationRequest
{
    public string Mark { get; set; } = "";
    /// <summary>pallet 或 cargo。</summary>
    public string Kind { get; set; } = "";
    public string Code { get; set; } = "";
}

public class InventoryUnitMutationResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string Mark { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Code { get; set; } = "";
}

public class InventoryBatchMutationRequest
{
    /// <summary>pallet 或 cargo。</summary>
    public string Kind { get; set; } = "";
    /// <summary>批量生成编码前缀，仅生成时使用。</summary>
    public string Prefix { get; set; } = "";
}

public class InventoryBatchMutationResult
{
    public bool Success { get; set; }
    public string Kind { get; set; } = "";
    public int ChangedCount { get; set; }
    public int SkippedCount { get; set; }
    public List<InventoryUnitChange> Changes { get; set; } = [];
    public string Message { get; set; } = "";
}

/// <summary>批量库存变更的轻量结果，用于前端只更新已变更储位。</summary>
public class InventoryUnitChange
{
    public string Mark { get; set; } = "";
    public string Kind { get; set; } = "";
    /// <summary>生成时为新编码，删除时为空。</summary>
    public string Code { get; set; } = "";
}
