FROM golang:1.24.8-bookworm@sha256:4ed690d6649d63c312b99a6120025ec79ce3b542968a37da53d6236c7c61a848 AS build
WORKDIR /source
ENV CGO_ENABLED=0 GOMAXPROCS=2 GOTOOLCHAIN=local
RUN curl --fail --location --silent --show-error \
      https://codeload.github.com/minio/minio/legacy.tar.gz/9e49d5e7a648f00e26f2246f4dc28e6b07f8c84a \
      -o /tmp/source.tar.gz && \
    echo '7b8bbbeebfb0c9829ac3b1b85473a18d2691b6873e76d1770c6a2dea0b08b985  /tmp/source.tar.gz' | sha256sum --check && \
    tar -xzf /tmp/source.tar.gz --strip-components=1 -C /source && \
    rm /tmp/source.tar.gz
RUN go build -p 2 -trimpath -ldflags '-s -w' -o /out/minio .

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0@sha256:62028293921f5bd6db3d9f9119cd29cd926dfd3c2336da23b09f425e40c3c832
LABEL org.opencontainers.image.source="https://github.com/minio/minio" \
      org.opencontainers.image.revision="9e49d5e7a648f00e26f2246f4dc28e6b07f8c84a"
COPY --from=build /out/minio /usr/local/bin/minio
COPY --from=build /source/LICENSE /LICENSE.minio
RUN mkdir /data && chown 65532:65532 /data
USER 65532:65532
EXPOSE 9000
ENTRYPOINT ["/usr/local/bin/minio"]
CMD ["server", "/data", "--console-address", ":9001"]
