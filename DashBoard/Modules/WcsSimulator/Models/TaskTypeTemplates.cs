using GrcsBackend.Contracts.Dtos;

namespace GRCS.Dashboard.Modules.WcsSimulator.Models;

/// <summary>
/// 任务类型模板注册表：内置模板已清空（本轮改版后任务类型一律由界面创建），
/// 运行时列表 = 用户创建的自定义模板（持久化到后端 task_templates + localStorage 兜底）。
/// 模板数据类见 GrcsBackend.Contracts.Dtos（TaskTemplateDto / TaskPointDto / WorkParamDto / WorkValueSourceDto）。
/// </summary>
public static class TaskTypeRegistry
{
    /// <summary>内置模板（当前为空；恢复历史内置类型时在此追加）。</summary>
    private static readonly TaskTemplateDto[] Builtins = [];

    private static readonly List<TaskTemplateDto> Custom = [];
    private static List<TaskTemplateDto>? _all;

    /// <summary>全部模板（内置 + 自定义），顺序即「选择任务类型」芯片展示顺序。</summary>
    public static IReadOnlyList<TaskTemplateDto> All
    {
        get
        {
            if (_all == null)
            {
                _all = [.. Builtins, .. Custom];
            }
            return _all;
        }
    }

    /// <summary>仅用户自定义模板（用于持久化）。</summary>
    public static IReadOnlyList<TaskTemplateDto> Customs => Custom;

    /// <summary>启动时载入自定义模板（替换当前自定义集，不影响内置）。</summary>
    public static void SetCustoms(IEnumerable<TaskTemplateDto> customs)
    {
        Custom.Clear();
        Custom.AddRange(customs);
        _all = null;
    }

    /// <summary>新增模板；Value 与已有模板冲突时返回 false。</summary>
    public static bool Add(TaskTemplateDto t)
    {
        if (All.Any(x => string.Equals(x.Value, t.Value, StringComparison.OrdinalIgnoreCase))) return false;
        Custom.Add(t);
        _all = null;
        return true;
    }

    /// <summary>按 Value 删除自定义模板；内置模板不可删，返回是否删除成功。</summary>
    public static bool Remove(string value)
    {
        var n = Custom.RemoveAll(x => string.Equals(x.Value, value, StringComparison.OrdinalIgnoreCase));
        if (n > 0) _all = null;
        return n > 0;
    }

    /// <summary>是否为用户自定义模板（内置返回 false，用于芯片上是否显示删除按钮）。</summary>
    public static bool IsCustom(string value) => Custom.Any(x => string.Equals(x.Value, value, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 按旧 Value 原地替换自定义模板（保留其在列表中的位置，不改变排序）；
    /// 内置模板不可替换，返回是否替换成功。
    /// </summary>
    public static bool Replace(string oldValue, TaskTemplateDto replacement)
    {
        var idx = Custom.FindIndex(x => string.Equals(x.Value, oldValue, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return false;
        Custom[idx] = replacement;
        _all = null;
        return true;
    }
}
