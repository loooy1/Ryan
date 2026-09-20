using Microsoft.AspNetCore.Components;

namespace Dashboard.Modules.WcsSimulator.Pages;

public partial class MapCreation
{
    [CascadingParameter] internal MapReader Host { get; set; } = null!;
}

