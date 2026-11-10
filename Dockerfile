# syntax=docker/dockerfile:1
# Single image, single origin (D-11): ASP.NET Core serves the API, the SignalR hub and the built React SPA.

FROM node:22-alpine AS client
WORKDIR /client
COPY client/package*.json ./
RUN npm ci
COPY client/ .
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY server/ .
RUN dotnet restore src/Messenger.Api/Messenger.Api.csproj
RUN dotnet publish src/Messenger.Api/Messenger.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends libicu-dev curl && rm -rf /var/lib/apt/lists/*
COPY --from=build /app .
COPY --from=client /client/dist ./wwwroot
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
USER app
EXPOSE 10000
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s CMD curl -fsS "http://localhost:${PORT:-10000}/health" || exit 1
# Render passes the port in $PORT.
CMD ["sh", "-c", "ASPNETCORE_URLS=http://0.0.0.0:${PORT:-10000} dotnet Messenger.Api.dll"]
