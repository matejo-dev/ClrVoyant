# Contributing to ClrVoyant

Thanks for considering a contribution. This page is the short version of how the
project is built, tested and reviewed. By participating you agree to the
[Code of Conduct](CODE_OF_CONDUCT.md).

## Before you start

- **Bugs and ideas**: open an issue using one of the templates. For a bug, the
  environment details the template asks for are what make it reproducible.
- **Security problems**: do not open a public issue. Follow [SECURITY.md](SECURITY.md).
- **Larger changes**: open an issue first and describe the approach. Decisions that
  shape the design are recorded as ADRs in [docs/adr](docs/adr); read the ones that
  touch your area before proposing to change them.

## Build and test

You need the .NET 10 SDK (the version band is pinned in `global.json`). To run every
per-runtime test you also need the .NET 8 and .NET 9 runtimes; without them those
tests are reported as skipped.

```sh
dotnet build ClrVoyant.slnx

# Fast tests: no debugger, no child processes.
dotnet test tests/ClrVoyant.Tests --filter "Category!=Integration&Category!=Packaging"

# Everything, including the tests that drive a real netcoredbg and ClrMD.
dotnet test tests/ClrVoyant.Tests
```

The first build downloads the pinned netcoredbg (SHA-256 verified). The test project
builds the sample debuggees itself. [docs/testing.md](docs/testing.md) describes the
layers of the suite and what CI runs.

## What a change needs

- **Tests that can fail.** Coverage is kept high, but not with padding: a test must
  break when the behaviour it names breaks. No tests that only assert what a fake
  returns, no sleeps used as synchronisation, every wait bounded.
- **Linux as well as Windows.** CI runs on Linux x64, Linux arm64 and Windows x64.
  Build paths with `Path` APIs, and do not assume `.exe`, a drive letter, a
  case-insensitive file system, CRLF line endings or the machine's culture.
- **Tool changes are API changes.** MCP clients and the models driving them depend on
  tool names, parameters, result shapes and error texts. If you add or change a tool,
  update [docs/tools.md](docs/tools.md), give the tool a title and behaviour hints
  (a test pins the table), and say in the pull request what a client will see.
- **Docs and changelog.** Update the documentation your change makes stale, and add an
  entry under `Unreleased` in [CHANGELOG.md](CHANGELOG.md) for anything a user or an
  MCP client can observe.
- **English** for code comments, documentation and commit messages.
- **No private identifiers**: no internal host names, private package feeds or
  machine-specific paths in code, tests, docs or commit messages.

## Pull requests

Keep a pull request to one concern, explain the why in the description, and expect
CI to pass on all three platforms before review. Dependency updates arrive through
Dependabot; if you bump a dependency by hand, say what changed for this project.

## Releases

Releases are cut by the maintainer: the version lives in
`src/ClrVoyant.Server/ClrVoyant.Server.csproj`, and a matching `vX.Y.Z` tag triggers
the release workflow.
