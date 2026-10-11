#!/usr/bin/env bash
set -euo pipefail
umask 077

PLUGIN_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
SOLUTION="$PLUGIN_ROOT/Jellyfin.Plugin.Database.Npgsql.slnx"
HARNESS_PROJECT="$PLUGIN_ROOT/tests/Jellyfin.Plugin.Database.Npgsql.TestHarness/Jellyfin.Plugin.Database.Npgsql.TestHarness.csproj"
HARNESS="$PLUGIN_ROOT/tests/Jellyfin.Plugin.Database.Npgsql.TestHarness/bin/Release/net10.0/Jellyfin.Plugin.Database.Npgsql.TestHarness.dll"
PACKAGE_NAME="jellyfin-plugin-database-npgsql_0.1.2.0.zip"
SYMBOL_NAME="Jellyfin.Plugin.Database.Npgsql.pdb"
SCRATCH_ID=$(tr -d '-' < /proc/sys/kernel/random/uuid)
OWNED_ROOT="${TMPDIR:-/tmp}/jellyfin-npgsql-release-verify-$SCRATCH_ID"
ORIGINAL_CHECKOUT=$(git -C "$PLUGIN_ROOT" rev-parse --show-toplevel)
export DOTNET_CLI_HOME="$PLUGIN_ROOT/.dotnet-cli"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_USE_MSBUILD_SERVER=0
export MSBUILDDISABLENODEREUSE=1
export UseSharedCompilation=false
export NUGET_PACKAGES="$PLUGIN_ROOT/.packages"
export NUGET_HTTP_CACHE_PATH="$PLUGIN_ROOT/.nuget-http"
export XDG_DATA_HOME="$PLUGIN_ROOT/.artifacts/xdg-data"

command -v grep >/dev/null 2>&1 \
    || { echo "FAIL: release verification requires grep" >&2; exit 1; }

restore_without_build_servers() {
    dotnet restore "$@" --disable-build-servers
}

build_without_build_servers() {
    dotnet build "$@" --disable-build-servers
}

msbuild_without_build_servers() {
    dotnet msbuild "$@" -nodeReuse:false -property:UseSharedCompilation=false
}

hash_file() {
    local harness=$1
    local file=$2
    local value
    value=$(dotnet "$harness" hash-archive "$file")
    [[ "$value" =~ ^[0-9a-f]{64}$ ]] \
        || { echo "FAIL: file hash helper returned malformed output" >&2; exit 1; }
    printf '%s' "$value"
}

assert_relocated_obj_metadata() {
    local forbidden_root=$1
    local relocated_root=$2
    local metadata_leaks
    local grep_status=0
    metadata_leaks=$(grep --recursive --binary-files=text --files-with-matches --fixed-strings \
        --include='project.assets.json' \
        --include='*.nuget.dgspec.json' \
        -- "$forbidden_root" "$relocated_root") || grep_status=$?
    [ "$grep_status" -le 1 ] \
        || { echo "FAIL: could not inspect relocated obj metadata" >&2; exit 1; }
    if [ -n "$metadata_leaks" ]; then
        while IFS= read -r leaked_path; do
            echo "FAIL: relocated obj metadata contains source checkout path: ${leaked_path#"$relocated_root/"}" >&2
        done <<<"$metadata_leaks"
        echo "FAIL: source and relocated release phases did not use isolated build-server state" >&2
        exit 1
    fi
}

validate_black_box_inputs() {
    [ -z "${JELLYFIN_HOST_REPOSITORY:-}" ] \
        || { echo "FAIL: release verification rejects JELLYFIN_HOST_REPOSITORY; supply an exact prebuilt JELLYFIN_HOST_PUBLISH" >&2; exit 1; }
    [ -z "${JELLYFIN_UNSUPPORTED_HOST_REPOSITORY:-}" ] \
        || { echo "FAIL: release verification rejects JELLYFIN_UNSUPPORTED_HOST_REPOSITORY" >&2; exit 1; }
    [ -z "${UNSUPPORTED_JELLYFIN_HOST_REPOSITORY:-}" ] \
        || { echo "FAIL: release verification rejects UNSUPPORTED_JELLYFIN_HOST_REPOSITORY" >&2; exit 1; }
    [ -n "${JELLYFIN_HOST_PUBLISH:-}" ] \
        || { echo "FAIL: RUN_BLACK_BOX=true requires an exact prebuilt JELLYFIN_HOST_PUBLISH" >&2; exit 1; }
    [[ "${JELLYFIN_HOST_SHA256:-}" =~ ^[0-9a-fA-F]{64}$ ]] \
        || { echo "FAIL: RUN_BLACK_BOX=true requires a 64-hex JELLYFIN_HOST_SHA256" >&2; exit 1; }
    if [ -n "${UNSUPPORTED_JELLYFIN_HOST_PUBLISH:-}" ]; then
        [[ "${UNSUPPORTED_JELLYFIN_HOST_SHA256:-}" =~ ^[0-9a-fA-F]{64}$ ]] \
            || { echo "FAIL: UNSUPPORTED_JELLYFIN_HOST_PUBLISH requires a 64-hex UNSUPPORTED_JELLYFIN_HOST_SHA256" >&2; exit 1; }
    fi
}

if [ "${RUN_BLACK_BOX:-false}" = true ]; then
    validate_black_box_inputs
fi

restore_without_build_servers "$SOLUTION" --locked-mode
build_without_build_servers "$SOLUTION" --configuration Release --no-restore
build_without_build_servers "$HARNESS_PROJECT" --configuration Release --no-restore
dotnet test --project "$PLUGIN_ROOT/tests/Jellyfin.Plugin.Database.Npgsql.UnitTests/Jellyfin.Plugin.Database.Npgsql.UnitTests.csproj" --configuration Release --no-build
python3 -m unittest discover -s "$PLUGIN_ROOT/tests/scripts" -p 'test_*.py' -v
msbuild_without_build_servers "$PLUGIN_ROOT/build/Package.proj" -target:Package -property:Configuration=Release
first_hash=$(hash_file "$HARNESS" "$PLUGIN_ROOT/artifacts/$PACKAGE_NAME")
msbuild_without_build_servers "$PLUGIN_ROOT/build/Package.proj" -target:Package -property:Configuration=Release
second_hash=$(hash_file "$HARNESS" "$PLUGIN_ROOT/artifacts/$PACKAGE_NAME")
[ "$first_hash" = "$second_hash" ] || { echo "FAIL: two clean package executions were not reproducible" >&2; exit 1; }
source_symbol_hash=$(hash_file "$HARNESS" "$PLUGIN_ROOT/artifacts/symbols/$SYMBOL_NAME")
"$PLUGIN_ROOT/scripts/audit-build.sh"

mkdir -p "$PLUGIN_ROOT/.artifacts/dependency-audit"
dotnet package list \
    --project "$SOLUTION" \
    --vulnerable \
    --include-transitive \
    --format json \
    --output-version 1 \
    --no-restore >"$PLUGIN_ROOT/.artifacts/dependency-audit/vulnerabilities.json"
python3 "$PLUGIN_ROOT/scripts/audit-vulnerabilities.py" \
    "$PLUGIN_ROOT/.artifacts/dependency-audit/vulnerabilities.json" \
    "$PLUGIN_ROOT" \
    "$PLUGIN_ROOT/build/release-policy.json"

dotnet "$HARNESS" relocate-isolated "$PLUGIN_ROOT" "$OWNED_ROOT" >/dev/null
ISOLATED_CLEANER="$OWNED_ROOT/cleanup-tool/Jellyfin.Plugin.Database.Npgsql.TestHarness.dll"
cleanup() {
    if [ -f "$ISOLATED_CLEANER" ]; then
        dotnet "$ISOLATED_CLEANER" delete-isolated-artifacts "$OWNED_ROOT"
    else
        echo "FAIL: isolated cleanup helper is missing; retained $OWNED_ROOT for inspection" >&2
        return 1
    fi
}
trap cleanup EXIT

RELOCATED="$OWNED_ROOT/repository"
case "$RELOCATED/" in
    "$ORIGINAL_CHECKOUT/"*)
        echo "FAIL: relocated repository is still inside the original checkout" >&2
        exit 1
        ;;
esac
export GIT_CEILING_DIRECTORIES="$OWNED_ROOT"
if git -C "$RELOCATED" rev-parse --show-toplevel >/dev/null 2>&1; then
    echo "FAIL: relocated repository can discover an enclosing Git checkout" >&2
    exit 1
fi
export DOTNET_CLI_HOME="$RELOCATED/.dotnet-cli"
export NUGET_PACKAGES="$RELOCATED/.packages"
export NUGET_HTTP_CACHE_PATH="$RELOCATED/.nuget-http"
export XDG_DATA_HOME="$RELOCATED/.artifacts/xdg-data"
cd "$RELOCATED"
restore_without_build_servers "$RELOCATED/Jellyfin.Plugin.Database.Npgsql.slnx" --locked-mode
assert_relocated_obj_metadata "$ORIGINAL_CHECKOUT" "$RELOCATED"
build_without_build_servers "$RELOCATED/Jellyfin.Plugin.Database.Npgsql.slnx" --configuration Release --no-restore
msbuild_without_build_servers "$RELOCATED/build/Package.proj" -target:Package -property:Configuration=Release
dotnet test --project "$RELOCATED/tests/Jellyfin.Plugin.Database.Npgsql.UnitTests/Jellyfin.Plugin.Database.Npgsql.UnitTests.csproj" --configuration Release --no-build
dotnet test --project "$RELOCATED/tests/Jellyfin.Plugin.Database.Npgsql.IntegrationTests/Jellyfin.Plugin.Database.Npgsql.IntegrationTests.csproj" --configuration Release --no-build
python3 -m unittest discover -s "$RELOCATED/tests/scripts" -p 'test_*.py' -v
mkdir -p "$RELOCATED/.artifacts/dependency-audit"
dotnet package list \
    --project "$RELOCATED/Jellyfin.Plugin.Database.Npgsql.slnx" \
    --vulnerable \
    --include-transitive \
    --format json \
    --output-version 1 \
    --no-restore >"$RELOCATED/.artifacts/dependency-audit/vulnerabilities.json"
python3 "$RELOCATED/scripts/audit-vulnerabilities.py" \
    "$RELOCATED/.artifacts/dependency-audit/vulnerabilities.json" \
    "$RELOCATED" \
    "$RELOCATED/build/release-policy.json"
STANDALONE_RELOCATED_COPY=true FORBIDDEN_PARENT_CHECKOUT="$ORIGINAL_CHECKOUT" "$RELOCATED/scripts/audit-build.sh"
RELOCATED_HARNESS="$RELOCATED/tests/Jellyfin.Plugin.Database.Npgsql.TestHarness/bin/Release/net10.0/Jellyfin.Plugin.Database.Npgsql.TestHarness.dll"
relocated_hash=$(hash_file "$RELOCATED_HARNESS" "$RELOCATED/artifacts/$PACKAGE_NAME")
[ "$first_hash" = "$relocated_hash" ] \
    || { echo "FAIL: relocated package $relocated_hash differs from source package $first_hash" >&2; exit 1; }
relocated_symbol_hash=$(hash_file "$RELOCATED_HARNESS" "$RELOCATED/artifacts/symbols/$SYMBOL_NAME")
[ "$source_symbol_hash" = "$relocated_symbol_hash" ] \
    || { echo "FAIL: relocated PDB $relocated_symbol_hash differs from source PDB $source_symbol_hash" >&2; exit 1; }

if [ "${RUN_BLACK_BOX:-false}" = true ]; then
    STAGED_HOST_PUBLISH=$(dotnet "$ISOLATED_CLEANER" stage-host-publish \
        "$JELLYFIN_HOST_PUBLISH" \
        "$OWNED_ROOT" \
        host-publish-input \
        "$JELLYFIN_HOST_SHA256")
    export JELLYFIN_HOST_PUBLISH="$STAGED_HOST_PUBLISH"
    if [ -n "${UNSUPPORTED_JELLYFIN_HOST_PUBLISH:-}" ]; then
        STAGED_UNSUPPORTED_HOST_PUBLISH=$(dotnet "$ISOLATED_CLEANER" stage-host-publish \
            "$UNSUPPORTED_JELLYFIN_HOST_PUBLISH" \
            "$OWNED_ROOT" \
            unsupported-host-publish-input \
            "${UNSUPPORTED_JELLYFIN_HOST_SHA256:-}")
        export UNSUPPORTED_JELLYFIN_HOST_PUBLISH="$STAGED_UNSUPPORTED_HOST_PUBLISH"
    fi
    export FORBIDDEN_PARENT_CHECKOUT="$ORIGINAL_CHECKOUT"
    "$RELOCATED/tests/black-box-smoke.sh"
fi

assert_relocated_obj_metadata "$ORIGINAL_CHECKOUT" "$RELOCATED"
grep_status=0
path_leaks=$(grep --recursive --binary-files=text --files-with-matches --fixed-strings \
    --exclude-dir='.dotnet-cli' \
    --exclude-dir='.nuget-http' \
    --exclude-dir='.packages' \
    -- "$ORIGINAL_CHECKOUT" "$RELOCATED") || grep_status=$?
[ "$grep_status" -le 1 ] \
    || { echo "FAIL: could not inspect the relocated repository for path leaks" >&2; exit 1; }
if [ -n "$path_leaks" ]; then
    while IFS= read -r leaked_path; do
        echo "FAIL: isolated path leak: ${leaked_path#"$RELOCATED/"}" >&2
    done <<<"$path_leaks"
    echo "FAIL: isolated source/build/artifacts contain a path back to the original checkout" >&2
    exit 1
fi

echo "Release verification passed: isolated reproducible ZIP SHA-256 $first_hash and PDB SHA-256 $source_symbol_hash"
