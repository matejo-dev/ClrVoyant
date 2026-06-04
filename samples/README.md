# Sample target apps

These are small .NET apps used as **debug targets** by the integration tests and
for manual demos. They are **committed source** (their `bin/`/`obj/` are gitignored,
like any project) and are part of the solution (`ClrVoyant.slnx`).

They are **not** bundled into the server and **not** referenced as project
dependencies — the tests/smokes launch them as *external* processes (which is the
whole point: ClrVoyant attaches to a real, separate .NET process). The tests build
them on demand (`TestPaths` locates the built DLL); CI builds them explicitly.

| Project | What it exercises |
|---|---|
| [SampleApp](SampleApp/Program.cs) | Parks async `Task`s (`AwaitForever` → WaitingForActivation, a Faulted task), then loops in a synchronous `Compute()` — a stable breakpoint target plus live Tasks for the ClrMD inspector. |
| [ParentApp](ParentApp/Program.cs) | Spawns `SampleApp` as a child .NET process — drives the child-process auto-attach test. |

Build them standalone with:

```sh
dotnet build samples/SampleApp/SampleApp.csproj -c Debug
dotnet build samples/ParentApp/ParentApp.csproj -c Debug
```
