using Microsoft.AspNetCore.Components;

namespace Dashboard.Modules.WcsSimulator.Pages;

public partial class MapCreation
{
    [CascadingParameter] internal MapEditor Host { get; set; } = null!;
}

