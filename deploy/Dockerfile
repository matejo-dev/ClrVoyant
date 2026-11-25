# ClrVoyant as an in-POD debug sidecar (Linux, HTTP transport).
#
# The agent connects over HTTP from outside the cluster (port-forward / ingress);
# netcoredbg + ClrMD attach to the target process LOCALLY, co-located in the POD
# (see ../docs/remote-debugging-pod.md). HTTP always requires CLRVOYANT_AUTH_TOKEN
# (the image does not bake one in — the server fails closed without it).
#
# Build from the repository root:  docker build -f deploy/Dockerfile -t clrvoyant .

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore in its own layer: only the files that determine the package graph are
# copied first (props, global.json, nuget.config and the project files of the
# server and the projects it references), so editing C# sources does not
# invalidate the restore cache.
COPY global.json nuget.config Directory.Build.props Directory.Packages.props ./
COPY src/ClrVoyant.Core/ClrVoyant.Core.csproj src/ClrVoyant.Core/
COPY src/ClrVoyant.Dap/ClrVoyant.Dap.csproj src/ClrVoyant.Dap/
COPY src/ClrVoyant.Inspection/ClrVoyant.Inspection.csproj src/ClrVoyant.Inspection/
COPY src/ClrVoyant.Server/ClrVoyant.Server.csproj src/ClrVoyant.Server/
RUN dotnet restore src/ClrVoyant.Server/ClrVoyant.Server.csproj

# Only the sources and the netcoredbg fetch scripts (tools/) are needed to publish.
COPY src/ src/
COPY tools/ tools/

# Publish stages netcoredbg for ALL supported RIDs (SHA-256 verified) under
# /app/tools/netcoredbg/<rid>/ via the StageNetcoredbgRidsToPublish target; the
# locator picks the image's RID at runtime. Build for arm64 with
# `docker buildx build --platform linux/arm64 .` — the base images are multi-arch.
# UseAppHost=false: the image starts the server with `dotnet ClrVoyant.Server.dll`.
RUN dotnet publish src/ClrVoyant.Server/ClrVoyant.Server.csproj \
      -c Release -o /app --no-restore -p:UseAppHost=false

# The Windows engine is useless in a Linux image. The Linux launchers must be
# executable; do both here so the runtime stage does not copy the files a second
# time just to change their mode. There is deliberately no fallback: if the
# per-RID layout changes, the glob matches nothing, chmod fails, and so does the
# build (rather than shipping an image whose engine cannot start).
RUN rm -rf /app/tools/netcoredbg/win-x64 \
 && chmod +x /app/tools/netcoredbg/linux-*/netcoredbg

# ---- runtime ----
# aspnet image: the HTTP transport needs the ASP.NET Core shared framework.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV CLRVOYANT_TRANSPORT=http \
    CLRVOYANT_HTTP_URL=http://0.0.0.0:3001
# CLRVOYANT_AUTH_TOKEN is intentionally NOT set here — supply it at deploy time
# (e.g. a Kubernetes Secret). Without it the server refuses to start.

EXPOSE 3001
ENTRYPOINT ["dotnet", "ClrVoyant.Server.dll"]
