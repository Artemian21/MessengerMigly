# syntax=docker/dockerfile:1
# Single image, single origin (D-11): ASP.NET Core serves the API, the SignalR hub and the built React SPA.
# The Python AI service (ai/) runs next to the API in the same container when Ai__Enabled=true (see docker/entrypoint.sh).

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
RUN apt-get update && apt-get install -y --no-install-recommends libicu-dev curl python3 python3-venv && rm -rf /var/lib/apt/lists/*
# AI service in its own virtualenv; the requirements layer is cached until requirements.txt changes.
COPY ai/requirements.txt /app/ai/requirements.txt
RUN python3 -m venv /opt/ai-venv && /opt/ai-venv/bin/pip install --no-cache-dir -r /app/ai/requirements.txt
COPY --from=build /app .
COPY --from=client /client/dist ./wwwroot
COPY ai/app /app/ai/app
COPY docker/entrypoint.sh /entrypoint.sh
RUN sed -i 's/\r$//' /entrypoint.sh && chmod +x /entrypoint.sh
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
USER app
EXPOSE 10000
HEALTHCHECK --interval=30s --timeout=5s --start-period=90s CMD curl -fsS "http://localhost:${PORT:-10000}/health" || exit 1
# The entrypoint starts the AI service when enabled, reads $PORT (Render) and runs the API.
CMD ["/entrypoint.sh"]
