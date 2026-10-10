#!/usr/bin/env bash
set -euo pipefail
umask 077

PLUGIN_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
SOLUTION="$PLUGIN_ROOT/Jellyfin.Plugin.Database.Npgsql.slnx"
HARNESS_PROJECT="$PLUGIN_ROOT/tests/Jellyfin.Plugin.Database.Npgsql.TestHarness/Jellyfin.Plugin.Database.Npgsql.TestHarness.csproj"
HARNESS="$PLUGIN_ROOT/tests/Jellyfin.Plugin.Database.Npgsql.TestHarness/bin/Release/net10.0/Jellyfin.Plugin.Database.Npgsql.TestHarness.dll"
PACKAGE_NAME="jellyfin-plugin-database-npgsql_0.1.0.0.zip"
SCRATCH_ID=$(tr -d '-' < /proc/sys/kernel/random/uuid)
OWNED_ROOT="$PLUGIN_ROOT/.artifacts/release-verify-$SCRATCH_ID"
export DOTNET_CLI_HOME="$PLUGIN_ROOT/.dotnet-cli"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export NUGET_PACKAGES="$PLUGIN_ROOT/.packages"
export NUGET_HTTP_CACHE_PATH="$PLUGIN_ROOT/.nuget-http"
export XDG_DATA_HOME="$PLUGIN_ROOT/.artifacts/xdg-data"

hash_archive() {
    local value
    value=$(dotnet "$HARNESS" hash-archive "$1")
    [[ "$value" =~ ^[0-9a-f]{64}$ ]] \
        || { echo "FAIL: archive hash helper returned malformed output" >&2; exit 1; }
    printf '%s' "$value"
}

dotnet restore "$SOLUTION" --locked-mode
dotnet build "$SOLUTION" --configuration Release --no-restore
dotnet build "$HARNESS_PROJECT" --configuration Release --no-restore
dotnet test --project "$PLUGIN_ROOT/tests/Jellyfin.Plugin.Database.Npgsql.UnitTests/Jellyfin.Plugin.Database.Npgsql.UnitTests.csproj" --configuration Release --no-build
dotnet msbuild "$PLUGIN_ROOT/build/Package.proj" -target:Package -property:Configuration=Release
first_hash=$(hash_archive "$PLUGIN_ROOT/artifacts/$PACKAGE_NAME")
dotnet msbuild "$PLUGIN_ROOT/build/Package.proj" -target:Package -property:Configuration=Release
second_hash=$(hash_archive "$PLUGIN_ROOT/artifacts/$PACKAGE_NAME")
[ "$first_hash" = "$second_hash" ] || { echo "FAIL: two clean package executions were not reproducible" >&2; exit 1; }
"$PLUGIN_ROOT/scripts/audit-build.sh"

mkdir -p "$PLUGIN_ROOT/.artifacts/dependency-audit"
dotnet package list \
    --project "$SOLUTION" \
    --vulnerable \
    --include-transitive \
    --format json \
    --output-version 1 \
    --no-restore >"$PLUGIN_ROOT/.artifacts/dependency-audit/vulnerabilities.json"
python3 "$PLUGIN_ROOT/scripts/audit-vulnerabilities.py" "$PLUGIN_ROOT/.artifacts/dependency-audit/vulnerabilities.json"

dotnet "$HARNESS" relocate-standalone "$PLUGIN_ROOT" "$OWNED_ROOT" >/dev/null
cleanup() {
    dotnet "$HARNESS" delete-owned-artifacts "$PLUGIN_ROOT" "$OWNED_ROOT"
}
trap cleanup EXIT

RELOCATED="$OWNED_ROOT/repository"
export DOTNET_CLI_HOME="$RELOCATED/.dotnet-cli"
export NUGET_PACKAGES="$RELOCATED/.packages"
export NUGET_HTTP_CACHE_PATH="$RELOCATED/.nuget-http"
export XDG_DATA_HOME="$RELOCATED/.artifacts/xdg-data"
dotnet restore "$RELOCATED/Jellyfin.Plugin.Database.Npgsql.slnx" --locked-mode
dotnet build "$RELOCATED/Jellyfin.Plugin.Database.Npgsql.slnx" --configuration Release --no-restore
dotnet msbuild "$RELOCATED/build/Package.proj" -target:Package -property:Configuration=Release
dotnet test --project "$RELOCATED/tests/Jellyfin.Plugin.Database.Npgsql.UnitTests/Jellyfin.Plugin.Database.Npgsql.UnitTests.csproj" --configuration Release --no-build
dotnet test --project "$RELOCATED/tests/Jellyfin.Plugin.Database.Npgsql.IntegrationTests/Jellyfin.Plugin.Database.Npgsql.IntegrationTests.csproj" --configuration Release --no-build
STANDALONE_RELOCATED_COPY=true "$RELOCATED/scripts/audit-build.sh"
relocated_hash=$(hash_archive "$RELOCATED/artifacts/$PACKAGE_NAME")
[ "$first_hash" = "$relocated_hash" ] \
    || { echo "FAIL: relocated package $relocated_hash differs from source package $first_hash" >&2; exit 1; }

if [ "${RUN_BLACK_BOX:-false}" = true ]; then
    "$RELOCATED/tests/black-box-smoke.sh"
fi

echo "Release verification passed: reproducible SHA-256 $first_hash"
