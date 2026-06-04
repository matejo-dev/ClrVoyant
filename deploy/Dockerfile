# ClrVoyant as an in-POD debug sidecar (Linux, HTTP transport).
#
# The agent connects over HTTP from outside the cluster (port-forward / ingress);
# netcoredbg + ClrMD attach to the target process LOCALLY, co-located in the POD
# (see ../docs/remote-debugging-pod.md). HTTP always requires CLRVOYANT_AUTH_TOKEN
# (the image does not bake one in — the server fails closed without it).

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
# Publish triggers the FetchNetcoredbg MSBuild target, which downloads the pinned
# netcoredbg for the build architecture (SHA-256 verified) and bundles it under
# /app/tools. Build for arm64 with `docker buildx build --platform linux/arm64 .`
# — the base images are multi-arch and the fetch picks the matching asset.
RUN dotnet publish src/ClrVoyant.Server/ClrVoyant.Server.csproj -c Release -o /app

# ---- runtime ----
# aspnet image: the HTTP transport needs the ASP.NET Core shared framework.
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .
RUN chmod +x /app/tools/netcoredbg/netcoredbg

ENV CLRVOYANT_TRANSPORT=http \
    CLRVOYANT_HTTP_URL=http://0.0.0.0:3001
# CLRVOYANT_AUTH_TOKEN is intentionally NOT set here — supply it at deploy time
# (e.g. a Kubernetes Secret). Without it the server refuses to start.

EXPOSE 3001
ENTRYPOINT ["dotnet", "ClrVoyant.Server.dll"]
