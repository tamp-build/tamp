# Diagnosing test-host crashes

The `Tamp.NetCli.V8`, `V9`, and `V10` test settings expose VSTest crash diagnostics
alongside the existing hang diagnostics:

```csharp
using Tamp.NetCli.V10;

var plan = DotNet.Test(s => s
    .SetProject("Tests.csproj")
    .SetBlameCrash(true)
    .SetBlameCrashDumpType(DotNetCrashDumpType.Mini));
```

This emits `test Tests.csproj --blame-crash --blame-crash-dump-type mini`.
Choose `DotNetCrashDumpType.Full` for a full dump. The nullable dump-type setting
is validated when assigned, including direct property assignments; undefined
enum values are rejected rather than emitted as numeric CLI arguments.

Both options are omitted by default. `SetBlameCrash(false)` omits the explicit
crash flag, and `SetBlameCrashDumpType(null)` omits the dump-type flag. As in the
underlying CLI, specifying a dump type still implies crash diagnostics even
without the explicit crash flag. Crash and hang settings can be combined.

These flags target **VSTest**, not Microsoft.Testing.Platform. Dump availability
still depends on the runtime, operating system, and crash type. See Microsoft's
[dotnet test VSTest reference](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-test-vstest#options)
for the underlying behavior and platform requirements.
