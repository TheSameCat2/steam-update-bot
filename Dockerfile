FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . .
RUN dotnet restore SteamUpdateBot.sln \
    && dotnet publish src/SteamUpdateBot.App/SteamUpdateBot.App.csproj --configuration Release --no-restore --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
USER root
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir --parents /data \
    && chown ${APP_UID}:${APP_UID} /data

WORKDIR /app
COPY --from=build --chown=${APP_UID}:${APP_UID} /app/publish .

USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 CMD curl --fail --silent http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "SteamUpdateBot.App.dll"]
