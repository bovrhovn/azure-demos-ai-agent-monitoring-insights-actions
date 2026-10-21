FROM node:24-alpine AS frontend
WORKDIR /ui
COPY src/AgentOps.Web/package*.json ./
RUN --mount=type=secret,id=npmrc,target=/root/.npmrc npm ci --ignore-scripts --no-audit --no-fund
COPY src/AgentOps.Web/Frontend ./Frontend
COPY src/AgentOps.Web/vite.config.mjs ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG NUGET_SOURCE
WORKDIR /source/src
COPY src/global.json src/Directory.Build.props ./
COPY src/AgentOps.Web/AgentOps.Web.csproj AgentOps.Web/
RUN if [ -n "$NUGET_SOURCE" ]; then \
      dotnet restore AgentOps.Web/AgentOps.Web.csproj --source "$NUGET_SOURCE"; \
    else dotnet restore AgentOps.Web/AgentOps.Web.csproj; fi
COPY src/AgentOps.Web/ AgentOps.Web/
COPY --from=frontend /ui/wwwroot/js/app.js AgentOps.Web/wwwroot/js/app.js
RUN dotnet publish AgentOps.Web/AgentOps.Web.csproj -c Release --no-restore -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "AgentOps.Web.dll"]
