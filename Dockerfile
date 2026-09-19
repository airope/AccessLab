FROM node:26.5.0-bookworm-slim AS frontend
WORKDIR /web
COPY src/AccessLab.Web/package*.json ./
RUN npm ci
COPY src/AccessLab.Web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0.400-noble AS backend
WORKDIR /source
COPY global.json Directory.Build.props ./
COPY src/AccessLab.Api/ src/AccessLab.Api/
RUN dotnet restore src/AccessLab.Api/AccessLab.Api.csproj --locked-mode
RUN dotnet publish src/AccessLab.Api/AccessLab.Api.csproj --no-restore -c Release -o /out /p:UseAppHost=false
COPY --from=frontend /web/dist/ /out/wwwroot/

FROM mcr.microsoft.com/dotnet/aspnet:10.0.9-noble
# curl is used only for an explicit readiness probe.
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=backend /out/ ./
ENV ASPNETCORE_URLS=https://0.0.0.0:5443
EXPOSE 5443
HEALTHCHECK --interval=10s --timeout=5s --start-period=60s --retries=12 CMD curl --fail --silent --insecure https://localhost:5443/health/ready || exit 1
ENTRYPOINT ["dotnet", "AccessLab.Api.dll"]
