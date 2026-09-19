namespace Contracts.Dtos;

/// <summary>GRCS 硬编码接口说明条目（前端「GRCS 接口说明」模块展示，数据源与 GrcsHttpClient 实际路径保持一致）。</summary>
public class GrcsApiDocDto
{
    /// <summary>接口名（如 TaskReceive）。</summary>
    public string Name { get; set; } = "";
    /// <summary>HTTP 方法（GET/POST）。</summary>
    public string Method { get; set; } = "";
    /// <summary>相对路径模板（baseUrl 来自「地图信息 → 系统设置」的 GRCS 地址，如 http://localhost:8224）。</summary>
    public string UrlTemplate { get; set; } = "";
    /// <summary>接口描述。</summary>
    public string Description { get; set; } = "";
    /// <summary>在哪些页面/功能中用到。</summary>
    public string UsedBy { get; set; } = "";
    /// <summary>请求参数说明（展开显示）。</summary>
    public List<GrcsApiParamDto> Params { get; set; } = [];
    /// <summary>请求体示例 JSON（POST 接口，展开显示；GET 为 null）。</summary>
    public string? BodyExample { get; set; }
}

/// <summary>单个参数说明。</summary>
public class GrcsApiParamDto
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public bool Required { get; set; }
    public string Description { get; set; } = "";
}