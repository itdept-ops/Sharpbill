# syntax=docker/dockerfile:1.8.1@sha256:e87caa74dcb7d46cd820352bfea12591f3dba3ddc4285e19c7dcd13359f7cefd

# Patch-level and manifest-digest pins keep SDK/runtime rebuild inputs reviewable. Renovate or an
# equivalent controlled process should update the tag and digest together.
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.302-noble@sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664
ARG DOTNET_RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0.10-noble-chiseled-extra@sha256:f9bd6be9b5ab75b8196bff0f0972580edaea7fa8ca04e6ef530950e33caee5b0

FROM ${DOTNET_SDK_IMAGE} AS restore
WORKDIR /src

# Copy dependency manifests before source so ordinary code edits reuse the restore layer.
COPY global.json Directory.Build.props Directory.Packages.props Sharpbill.slnx ./
COPY src/Sharpbill.Api/Sharpbill.Api.csproj src/Sharpbill.Api/
COPY src/Sharpbill.Application/Sharpbill.Application.csproj src/Sharpbill.Application/
COPY src/Sharpbill.Contracts/Sharpbill.Contracts.csproj src/Sharpbill.Contracts/
COPY src/Sharpbill.Domain/Sharpbill.Domain.csproj src/Sharpbill.Domain/
COPY src/Sharpbill.Infrastructure/Sharpbill.Infrastructure.csproj src/Sharpbill.Infrastructure/
COPY src/Sharpbill.Migrator/Sharpbill.Migrator.csproj src/Sharpbill.Migrator/
COPY src/Sharpbill.Migrator/Tests/Sharpbill.Migrator.Tests.csproj src/Sharpbill.Migrator/Tests/
COPY src/Sharpbill.Workers/Sharpbill.Workers.csproj src/Sharpbill.Workers/
COPY tests/Sharpbill.Application.Tests/Sharpbill.Application.Tests.csproj tests/Sharpbill.Application.Tests/
COPY tests/Sharpbill.ArchitectureTests/Sharpbill.ArchitectureTests.csproj tests/Sharpbill.ArchitectureTests/
COPY tests/Sharpbill.Domain.Tests/Sharpbill.Domain.Tests.csproj tests/Sharpbill.Domain.Tests/
COPY tests/Sharpbill.IntegrationTests/Sharpbill.IntegrationTests.csproj tests/Sharpbill.IntegrationTests/
RUN dotnet restore Sharpbill.slnx --nologo

FROM restore AS build
COPY . .
RUN dotnet publish src/Sharpbill.Api/Sharpbill.Api.csproj \
      --configuration Release \
      --no-restore \
      --output /out/api \
      --nologo \
      --property:UseAppHost=true \
    && dotnet publish src/Sharpbill.Migrator/Sharpbill.Migrator.csproj \
      --configuration Release \
      --no-restore \
      --output /out/migrator \
      --nologo \
      --property:UseAppHost=true

# Development retains the SDK and source for dotnet-watch. The published copy is used solely by
# the container healthcheck, so the probe never depends on curl, wget, a shell, or build output in
# the bind-mounted source tree.
FROM build AS dev
ENV ASPNETCORE_ENVIRONMENT=Development \
    ASPNETCORE_URLS=http://0.0.0.0:8000 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1
WORKDIR /src
EXPOSE 8000
HEALTHCHECK --interval=10s --timeout=5s --start-period=40s --retries=6 \
  CMD ["/out/api/Sharpbill.Api", "--health-check", "http://127.0.0.1:8000/api/health/live"]
CMD ["dotnet", "watch", "--project", "src/Sharpbill.Api/Sharpbill.Api.csproj", "run", "--no-launch-profile", "--urls", "http://0.0.0.0:8000"]

# Fetch the AWS RDS trust bundle as a checksum-verified build input without adding a downloader to
# either runtime image.
FROM scratch AS certificates
ADD --checksum=sha256:e5bb2084ccf45087bda1c9bffdea0eb15ee67f0b91646106e466714f9de3c7e3 \
  https://truststore.pki.rds.amazonaws.com/global/global-bundle.pem /rds-global-bundle.pem

FROM ${DOTNET_RUNTIME_IMAGE} AS runtime
ENV ASPNETCORE_HTTP_PORTS=8000 \
    COMPlus_EnableDiagnostics=0 \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    HOME=/tmp \
    TMPDIR=/tmp
WORKDIR /app/api
COPY --from=certificates --chown=65532:65532 /rds-global-bundle.pem /opt/sharpbill/certs/rds-global-bundle.pem

# Production contains only published artifacts. The migrator is included in the API image so
# promotion can use one signed digest; the migrator target below changes only the entrypoint.
FROM runtime AS prod
COPY --from=build --chown=65532:65532 /out/api/ /app/api/
COPY --from=build --chown=65532:65532 /out/migrator/ /app/migrator/
USER 65532:65532
EXPOSE 8000
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
  CMD ["/app/api/Sharpbill.Api", "--health-check", "http://127.0.0.1:8000/api/health/live"]
ENTRYPOINT ["/app/api/Sharpbill.Api"]

FROM prod AS migrator
WORKDIR /app/migrator
HEALTHCHECK NONE
ENTRYPOINT ["/app/migrator/Sharpbill.Migrator"]
CMD ["migrate"]
