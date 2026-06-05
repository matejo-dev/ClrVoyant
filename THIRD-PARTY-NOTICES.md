# Third-party notices

ClrVoyant depends on the components below. NuGet dependencies ship their own license
files inside the packages; the licenses are summarized here for convenience.
netcoredbg is **not** committed to this repository — it is downloaded at build/publish
time (pinned version, SHA-256 verified) by MSBuild targets (`FetchNetcoredbg` for the
build-host cache; `StageNetcoredbgRidsToPublish`, which stages every supported RID into
the published package) and bundled with the server output. By redistributing the server
output you also redistribute netcoredbg under its license, reproduced below.

## netcoredbg — MIT License

- Project: https://github.com/Samsung/netcoredbg
- Copyright (c) Samsung Electronics Co., LTD
- Fetched version: pinned in `Directory.Build.props` (`NetcoredbgVersion`, the single
  source of truth) and consumed by the server csproj.

```
MIT License

Copyright (c) Samsung Electronics Co., LTD

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

netcoredbg itself bundles parts of the .NET runtime debugging components
(`dbgshim`, `Microsoft.CodeAnalysis.*`, etc.), licensed by Microsoft under the MIT
License; their notices ship inside the netcoredbg release archive.

## NuGet dependencies (all MIT)

| Package | Owner | License |
|---|---|---|
| `Microsoft.Diagnostics.Runtime` (ClrMD) | Microsoft | MIT |
| `Microsoft.Diagnostics.NETCore.Client` | Microsoft | MIT |
| `ModelContextProtocol` | Model Context Protocol | MIT |
| `ModelContextProtocol.AspNetCore` | Model Context Protocol | MIT |
| `Microsoft.Extensions.Hosting` | Microsoft (.NET) | MIT |
| `System.Management` | Microsoft (.NET) | MIT |

Each package's authoritative license text is included in the package and on its
NuGet listing.
