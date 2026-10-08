#!/usr/bin/env bash
set -euo pipefail

PLUGIN_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
PACKAGE_DIR="$PLUGIN_ROOT/artifacts/package"
PACKAGE_ZIP="$PLUGIN_ROOT/artifacts/jellyfin-plugin-database-npgsql_0.1.0.0.zip"
PROVIDER_PROJECT="$PLUGIN_ROOT/src/Jellyfin.Plugin.Database.Npgsql/Jellyfin.Plugin.Database.Npgsql.csproj"
REFERENCE_REPORT="$PLUGIN_ROOT/artifacts/evaluated-references.json"

fail() {
    echo "FAIL: $*" >&2
    exit 1
}

[ -f "$PACKAGE_ZIP" ] || fail "package ZIP is missing; run the package command first"
[ -d "$PACKAGE_DIR" ] || fail "staged package directory is missing"

mapfile -t actual_files < <(find "$PACKAGE_DIR" -mindepth 1 -maxdepth 1 -type f -printf '%f\n' | sort)
expected_files=(
    Jellyfin.Plugin.Database.Npgsql.deps.json
    Jellyfin.Plugin.Database.Npgsql.dll
    LICENSE
    Npgsql.EntityFrameworkCore.PostgreSQL.dll
    Npgsql.dll
    THIRD-PARTY-NOTICES.md
)

[ "${actual_files[*]}" = "${expected_files[*]}" ] \
    || fail "unexpected package contents: ${actual_files[*]}"

if find "$PACKAGE_DIR" -maxdepth 1 -type f \
    \( -name 'Jellyfin.Database.Implementations.dll' \
       -o -name 'MediaBrowser.*.dll' \
       -o -name 'Microsoft.EntityFrameworkCore*.dll' \
       -o -name 'Microsoft.Extensions.*.dll' \) \
    | grep -q .; then
    fail "package contains a host-owned runtime assembly"
fi

dotnet msbuild "$PROVIDER_PROJECT" \
    -property:Configuration=Release \
    -getItem:ProjectReference,ReferencePath >"$REFERENCE_REPORT"

if grep -F '"ProjectReference"' "$REFERENCE_REPORT" | grep -q 'Identity'; then
    fail "release provider project has an unexpected project reference"
fi

if [ -n "${FORBIDDEN_PARENT_CHECKOUT:-}" ] \
    && grep -F "$FORBIDDEN_PARENT_CHECKOUT" "$REFERENCE_REPORT" >/dev/null; then
    fail "evaluated references contain the forbidden parent checkout: $FORBIDDEN_PARENT_CHECKOUT"
fi

echo "Build audit passed: six allowlisted package files and no external project references."
