using WCSBackend.Modules.Wcs.Infrastructure;
using Contracts.Dtos;

namespace WCSBackend.Modules.Wcs.Automation.Services;

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
        var rangeSet = range.Enabled && range.Marks.Count > 0 ? new HashSet<string>(range.Marks, StringComparer.OrdinalIgnoreCase) : null;
        var allStations = _map.GetStations();
        var stations = rangeSet == null ? allStations : allStations.Where(s => rangeSet.Contains(s.Mark)).ToList();
        bool HasType(int bits) => bits != 0 && stations.Any(s => (s.StationType & bits) != 0);

        foreach (var template in items ?? [])
        {
            TaskTemplateDto? previousTask = null;
            var seenPick = false;
            for (var i = 0; i < template.Steps.Count; i++)
            {
                var step = template.Steps[i];
                if (step.Kind is AutoStepKinds.PickPallet or AutoStepKinds.PickCargo or AutoStepKinds.PickLoadedPallet)
                {
                    seenPick = true;
                    previousTask = null;
                    continue;
                }
                if (step.Kind != AutoStepKinds.RunTemplate)
                {
                    previousTask = null;
                    continue;
                }

                var taskTemplate = _taskTemplates.GetAll().FirstOrDefault(t => string.Equals(t.Value, step.TemplateValue, StringComparison.OrdinalIgnoreCase));
                if (taskTemplate == null)
                {
                    errors.Add($"Template [{template.Name}] step {i + 1}: task template is missing ({step.TemplateValue})");
                    previousTask = null;
                    continue;
                }

                var startSource = ResolveStartSource(step, previousTask != null);
                if (startSource == AutoStartSources.SelectedStation && !seenPick)
                    errors.Add($"Template [{template.Name}] step {i + 1} ({taskTemplate.Label}): selected-station start requires a preceding pick step");
                if (startSource == AutoStartSources.PreviousTaskEnd && previousTask == null)
                    errors.Add($"Template [{template.Name}] step {i + 1} ({taskTemplate.Label}): previous-task-end start requires a preceding task step");

                var endBits = taskTemplate.End?.StationTypeBits ?? 0;
                if (endBits != 0 && !HasType(endBits))
                    errors.Add($"Template [{template.Name}] step {i + 1} ({taskTemplate.Label}): no destination type exists in the selection range");
                var startBits = taskTemplate.Start?.StationTypeBits ?? 0;
                if (startSource == AutoStartSources.PreviousTaskEnd && startBits != 0 && previousTask != null)
                {
                    var previousEndBits = previousTask.End?.StationTypeBits ?? 0;
                    if (previousEndBits != 0 && (previousEndBits & startBits) == 0)
                        errors.Add($"Template [{template.Name}] step {i + 1} ({taskTemplate.Label}): previous destination type does not match this start type");
                }
                previousTask = taskTemplate;
            }
        }
        return errors;
    }

    private static string ResolveStartSource(AutoStepDto step, bool hasPreviousTask)
    {
        if (step.StartSource == AutoStartSources.SelectedStation || step.StartSource == AutoStartSources.PreviousTaskEnd)
            return step.StartSource;
        if (step.StartSource == AutoStartSources.AutoSelect)
            return hasPreviousTask ? AutoStartSources.PreviousTaskEnd : AutoStartSources.SelectedStation;
        return step.UsePickedStart
            ? (hasPreviousTask ? AutoStartSources.PreviousTaskEnd : AutoStartSources.SelectedStation)
            : (hasPreviousTask ? AutoStartSources.PreviousTaskEnd : AutoStartSources.SelectedStation);
    }

    public string TaskTemplateLabel(string value)
    {
        var template = _taskTemplates.GetAll().FirstOrDefault(x => string.Equals(x.Value, value, StringComparison.OrdinalIgnoreCase));
        return template?.Label ?? value;
    }
}
