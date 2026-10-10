#!/usr/bin/env bash
set -euo pipefail

PLUGIN_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
PACKAGE_DIR="$PLUGIN_ROOT/artifacts/package"
PACKAGE_ZIP="$PLUGIN_ROOT/artifacts/jellyfin-plugin-database-npgsql_0.1.0.0.zip"
PACKAGE_CHECKSUM="$PACKAGE_ZIP.sha256"
SYMBOL_FILE="$PLUGIN_ROOT/artifacts/symbols/Jellyfin.Plugin.Database.Npgsql.pdb"
PROVIDER_PROJECT="$PLUGIN_ROOT/src/Jellyfin.Plugin.Database.Npgsql/Jellyfin.Plugin.Database.Npgsql.csproj"
NEGATIVE_CONTROL_PROJECT="$PLUGIN_ROOT/tests/Jellyfin.Plugin.Database.Npgsql.IntegrationTests/Jellyfin.Plugin.Database.Npgsql.IntegrationTests.csproj"
REFERENCE_REPORT_DIR="$PLUGIN_ROOT/artifacts/evaluated-references"
PREDECESSOR_ROOT="$PLUGIN_ROOT/tests/fixtures/predecessor"
PREDECESSOR_PROJECT="$PREDECESSOR_ROOT/Jellyfin.Plugin.Database.Npgsql.PredecessorFixture.csproj"

fail() {
    echo "FAIL: $*" >&2
    exit 1
}

[ -f "$PLUGIN_ROOT/build/Package.proj" ] \
    || fail "tracked packaging source build/Package.proj is missing"
if [ "${STANDALONE_RELOCATED_COPY:-false}" != true ] \
    && git -C "$PLUGIN_ROOT" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
    git -C "$PLUGIN_ROOT" ls-files --error-unmatch -- build/Package.proj >/dev/null 2>&1 \
        || fail "build/Package.proj is not tracked; enclosing ignore rules may have hidden it"
fi

[ -f "$PACKAGE_ZIP" ] || fail "package ZIP is missing; run the package command first"
[ -f "$PACKAGE_CHECKSUM" ] || fail "package SHA-256 sidecar is missing"
[ -f "$SYMBOL_FILE" ] || fail "portable provider symbols are missing"
[ -d "$PACKAGE_DIR" ] || fail "staged package directory is missing"

python3 - "$PACKAGE_ZIP" "$PACKAGE_CHECKSUM" "$SYMBOL_FILE" <<'PY'
import hashlib
import pathlib
import re
import sys
import zipfile

package = pathlib.Path(sys.argv[1])
checksum = pathlib.Path(sys.argv[2]).read_text(encoding="utf-8")
symbol = pathlib.Path(sys.argv[3])
match = re.fullmatch(r"([0-9A-Fa-f]{64})  ([^/\\\r\n]+)\r?\n?", checksum)
if match is None or match.group(2) != package.name:
    raise SystemExit("FAIL: package checksum sidecar is not canonical SHA256SUMS format")
actual = hashlib.sha256(package.read_bytes()).hexdigest()
if actual != match.group(1).lower():
    raise SystemExit("FAIL: package checksum sidecar does not match the ZIP")
if symbol.read_bytes()[:4] != b"BSJB":
    raise SystemExit("FAIL: provider symbol artifact is not a portable PDB")
with zipfile.ZipFile(package) as archive:
    for entry in archive.infolist():
        if entry.date_time != (2000, 1, 1, 0, 0, 0):
            raise SystemExit(f"FAIL: non-reproducible ZIP timestamp for {entry.filename}: {entry.date_time}")
PY

python3 - "$PLUGIN_ROOT/build.yaml" "$PLUGIN_ROOT/meta.json" "$PLUGIN_ROOT/build/release-policy.json" <<'PY'
import json
import pathlib
import re
import sys

build_text = pathlib.Path(sys.argv[1]).read_text(encoding="utf-8")
metadata = json.loads(pathlib.Path(sys.argv[2]).read_text(encoding="utf-8"))
policy = json.loads(pathlib.Path(sys.argv[3]).read_text(encoding="utf-8"))

def scalar(name):
    match = re.search(rf"(?m)^{re.escape(name)}: \"([^\"]+)\"$", build_text)
    if match is None:
        raise SystemExit(f"FAIL: build.yaml has no canonical quoted {name}")
    return match.group(1)

for key in ("name", "guid", "version", "targetAbi"):
    if scalar(key) != metadata[key]:
        raise SystemExit(f"FAIL: build.yaml and meta.json disagree on {key}")
    if policy[key] != metadata[key]:
        raise SystemExit(f"FAIL: release policy and meta.json disagree on {key}")
if scalar("framework") != policy["framework"]:
    raise SystemExit("FAIL: release policy and build.yaml disagree on framework")
if metadata.get("status") != "Active" or metadata.get("autoUpdate") is not False:
    raise SystemExit("FAIL: metadata must be Active and must not claim catalog auto-update support")
if metadata.get("assemblies") != ["Jellyfin.Plugin.Database.Npgsql.dll"]:
    raise SystemExit("FAIL: metadata ordinary-plugin assembly allowlist changed")
PY

grep -F '"status": "Active"' "$PACKAGE_DIR/meta.json" >/dev/null \
    || fail "packaged metadata must keep the full-system restore guard active"
grep -F 'full-system restore is unsupported' "$PACKAGE_DIR/meta.json" >/dev/null \
    || fail "packaged metadata must prominently disclose the restore policy"

for forbidden_restore_symbol in \
    NpgsqlBackupArchive \
    NpgsqlRestoreCoordinator \
    NpgsqlRestoreFileSnapshot \
    NpgsqlScratchSpace \
    NpgsqlBackupTransactionInterceptor \
    NpgsqlRestoreSaveChangesInterceptor; do
    if grep -R -F "$forbidden_restore_symbol" "$PLUGIN_ROOT/src" >/dev/null; then
        fail "experimental full-system restore symbol '$forbidden_restore_symbol' is present in release source"
    fi
    if grep -a -F "$forbidden_restore_symbol" "$PACKAGE_DIR/Jellyfin.Plugin.Database.Npgsql.dll" >/dev/null; then
        fail "experimental full-system restore symbol '$forbidden_restore_symbol' is compiled into the package"
    fi
done

[ -f "$PREDECESSOR_ROOT/fixture-manifest.json" ] || fail "predecessor integrity manifest is missing"
[ "$(grep -F -c '<Compile Include="source-tree/**/*.cs" />' "$PREDECESSOR_PROJECT")" = "1" ] \
    || fail "predecessor fixture must compile exactly its immutable source-tree"
if grep -F '../../../src/' "$PREDECESSOR_PROJECT" >/dev/null; then
    fail "predecessor fixture links the current production source tree"
fi
mapfile -t provider_assembly_projects < <(
    grep -R -l --include='*.csproj' '<AssemblyName>Jellyfin.Plugin.Database.Npgsql</AssemblyName>' \
        "$PLUGIN_ROOT/src" "$PLUGIN_ROOT/tests" "$PLUGIN_ROOT/tools" | sort
)
expected_provider_assembly_projects=(
    "$PLUGIN_ROOT/src/Jellyfin.Plugin.Database.Npgsql/Jellyfin.Plugin.Database.Npgsql.csproj"
    "$PREDECESSOR_PROJECT"
)
[ "${provider_assembly_projects[*]}" = "${expected_provider_assembly_projects[*]}" ] \
    || fail "only the release provider and explicit predecessor fixture may produce the provider assembly"
for forbidden_symbol in ValidateMigrationHistoryAsync IsLoadedHostPresent UuidAggregatesMigration AddUuidMinMaxAggregates; do
    if grep -R -F "$forbidden_symbol" "$PREDECESSOR_ROOT/source-tree" >/dev/null; then
        fail "predecessor source contains post-0.0.9 symbol '$forbidden_symbol'"
    fi
done
expected_source_hash=$(sed -n 's/^[[:space:]]*"sourceSha256": "\([0-9a-f]\{64\}\)",$/\1/p' "$PREDECESSOR_ROOT/fixture-manifest.json")
[ -n "$expected_source_hash" ] || fail "predecessor source hash is absent or malformed"
actual_source_hash=$(dotnet "$PLUGIN_ROOT/tests/Jellyfin.Plugin.Database.Npgsql.TestHarness/bin/Release/net10.0/Jellyfin.Plugin.Database.Npgsql.TestHarness.dll" \
    hash-publish "$PREDECESSOR_ROOT/source-tree")
[ "$actual_source_hash" = "$expected_source_hash" ] \
    || fail "predecessor source snapshot differs from its pinned manifest"

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

mapfile -t policy_files < <(python3 -c 'import json, sys
print("\n".join(sorted(json.load(open(sys.argv[1], encoding="utf-8"))["zipArtifacts"])))' "$PLUGIN_ROOT/build/release-policy.json")
[ "${policy_files[*]}" = "${expected_files[*]}" ] \
    || fail "release-policy.json artifacts differ from the release allowlist: ${policy_files[*]}"

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

python3 "$PLUGIN_ROOT/scripts/audit-dependencies.py" "$PLUGIN_ROOT"

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
    find "$PLUGIN_ROOT/src" "$PLUGIN_ROOT/tests" "$PLUGIN_ROOT/tools" \
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
# Keep the fixture in marker-owned repository-local scratch while overriding
# the audit's synthetic repository boundary to its `repository` child. The
# injected sibling ProjectReference then proves the boundary check rejects an
# escape without leaving undeletable shared scratch.
scratch_id=$(tr -d '-' < /proc/sys/kernel/random/uuid)
negative_root="$PLUGIN_ROOT/.artifacts/audit-negative-$scratch_id"
negative_repository="$negative_root/repository"
harness="$PLUGIN_ROOT/tests/Jellyfin.Plugin.Database.Npgsql.TestHarness/bin/Release/net10.0/Jellyfin.Plugin.Database.Npgsql.TestHarness.dll"
dotnet "$harness" create-owned-artifacts "$PLUGIN_ROOT" "$negative_root" >/dev/null
cleanup_negative_control() {
    dotnet "$harness" delete-owned-artifacts "$PLUGIN_ROOT" "$negative_root"
}
trap cleanup_negative_control EXIT
mkdir -p "$negative_root/forbidden" "$negative_repository"
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
    -property:StandaloneAuditRepositoryRoot="$negative_repository" \
    -property:CustomAfterMicrosoftCommonTargets="$negative_root/inject-forbidden-reference.targets" \
    -target:AuditStandaloneReferences >"$negative_log" 2>&1; then
    fail "negative control unexpectedly accepted an external ProjectReference"
fi

if ! grep -F "ProjectReference escapes the standalone repository:" "$negative_log" >/dev/null; then
    cat "$negative_log" >&2
    fail "negative control failed for an unexpected reason"
fi

echo "Build audit passed: metadata, licenses, symbols, checksum, staged package, and ZIP match the release policy; all evaluated project references are internal; and the external-reference negative control was rejected."
