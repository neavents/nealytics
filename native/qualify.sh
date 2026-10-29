set -euo pipefail

rid="${1:?Provide linux-x64 or linux-arm64}"
case "$rid" in
    linux-x64) expected_architecture=x86_64 ;;
    linux-arm64) expected_architecture=aarch64 ;;
    *) exit 2 ;;
esac
test "$(uname -m)" = "$expected_architecture"

repository="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repository"
image="nealytics-native-qualification:$rid"
output="/workspace/artifacts/platform/$rid"
bridge="$output/native/libnealytics_wal.so"
mkdir -p "$HOME/.nuget/packages" "artifacts/platform/$rid"
uname -a > "artifacts/platform/$rid/host.txt"
docker build --file native/Dockerfile --tag "$image" .

common=(
    run --rm --cap-drop ALL --memory 3g --cpus 2 --env OTEL_SDK_DISABLED=true
    --user "$(id -u):$(id -g)" --env DOTNET_CLI_HOME=/tmp/nealytics-dotnet --env NUGET_PACKAGES=/cache
    --mount "type=bind,source=$repository,target=/workspace"
    --mount "type=bind,source=$HOME/.nuget/packages,target=/cache"
)
permitted=(--network none --security-opt seccomp=unconfined)

docker "${common[@]}" --network none "$image" bash /native/build.sh "$output/native" /build/liburing
docker "${common[@]}" "${permitted[@]}" "$image" bash /native/test.sh "$output/native-c" /build/liburing

for project in Nealytics.Engine.Tests.Unit Nealytics.Engine.Tests.Native; do
    docker "${common[@]}" "$image" dotnet restore "tests/$project" \
        --artifacts-path "$output/build" -p:NealyticsNativeLibrary="$bridge" --nologo
    docker "${common[@]}" "${permitted[@]}" "$image" dotnet test "tests/$project" --no-restore \
        --artifacts-path "$output/build" -p:NealyticsNativeLibrary="$bridge" \
        --collect:'XPlat Code Coverage' --results-directory "$output/$project" \
        --logger "trx;LogFileName=$project.trx" --nologo
done

python3 native/verify_coverage.py "artifacts/platform/$rid" "artifacts/platform/$rid/native-c/nea_wal.c.gcov"

docker "${common[@]}" "${permitted[@]}" --env NEALYTICS_TEST_WAL_BACKEND=io_uring "$image" \
    dotnet test tests/Nealytics.Engine.Tests.Unit --no-restore --no-build \
    --artifacts-path "$output/build" -p:NealyticsNativeLibrary="$bridge" \
    --filter 'FullyQualifiedName~IngestionDurabilityContractTests' \
    --results-directory "$output/http" --logger 'trx;LogFileName=native-http.trx' --nologo

docker "${common[@]}" --network none --security-opt "seccomp=$repository/native/deny-io-uring.json" \
    --env NEALYTICS_EXPECT_NATIVE_DENIED=1 "$image" \
    dotnet test tests/Nealytics.Engine.Tests.Native --no-restore --no-build \
    --artifacts-path "$output/build" -p:NealyticsNativeLibrary="$bridge" \
    --filter 'FullyQualifiedName~StartupHonorsTheExplicitlySelectedKernelPolicy' \
    --results-directory "$output/denied" --logger 'trx;LogFileName=denied.trx' --nologo

docker "${common[@]}" "$image" dotnet publish src/Nealytics.Engine --configuration Release \
    --runtime "$rid" --self-contained true --artifacts-path "$output/publish-build" \
    --output "$output/publish" -p:NealyticsNativeLibrary="$bridge" -p:DebugType=None --nologo

binary="artifacts/platform/$rid/publish/Nealytics.Engine"
python3 native/test_artifact.py "$binary" --backend io_uring --permit-native
python3 native/test_artifact.py "$binary" --backend auto --permit-native
python3 native/test_artifact.py "$binary" --backend auto
python3 native/test_artifact.py "$binary" --backend managed

docker build --file Dockerfile --tag "nealytics-qualified-engine:$rid" .
python3 native/test_artifact.py --engine-image "nealytics-qualified-engine:$rid" --backend io_uring --permit-native

docker "${common[@]}" "$image" dotnet tool restore

docker "${common[@]}" "${permitted[@]}" "$image" bash /workspace/native/mutate.sh \
    /workspace "$bridge" "$output/mutation" stryker-config.selection.json
