#!/usr/bin/env python3
"""Audit the exact redistributed NuGet runtime artifacts and their licenses."""

from __future__ import annotations

import json
import pathlib
import sys
import xml.etree.ElementTree as ET


def fail(message: str) -> None:
    raise SystemExit(f"FAIL: {message}")


if len(sys.argv) != 2:
    fail("usage: audit-dependencies.py PLUGIN_ROOT")

root = pathlib.Path(sys.argv[1]).resolve()
assets_path = root / "src/Jellyfin.Plugin.Database.Npgsql/obj/project.assets.json"
if not assets_path.is_file():
    fail("provider project.assets.json is missing; restore the locked solution first")

assets = json.loads(assets_path.read_text(encoding="utf-8"))
policy = json.loads((root / "build/release-policy.json").read_text(encoding="utf-8"))
targets = assets.get("targets", {})
target_name = next((name for name in targets if name.split("/", 1)[0] == "net10.0"), None)
if target_name is None:
    fail("provider assets contain no net10.0 target")

packaged_dlls = {
    path.name
    for path in (root / "artifacts/package").glob("*.dll")
    if path.name != "Jellyfin.Plugin.Database.Npgsql.dll"
}
expected = {
    details["artifact"]: (package_id, details["version"], details["license"])
    for package_id, details in policy["redistributedPackages"].items()
}
if packaged_dlls != set(expected):
    fail(f"redistributed DLL set differs from dependency policy: {sorted(packaged_dlls)}")

runtime_owners: dict[str, tuple[str, str]] = {}
for library_key, details in targets[target_name].items():
    package_id, separator, version = library_key.partition("/")
    if not separator:
        continue
    for runtime_path in details.get("runtime", {}):
        file_name = pathlib.PurePosixPath(runtime_path).name
        if file_name in packaged_dlls:
            if file_name in runtime_owners:
                fail(f"multiple packages provide redistributed artifact {file_name}")
            runtime_owners[file_name] = (package_id, version)

if set(runtime_owners) != packaged_dlls:
    fail(f"could not map every redistributed DLL to a restored package: {runtime_owners}")

package_folders = [pathlib.Path(path) for path in assets.get("packageFolders", {})]
if not package_folders:
    fail("NuGet global package folder is absent from provider assets")

notices = (root / "THIRD-PARTY-NOTICES.md").read_text(encoding="utf-8")
for file_name, (expected_id, expected_version, expected_license) in expected.items():
    actual_id, actual_version = runtime_owners[file_name]
    if (actual_id, actual_version) != (expected_id, expected_version):
        fail(
            f"{file_name} came from {actual_id}/{actual_version}, expected "
            f"{expected_id}/{expected_version}"
        )
    package_root = next(
        (
            folder / expected_id.lower() / expected_version.lower()
            for folder in package_folders
            if (folder / expected_id.lower() / expected_version.lower()).is_dir()
        ),
        None,
    )
    if package_root is None:
        fail(f"restored package directory is missing for {expected_id}/{expected_version}")
    nuspecs = list(package_root.glob("*.nuspec"))
    if len(nuspecs) != 1:
        fail(f"expected exactly one nuspec for {expected_id}/{expected_version}")
    metadata = ET.parse(nuspecs[0]).getroot()
    license_element = next((element for element in metadata.iter() if element.tag.endswith("license")), None)
    license_value = "" if license_element is None else (license_element.text or "").strip()
    if license_value != expected_license or license_element is None or license_element.get("type") != "expression":
        fail(
            f"{expected_id}/{expected_version} license changed from the reviewed "
            f"SPDX expression {expected_license!r}"
        )
    for required_notice in (file_name, f"v{expected_version}/LICENSE"):
        if required_notice not in notices:
            fail(f"third-party notices omit {required_notice}")

print("Dependency audit passed: every redistributed DLL has an exact package, version, SPDX license, and notice.")
