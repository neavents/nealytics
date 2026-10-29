FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:548d93f8a18a1acbe6cc127bc4f47281430d34a9e35c18afa80a8d6741c2adc3 AS native
WORKDIR /build

RUN apt-get update && apt-get install -y --no-install-recommends build-essential ca-certificates curl && rm -rf /var/lib/apt/lists/*

RUN curl --fail --location --silent --show-error \
      https://codeload.github.com/axboe/liburing/tar.gz/d41bf9220ec39277ff235379e9089d9e0fd6c2a5 \
      -o liburing.tar.gz && \
    echo 'e4db39641298c90f42c29e264bd1057d0342380e712d56dfd9049da953637f48  liburing.tar.gz' | sha256sum --check && \
    mkdir liburing && tar -xzf liburing.tar.gz --strip-components=1 -C liburing && \
    cd liburing && ./configure --cc=gcc --cxx=g++ && make -C src -j2 CFLAGS='-O3 -fPIC'

COPY native/ /native/
RUN bash /native/build.sh /native-output /build/liburing

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0@sha256:548d93f8a18a1acbe6cc127bc4f47281430d34a9e35c18afa80a8d6741c2adc3 AS build
ARG TARGETARCH
WORKDIR /src

COPY src/Nealytics.Engine/Nealytics.Engine.csproj src/Nealytics.Engine/
RUN RID=$([ "$TARGETARCH" = "arm64" ] && echo linux-arm64 || echo linux-x64) && \
    dotnet restore src/Nealytics.Engine/Nealytics.Engine.csproj -r "$RID"

COPY src/ src/
COPY --from=native /native-output/libnealytics_wal.so /native-output/libnealytics_wal.so
RUN RID=$([ "$TARGETARCH" = "arm64" ] && echo linux-arm64 || echo linux-x64) && \
    dotnet publish src/Nealytics.Engine/Nealytics.Engine.csproj \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -p:NealyticsNativeLibrary=/native-output/libnealytics_wal.so \
    -o /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0@sha256:62028293921f5bd6db3d9f9119cd29cd926dfd3c2336da23b09f425e40c3c832 AS runtime
WORKDIR /app

ENV DOTNET_BUNDLE_EXTRACT_BASE_DIR=/app/.net \
    TelemetryEngine__WriteAheadLogDirectory=/app/logs \
    ASPNETCORE_URLS=http://0.0.0.0:5000

RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*

RUN useradd -r -U nealytics && \
    mkdir -p /app/logs /app/.net && \
    chown -R nealytics:nealytics /app/logs /app/.net

COPY --from=build /app/publish .

USER nealytics
EXPOSE 5000

HEALTHCHECK --interval=30s --timeout=10s --start-period=15s --retries=3 \
    CMD curl -f http://localhost:5000/health || exit 1

ENTRYPOINT ["./Nealytics.Engine"]
