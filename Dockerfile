FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/Nealytics.Engine/Nealytics.Engine.csproj src/Nealytics.Engine/
RUN dotnet restore src/Nealytics.Engine/Nealytics.Engine.csproj -r linux-x64

COPY src/ src/
# Self contained, so the runtime-deps base image below is all this needs.
RUN dotnet publish src/Nealytics.Engine/Nealytics.Engine.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -o /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS runtime
WORKDIR /app

# curl is here for the HEALTHCHECK below. This image ships no HTTP client, so Docker had no way
# to probe the container and reported no health status at all.
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*

RUN useradd -r nealytics 2>/dev/null || true && \
    mkdir -p /app/logs && \
    (chown -R nealytics:nealytics /app/logs 2>/dev/null || true)

COPY --from=build /app/publish .

USER nealytics
EXPOSE 5000

HEALTHCHECK --interval=30s --timeout=10s --start-period=15s --retries=3 \
    CMD curl -f http://localhost:5000/health || exit 1

ENTRYPOINT ["./Nealytics.Engine"]
