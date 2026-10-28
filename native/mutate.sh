set -euo pipefail

repository="${1:?Provide the repository directory}"
bridge="${2:?Provide the compiled bridge path}"
output="${3:?Provide the report output directory}"
scratch="$(mktemp -d /tmp/nealytics-native-mutation-XXXXXX)"
trap 'rm -rf -- "$scratch"' EXIT

tar --exclude=bin --exclude=obj --exclude=artifacts --exclude=.git --exclude=StrykerOutput \
    -C "$repository" -cf - . | tar -C "$scratch" -xf -

cd "$scratch"
dotnet tool restore --ignore-failed-sources

export NealyticsNativeLibrary="$bridge"
export OTEL_SDK_DISABLED=true

cd src/Nealytics.Engine
dotnet stryker --config-file stryker-config.native.json --output "$output"
