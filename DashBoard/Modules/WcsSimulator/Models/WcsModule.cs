using GrcsBackend.Contracts.Dtos;

namespace GRCS.Dashboard.Modules.WcsSimulator.Models;

/// <summary>
/// 模块注册表：运行时列表 = 用户在信号交互页创建的模块（持久化到 localStorage 键 grcs_si_modules）。
/// 模块数据类见 GrcsBackend.Contracts.Dtos.FeatureModuleDto（Id/Name/ApiUrl/Params）。
/// </summary>
public static class ModuleRegistry
{
    private static readonly List<FeatureModuleDto> Custom = [];

    public static IReadOnlyList<FeatureModuleDto> All => Custom;

    /// <summary>载入模块集合（替换当前集合）。</summary>
    public static void SetCustoms(IEnumerable<FeatureModuleDto> modules)
    {
        Custom.Clear();
        Custom.AddRange(modules);
    }

    /// <summary>新增模块；Id 冲突时返回 false。</summary>
    public static bool Add(FeatureModuleDto m)
    {
        if (Custom.Any(x => string.Equals(x.Id, m.Id, StringComparison.OrdinalIgnoreCase))) return false;
        Custom.Add(m);
        return true;
    }

    /// <summary>按 Id 删除模块。</summary>
    public static bool Remove(string id) => Custom.RemoveAll(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;

    /// <summary>按 Id 查找模块。</summary>
    public static FeatureModuleDto? Find(string id) => Custom.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
}
