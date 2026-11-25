# Sample target apps

These are small .NET apps used as **debug targets** by the integration tests and
for manual demos. They are **committed source** (their `bin/`/`obj/` are gitignored,
like any project) and are part of the solution (`ClrVoyant.slnx`).

They are **not** bundled into the server and **not** referenced as project
dependencies — the tests/smokes launch them as *external* processes (which is the
whole point: ClrVoyant attaches to a real, separate .NET process). The test project
builds them itself (a plain `dotnet test tests/ClrVoyant.Tests` is enough; `TestPaths`
locates the built DLL), for every target framework the tests debug. `SampleApp` and
`ParentApp` are multi-targeted (`net8.0;net9.0;net10.0`) so the runtime-sensitive tests
can run once per target runtime; `WinFormsApp` is Windows only.

| Project | What it exercises |
|---|---|
| [SampleApp](SampleApp/Program.cs) | Parks async `Task`s (`AwaitForever` → WaitingForActivation, a Faulted task), then loops calling `Calc.Compute()` — a real named-type method (not a top-level local function) so it's a stable target for **both** a source-line breakpoint and a **function breakpoint** (`Calc.Compute`), plus live Tasks for the ClrMD inspector. |
| [ParentApp](ParentApp/Program.cs) | Spawns `SampleApp` as a child .NET process — drives the child-process auto-attach test. |
| [WinFormsApp](WinFormsApp/Program.cs) | **Windows-only.** Shows a form off-screen in a loop; its `OnLoad` is reached from the native window procedure, so a breakpoint there has native frames on its call stack. Rolls forward to the newest installed desktop runtime. Drives the native-frames call-stack test. |

Build them standalone with:

```sh
dotnet build samples/SampleApp/SampleApp.csproj -c Debug            # all three frameworks
dotnet build samples/SampleApp/SampleApp.csproj -c Debug -f net10.0 # one of them
dotnet build samples/ParentApp/ParentApp.csproj -c Debug
dotnet build samples/WinFormsApp/WinFormsApp.csproj -c Debug   # Windows only
```
