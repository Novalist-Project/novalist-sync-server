FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Novalist.Sync.Contracts/Novalist.Sync.Contracts.csproj src/Novalist.Sync.Contracts/
COPY src/Novalist.Sync.Server/Novalist.Sync.Server.csproj src/Novalist.Sync.Server/
RUN dotnet restore src/Novalist.Sync.Server/Novalist.Sync.Server.csproj

COPY src/ src/
ARG VERSION
ARG REVISION
RUN test -n "$VERSION" && test -n "$REVISION" \
    && dotnet publish src/Novalist.Sync.Server/Novalist.Sync.Server.csproj \
       --configuration Release --no-restore --output /publish \
       -p:UseAppHost=false -p:Version="$VERSION" -p:SourceRevisionId="$REVISION"

FROM build AS migration-build
RUN dotnet tool install dotnet-ef --tool-path /tools --version 10.0.13
RUN ASPNETCORE_ENVIRONMENT=Production DOTNET_ENVIRONMENT=Production \
    ConnectionStrings__Default=Host=localhost \
    /tools/dotnet-ef migrations bundle \
    --project src/Novalist.Sync.Server/Novalist.Sync.Server.csproj \
    --configuration Release --no-build --output /migration/efbundle

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ARG VERSION
ARG REVISION
LABEL org.opencontainers.image.title="Novalist Sync Server" \
      org.opencontainers.image.source="https://github.com/QF0xB/novalist-sync-server" \
      org.opencontainers.image.licenses="AGPL-3.0-or-later" \
      org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.revision="$REVISION"
COPY --from=build /publish/ ./
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Novalist.Sync.Server.dll"]

FROM runtime AS migrations
COPY --from=migration-build /migration/efbundle ./efbundle
ENV DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/novalist-migrations
ENTRYPOINT ["/app/efbundle"]

FROM runtime AS final
