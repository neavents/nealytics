FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:548d93f8a18a1acbe6cc127bc4f47281430d34a9e35c18afa80a8d6741c2adc3

RUN apt-get update && apt-get install -y --no-install-recommends build-essential ca-certificates curl && rm -rf /var/lib/apt/lists/*

WORKDIR /build

RUN curl --fail --location --silent --show-error \
      https://codeload.github.com/axboe/liburing/tar.gz/d41bf9220ec39277ff235379e9089d9e0fd6c2a5 \
      -o liburing.tar.gz && \
    echo 'e4db39641298c90f42c29e264bd1057d0342380e712d56dfd9049da953637f48  liburing.tar.gz' | sha256sum --check && \
    mkdir liburing && tar -xzf liburing.tar.gz --strip-components=1 -C liburing

WORKDIR /build/liburing

RUN ./configure --cc=gcc --cxx=g++ && make -C src -j2 CFLAGS='-O3 -fPIC'

COPY native/ /native/

RUN bash /native/build.sh /native-output /build/liburing

WORKDIR /workspace
