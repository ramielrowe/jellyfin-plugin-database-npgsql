#!/usr/bin/env bash
set -euo pipefail

PLUGIN_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
PACKAGE_DIR="$PLUGIN_ROOT/artifacts/package"
PACKAGE_ZIP="$PLUGIN_ROOT/artifacts/jellyfin-plugin-database-npgsql_0.1.0.0.zip"
PROVIDER_PROJECT="$PLUGIN_ROOT/src/Jellyfin.Plugin.Database.Npgsql/Jellyfin.Plugin.Database.Npgsql.csproj"
NEGATIVE_CONTROL_PROJECT="$PLUGIN_ROOT/tests/Jellyfin.Plugin.Database.Npgsql.IntegrationTests/Jellyfin.Plugin.Database.Npgsql.IntegrationTests.csproj"
REFERENCE_REPORT_DIR="$PLUGIN_ROOT/artifacts/evaluated-references"

fail() {
    echo "FAIL: $*" >&2
    exit 1
}

[ -f "$PLUGIN_ROOT/build/Package.proj" ] \
    || fail "tracked packaging source build/Package.proj is missing"
if git -C "$PLUGIN_ROOT" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
    git -C "$PLUGIN_ROOT" ls-files --error-unmatch -- build/Package.proj >/dev/null 2>&1 \
        || fail "build/Package.proj is not tracked; enclosing ignore rules may have hidden it"
fi

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
    meta.json
)

[ "${actual_files[*]}" = "${expected_files[*]}" ] \
    || fail "unexpected package contents: ${actual_files[*]}"

mapfile -t manifest_files < <(
    awk '
        /^artifacts:/ { in_artifacts = 1; next }
        in_artifacts && /^  - "/ { value = $0; sub(/^  - "/, "", value); sub(/"$/, "", value); print value; next }
        in_artifacts { exit }
    ' "$PLUGIN_ROOT/build.yaml" | sort
)
[ "${manifest_files[*]}" = "${expected_files[*]}" ] \
    || fail "build.yaml artifacts differ from the release allowlist: ${manifest_files[*]}"

mapfile -t zip_files < <(
    python3 -c 'import sys, zipfile
with zipfile.ZipFile(sys.argv[1]) as archive:
    print("\n".join(sorted(name.rstrip("/") for name in archive.namelist() if name.rstrip("/"))))' "$PACKAGE_ZIP"
)
[ "${zip_files[*]}" = "${expected_files[*]}" ] \
    || fail "ZIP contents differ from the artifact allowlist: ${zip_files[*]}"

if find "$PACKAGE_DIR" -maxdepth 1 -type f \
    \( -name 'Jellyfin.Database.Implementations.dll' \
       -o -name 'MediaBrowser.*.dll' \
       -o -name 'Microsoft.EntityFrameworkCore*.dll' \
       -o -name 'Microsoft.Extensions.*.dll' \) \
    | grep -q .; then
    fail "package contains a host-owned runtime assembly"
fi

mkdir -p "$REFERENCE_REPORT_DIR"

mapfile -d '' -t audited_projects < <(
    find "$PLUGIN_ROOT/src" "$PLUGIN_ROOT/tests" \
        -path "$PLUGIN_ROOT/tests/feasibility" -prune -o \
        -type f -name '*.csproj' -print0 \
        | sort -z
)

[ "${#audited_projects[@]}" -gt 0 ] || fail "no projects were found to audit"

audit_properties=(-property:Configuration=Release)
if [ -n "${FORBIDDEN_PARENT_CHECKOUT:-}" ]; then
    audit_properties+=("-property:StandaloneAuditForbiddenRoot=$FORBIDDEN_PARENT_CHECKOUT")
fi

for project in "${audited_projects[@]}"; do
    project_name=$(basename "$project" .csproj)
    report="$REFERENCE_REPORT_DIR/$project_name.json"
    dotnet msbuild "$project" \
        -nologo \
        "${audit_properties[@]}" \
        -target:AuditStandaloneReferences \
        -getItem:ProjectReference,ReferencePath >"$report"
done

# Regression control: import one known ProjectReference outside PLUGIN_ROOT and
# prove the same target rejects it. This also guards against an audit target
# that silently stops running after an SDK/MSBuild change.
negative_root=$(mktemp -d)
trap 'rm -rf "$negative_root"' EXIT
mkdir -p "$negative_root/forbidden"
cat >"$negative_root/forbidden/Forbidden.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
EOF
cat >"$negative_root/inject-forbidden-reference.targets" <<EOF
<Project>
  <ItemGroup>
    <ProjectReference Include="$negative_root/forbidden/Forbidden.csproj" />
  </ItemGroup>
</Project>
EOF

negative_log="$negative_root/audit.log"
if dotnet msbuild "$NEGATIVE_CONTROL_PROJECT" \
    -nologo \
    -property:Configuration=Release \
    -property:CustomAfterMicrosoftCommonTargets="$negative_root/inject-forbidden-reference.targets" \
    -target:AuditStandaloneReferences >"$negative_log" 2>&1; then
    fail "negative control unexpectedly accepted an external ProjectReference"
fi

if ! grep -F "ProjectReference escapes the standalone repository:" "$negative_log" >/dev/null; then
    cat "$negative_log" >&2
    fail "negative control failed for an unexpected reason"
fi

echo "Build audit passed: build.yaml, the staged package, and ZIP match the seven-file allowlist; all evaluated project references are internal; and the external-reference negative control was rejected."
