FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src

COPY src/Nealytics.Engine/Nealytics.Engine.csproj src/Nealytics.Engine/
RUN dotnet restore src/Nealytics.Engine/Nealytics.Engine.csproj -r linux-x64

COPY src/ src/
RUN dotnet publish src/Nealytics.Engine/Nealytics.Engine.csproj \
    -c Release \
    -r linux-x64 \
    -o /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-preview AS runtime
WORKDIR /app

RUN useradd -r nealytics 2>/dev/null || true && \
    mkdir -p /app/logs && \
    (chown -R nealytics:nealytics /app/logs 2>/dev/null || true)

COPY --from=build /app/publish .

USER nealytics
EXPOSE 5000
ENTRYPOINT ["./Nealytics.Engine"]
