using WCSBackend.Modules.Wcs.Infrastructure;
using Contracts.Dtos;

namespace WCSBackend.Modules.Wcs.Automation.Services;

/// <summary>自动化模板校验器：保存前校验模板步骤的结构合法性与站点类型匹配。</summary>
public class TemplateValidator
{
    private readonly MapStoreService _map;
    private readonly RangeConfigService _range;
    private readonly TaskTemplateStore _taskTemplates;
    private readonly AutoTemplateStore _templates;

    public TemplateValidator(MapStoreService map, RangeConfigService range, TaskTemplateStore taskTemplates, AutoTemplateStore templates)
    {
        _map = map; _range = range; _taskTemplates = taskTemplates; _templates = templates;
    }

    public List<string> ValidateTemplates(List<AutoTemplateDto> items)
    {
        var errors = new List<string>();
        var range = _range.Get();
        var rangeSet = (range.Enabled && range.Marks.Count > 0)
            ? new HashSet<string>(range.Marks, StringComparer.OrdinalIgnoreCase)
            : null;
        var allStations = _map.GetStations();
        var stations = rangeSet == null
            ? allStations
            : allStations.Where(s => rangeSet.Contains(s.Mark)).ToList();

        bool HasType(int bits) => bits != 0 && stations.Any(s => (s.StationType & bits) != 0);

        foreach (var tpl in items ?? [])
        {
            TaskTemplateDto? prevTt = null;
            var seenPick = false;
            for (var i = 0; i < tpl.Steps.Count; i++)
            {
                var step = tpl.Steps[i];
                if (step.Kind == AutoStepKinds.PickPallet || step.Kind == AutoStepKinds.PickCargo || step.Kind == AutoStepKinds.PickLoadedPallet)
                {
                    seenPick = true;
                    prevTt = null;
                    continue;
                }
                if (step.Kind != AutoStepKinds.RunTemplate)
                {
                    prevTt = null;
                    continue;
                }
                var tt = _taskTemplates.GetAll().FirstOrDefault(t => string.Equals(t.Value, step.TemplateValue, StringComparison.OrdinalIgnoreCase));
                if (tt == null)
                {
                    errors.Add($"模板「{tpl.Name}」步骤{i + 1}：引用的任务模板不存在（{step.TemplateValue}）");
                    prevTt = null;
                    continue;
                }

                if (step.PickedStepIndex > 0 && step.PickedStepIndex >= i + 1)
                    errors.Add($"模板「{tpl.Name}」步骤{i + 1}（{tt.Label}）：容器来源引用了第 {step.PickedStepIndex} 步，但只能引用前置步骤（当前第 {i + 1} 步）");
                if (step.PickedStepIndex == -1 || (step.PickedStepIndex == 0 && step.UsePickedContainer))
                {
                    if (!seenPick)
                        errors.Add($"模板「{tpl.Name}」步骤{i + 1}（{tt.Label}）：容器使用「最近前置挑选」，但其前面没有选托盘/选货物步骤");
                }
                if (step.UsePickedStart && i == 0)
                    errors.Add($"模板「{tpl.Name}」步骤{i + 1}（{tt.Label}）：勾选了「起点取自前置终点」，但不能作为第一步（前面没有可衔接的步骤）");

                var eb = tt.End?.StationTypeBits ?? 0;
                if (eb != 0 && !HasType(eb))
                    errors.Add($"模板「{tpl.Name}」步骤{i + 1}（{tt.Label}）：终点类型「{StationTypeHelper.BitsName(eb)}」在选点范围内无匹配站点");

                var sb = tt.Start?.StationTypeBits ?? 0;
                if (sb != 0 && !HasType(sb))
                    errors.Add($"模板「{tpl.Name}」步骤{i + 1}（{tt.Label}）：起点类型「{StationTypeHelper.BitsName(sb)}」在选点范围内无匹配站点");

                if (step.UsePickedStart && sb != 0 && prevTt != null)
                {
                    var prevEnd = prevTt.End?.StationTypeBits ?? 0;
                    if (prevEnd != 0 && (prevEnd & sb) == 0)
                        errors.Add($"模板「{tpl.Name}」步骤{i + 1}（{tt.Label}）起点类型「{StationTypeHelper.BitsName(sb)}」与步骤{i}（{prevTt.Label}）终点类型「{StationTypeHelper.BitsName(prevEnd)}」不匹配（起点取自前置终点）");
                }

                prevTt = tt;
            }
        }
        return errors;
    }

    public string TaskTemplateLabel(string value)
    {
        var t = _taskTemplates.GetAll().FirstOrDefault(x => string.Equals(x.Value, value, StringComparison.OrdinalIgnoreCase));
        return t?.Label ?? value;
    }
}