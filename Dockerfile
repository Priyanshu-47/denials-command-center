# ---------------------------------------------------------------------
# AQ.Denials API — multi-stage build.
#
# The data pack is deliberately NOT copied into any layer: .dockerignore
# excludes it, so no image this repository produces can contain PHI.
# The pack is mounted at run time by docker-compose, read-only.
# ---------------------------------------------------------------------

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first so source-only edits do not invalidate the package layer.
COPY AQ.Denials.sln ./
COPY .config/ .config/
COPY src/ src/
RUN dotnet tool restore
RUN dotnet restore AQ.Denials.sln
RUN dotnet publish src/AQ.Denials.Api/AQ.Denials.Api.csproj \
        -c Release --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# The base image ships neither wget nor curl, so the compose healthcheck would
# fail forever on a healthy container. curl only — no package recommendations.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

# No root, no debugger, no compiler in the final image.
RUN id -u denials >/dev/null 2>&1 || useradd --system --uid 10001 denials
USER denials

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080
ENTRYPOINT ["dotnet", "AQ.Denials.Api.dll"]
