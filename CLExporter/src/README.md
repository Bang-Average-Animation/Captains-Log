# Building CLExporter

This source was recovered from the 0.0.3 release assembly. Build it against the assemblies from your Captain of Industry installation with a .NET SDK and the .NET Framework 4.8 targeting pack.

```powershell
$env:COI_ROOT = 'D:\SteamLibrary\steamapps\common\Captain of Industry'
dotnet build .\CLExporter\src\CLExporter.csproj -c Release
```

From the repository root, the build writes `CLExporter\src\bin\Release\net48\CLExporter.dll`. Copy it and the updated `CLExporter\manifest.json` into the installed mod folder before starting the game. The game writes the recipe catalogue after entering a new or loaded game.

Shared recipes have an optional `machineBindings` object, keyed by machine prototype ID. Each binding includes its cycle duration in ticks and its scaled inputs and outputs. The dashboard looks up the machine-specific entry when available and supports older exports with a single duration per recipe.
