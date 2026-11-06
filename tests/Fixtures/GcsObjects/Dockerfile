FROM golang:1.24.8-bookworm@sha256:4ed690d6649d63c312b99a6120025ec79ce3b542968a37da53d6236c7c61a848 AS build
WORKDIR /source
ENV CGO_ENABLED=0 GOMAXPROCS=2 GOTOOLCHAIN=local
COPY tests/Fixtures/GcsObjects/main.go ./main.go
RUN gofmt -w main.go && go build -p 2 -trimpath -ldflags '-s -w' -o /out/gcs-objects main.go && /out/gcs-objects --self-test

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0@sha256:62028293921f5bd6db3d9f9119cd29cd926dfd3c2336da23b09f425e40c3c832
COPY --from=build /out/gcs-objects /usr/local/bin/gcs-objects
USER 65532:65532
EXPOSE 9000
ENTRYPOINT ["/usr/local/bin/gcs-objects"]
