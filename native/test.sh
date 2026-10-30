set -euo pipefail

native_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
output="${1:?Provide the output directory}"
liburing_source="${2:?Provide the pinned liburing source directory}"

mkdir -p "$output"
rm -f "$output/nea_wal_tests-nea_wal_tests.gcda"

common=(
    -std=c11 -D_GNU_SOURCE -g -Wall -Wextra -Werror
    -isystem "$liburing_source/src/include"
    "$native_root/wal/tests/nea_wal_tests.c" "$liburing_source/src/liburing.a"
    -Wl,--wrap=io_uring_submit_and_wait -Wl,--wrap=__io_uring_get_cqe
    -Wl,--wrap=io_uring_queue_init -Wl,--wrap=calloc -Wl,--wrap=malloc -Wl,--wrap=free
    -Wl,--wrap=io_uring_register_files
)

cc "${common[@]}" -O0 --coverage -o "$output/nea_wal_tests"

"$output/nea_wal_tests"

cc "${common[@]}" -O1 -fsanitize=address,undefined -fno-omit-frame-pointer \
    -o "$output/nea_wal_tests_sanitized"

ASAN_OPTIONS=detect_leaks=1:abort_on_error=1 UBSAN_OPTIONS=halt_on_error=1 \
    "$output/nea_wal_tests_sanitized"

cd "$output"
gcov --branch-probabilities --branch-counts nea_wal_tests-nea_wal_tests.gcno
