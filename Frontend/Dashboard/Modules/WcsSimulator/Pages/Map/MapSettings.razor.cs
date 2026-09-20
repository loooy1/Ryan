using Microsoft.AspNetCore.Components;

namespace Dashboard.Modules.WcsSimulator.Pages;

public partial class MapSettings
{
    [CascadingParameter] internal MapReader Host { get; set; } = null!;
}
