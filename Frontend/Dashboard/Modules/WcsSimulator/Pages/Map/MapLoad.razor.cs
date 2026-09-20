using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Contracts.Dtos;

namespace Dashboard.Modules.WcsSimulator.Pages;

public partial class MapLoad
{
    [Parameter] public bool Collapsed { get; set; }
    [Parameter] public EventCallback Toggle { get; set; }
    [Parameter] public bool OverviewCollapsed { get; set; }
    [Parameter] public bool Loading { get; set; }
    [Parameter] public EventCallback LoadFromApi { get; set; }
    [Parameter] public EventCallback<InputFileChangeEventArgs> OnFileSelected { get; set; }
    [Parameter] public string ErrorMessage { get; set; } = "";
    [Parameter] public bool Loaded { get; set; }
    [Parameter] public int StationCount { get; set; }
    [Parameter] public int PathsCount { get; set; }
    [Parameter] public EventCallback ToggleOverview { get; set; }
    [Parameter] public IReadOnlyList<MapStationLite> Stations { get; set; } = Array.Empty<MapStationLite>();
    [Parameter] public IReadOnlyList<MapStationLite> Filtered { get; set; } = Array.Empty<MapStationLite>();
    [Parameter] public int TypeFilter { get; set; }
    [Parameter] public string Search { get; set; } = "";
    [Parameter] public string EnableFilter { get; set; } = "";
    [Parameter] public EventCallback<int> SetTypeFilter { get; set; }
    [Parameter] public EventCallback<ChangeEventArgs> SearchChanged { get; set; }
    [Parameter] public EventCallback<ChangeEventArgs> EnableFilterChanged { get; set; }
    [Parameter] public EventCallback SaveState { get; set; }
    [Parameter] public EventCallback<string> CopyMark { get; set; }
    [Parameter] public Func<int, string> LoadStateText { get; set; } = static _ => "";
}

