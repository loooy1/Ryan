namespace GRCS.Dashboard.Modules.WcsSimulator.Services;

/// <summary>
/// 两层导航状态管理：功能 → 页面。
/// 每个浏览器标签页一个实例（Scoped），互不影响。
/// </summary>
public class ModuleNavigationService
{
    /// <summary>当前激活的功能（第一层），如 "wcs"。null 表示在主页。</summary>
    public string? ActiveFeature { get; private set; }

    /// <summary>当前功能下的页面导航（第二层导航项）。</summary>
    public IReadOnlyList<SubNavItem> SubNavItems { get; private set; } = [];

    /// <summary>选中功能后主侧边栏缩回为图标模式（60px），否则展开（260px）。</summary>
    public bool SidebarCollapsed => ActiveFeature != null;

    /// <summary>导航状态变化时触发，MainLayout 订阅此事件来刷新 UI。</summary>
    public event Action? StateChanged;

    /// <summary>
    /// 第一层 → 第二层：选中一个功能，直接进入该功能的页面导航。
    /// </summary>
    /// <param name="featureId">功能标识，如 "wcs"</param>
    /// <param name="subNavItems">该功能下的页面导航列表</param>
    public void SelectFeature(string featureId, List<SubNavItem> subNavItems)
    {
        ActiveFeature = featureId;
        SubNavItems = subNavItems;
        StateChanged?.Invoke();
    }

    /// <summary>回到主页：清空所有选中状态，侧边栏展开。</summary>
    public void GoHome()
    {
        ActiveFeature = null;
        SubNavItems = [];
        StateChanged?.Invoke();
    }
}

/// <summary>第二层：一个页面导航项（如"任务下发"）。Pinned=固定在侧边栏底部状态区上方，不随列表滚动。</summary>
public record SubNavItem(string Label, string Href, string Icon, bool Pinned = false);