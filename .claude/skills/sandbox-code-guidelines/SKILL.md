---
name: sandbox-code-guidelines
description: Guidelines for writing and running sandbox code in the `sandbox/DotnetFiles/` folder. This is for quick experiments, prototyping, or verification of ideas without needing to set up a full project. It includes instructions on how to create .cs files, run them, and best practices for using this sandbox environment effectively.
---

# Sandbox Code Guidelines

**IMPORTANT:** Never use `dotnet script` or `dotnet-script` command. This project does NOT use dotnet-script.

If you need to create a .cs file to verify something, you can create it in the `sandbox/DotnetFiles/` folder and run it.

See `dotnet run` details here: https://github.com/dotnet/sdk/blob/main/documentation/general/dotnet-run-file.md

- For a standalone C# file (without .csproj):

```csharp
#:sdk Microsoft.NET.Sdk.Web
#:property TargetFramework=net10.0
#:project ../../src/Seiton.Core
using Seiton.Core;

var stats = new StatisticsContext();
PowerSort.Sort<int>([ 5, 3, 8, 1, 2 ], stats);

Console.WriteLine("Sorted array with PowerSort.");
Console.WriteLine($"Compares: {stats.CompareCount}, Swaps: {stats.SwapCount}, IndexReads: {stats.IndexReadCount}, IndexWrites: {stats.IndexWriteCount}");
```

```shell
# Create a single .cs file and run it directly
dotnet run sandbox/DotnetFiles/YourCsFile.cs
```

- For a project folder with .csproj:

```shell
cd sandbox/YourProjectFolder
dotnet run -c Release
# Or specify the project file:
dotnet run -c Release --project YourProjectName.csproj
```

use `sandbox/DotnetFiles/Sample.cs` for template.
