# Source code

The project overview, usage instructions, privacy notes, and release guidance are in the [repository README](../README.md).

From this directory on Windows with the .NET 8 SDK:

```powershell
dotnet build .\CodexUsage.sln -c Release
dotnet run --project .\tests\CodexUsage.Tests -c Release --no-build
.\scripts\Build.ps1
```

`Build.ps1` publishes the self-contained executable to `artifacts/<runtime>/`. Local build output and `schemas-local/` are ignored by the root `.gitignore`.
