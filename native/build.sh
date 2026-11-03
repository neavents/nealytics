set -euo pipefail

native_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
output="${1:?Provide the output directory}"
liburing_source="${2:?Provide the pinned liburing source directory}"

mkdir -p "$output"

cc -std=c11 -D_GNU_SOURCE -O3 -fPIC -fvisibility=hidden \
    -Wall -Wextra -Werror -Wconversion -Wsign-conversion \
    -isystem "$liburing_source/src/include" \
    -shared "$native_root/wal/nea_wal.c" "$liburing_source/src/liburing.a" \
    -Wl,--no-undefined -Wl,--exclude-libs,ALL -Wl,-z,relro -Wl,-z,now \
    -o "$output/libnealytics_wal.so"
