FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

# curl exists only for the HEALTHCHECK below — the aspnet Debian image ships without it.
RUN apt-get update && apt-get install -y --no-install-recommends \
        curl \
    && rm -rf /var/lib/apt/lists/*

FROM node:22 AS web-build
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci --ignore-scripts
COPY web/ .
RUN npm run build -- --configuration production

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["src/EmotePurge.Api/EmotePurge.Api.csproj", "src/EmotePurge.Api/"]
COPY ["src/EmotePurge.Infrastructure/EmotePurge.Infrastructure.csproj", "src/EmotePurge.Infrastructure/"]
COPY ["src/EmotePurge.Core/EmotePurge.Core.csproj", "src/EmotePurge.Core/"]
RUN dotnet restore "src/EmotePurge.Api/EmotePurge.Api.csproj"
# src/ only, not the repo root (S4-17): `COPY . .` invalidated this layer for every web/, tests/
# or docs change, and the separate `dotnet build` stage produced output nothing ever copied —
# publish builds on its own.
COPY src/ src/
WORKDIR "/src/src/EmotePurge.Api"
RUN dotnet publish "EmotePurge.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
RUN groupadd --system appgroup && useradd --system --gid appgroup --no-create-home appuser
COPY --from=build /app/publish .
COPY --from=web-build /web/dist/web/browser ./wwwroot
# Mount point for the Data Protection key ring (DataProtection__KeyPath). appuser has --no-create-home,
# so without an explicitly owned directory there is nowhere writable to persist keys to, and every
# restart would silently invalidate all auth cookies.
RUN mkdir -p /keys && chown -R appuser:appgroup /app /keys
USER appuser
# S3-35: GET /api/health answers 503 while the worker pipeline is down/stale, so `curl -f` fails
# and the container shows unhealthy in `docker ps`/Portainer. start-period covers the S3-34
# migration guard plus Kestrel boot. Note the semantic: this reports the *pipeline's* health as
# seen by the Api — deliberate, per review Z1/S3-35 (a green Api in front of a dead worker is
# exactly the state that must not look healthy).
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
    CMD curl -fsS http://localhost:8080/api/health || exit 1
ENTRYPOINT ["dotnet", "EmotePurge.Api.dll"]
