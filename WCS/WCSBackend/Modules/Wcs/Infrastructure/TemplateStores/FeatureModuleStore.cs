using Contracts.Dtos;

namespace WCSBackend.Modules.Wcs.Infrastructure;

/// <summary>活动 RCS 类型的业务功能模板门面。</summary>
public sealed class FeatureModuleStore
{
    private readonly RcsProtocolTemplateStore _templates;

    public FeatureModuleStore(RcsProtocolTemplateStore templates) => _templates = templates;

    public List<FeatureModuleDto> GetAll() => _templates.GetActive().FeatureModules;

    public void ReplaceAll(IEnumerable<FeatureModuleDto> items)
        => _templates.ReplaceActiveFeatureModules(items ?? []);

    public bool Remove(string id)
    {
        var modules = GetAll();
        var removed = modules.RemoveAll(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed) ReplaceAll(modules);
        return removed;
    }
}
