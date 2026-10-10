#!/usr/bin/env python3
"""Fail closed unless a NuGet vulnerability report has the expected safe shape."""

from __future__ import annotations

import json
import pathlib
import shlex
import sys
from collections.abc import Mapping, Sequence
from typing import NoReturn


class AuditError(ValueError):
    """The report cannot prove that the expected dependency graph was audited."""


def fail(message: str) -> NoReturn:
    raise AuditError(message)


def parse_json_document(text: str, location: str) -> object:
    def reject_duplicate_keys(pairs: list[tuple[str, object]]) -> dict[str, object]:
        result: dict[str, object] = {}
        for key, value in pairs:
            if key in result:
                fail(f"{location} contains duplicate field {key!r}")
            result[key] = value
        return result

    def reject_nonstandard_constant(value: str) -> NoReturn:
        fail(f"{location} contains non-JSON numeric constant {value}")

    return json.loads(
        text,
        object_pairs_hook=reject_duplicate_keys,
        parse_constant=reject_nonstandard_constant,
    )


def require_object(value: object, location: str) -> Mapping[str, object]:
    if not isinstance(value, dict):
        fail(f"{location} must be a JSON object")
    return value


def require_array(value: object, location: str) -> Sequence[object]:
    if not isinstance(value, list):
        fail(f"{location} must be a JSON array")
    return value


def require_string(value: object, location: str) -> str:
    if not isinstance(value, str) or not value.strip():
        fail(f"{location} must be a nonempty string")
    return value


def require_keys(
    value: Mapping[str, object],
    location: str,
    required: set[str],
    optional: set[str] | None = None,
) -> None:
    optional = optional or set()
    missing = required - value.keys()
    unexpected = value.keys() - required - optional
    if missing:
        fail(f"{location} is missing required fields: {', '.join(sorted(missing))}")
    if unexpected:
        fail(f"{location} contains unknown fields: {', '.join(sorted(unexpected))}")


def normalized_project_path(root: pathlib.Path, raw_path: object, location: str) -> str:
    report_path = pathlib.Path(require_string(raw_path, location))
    absolute = report_path.resolve() if report_path.is_absolute() else (root / report_path).resolve()
    try:
        return absolute.relative_to(root).as_posix()
    except ValueError as ex:
        raise AuditError(f"{location} escapes the standalone repository") from ex


def inspect_package(package_value: object, location: str) -> tuple[str, str, int]:
    package = require_object(package_value, location)
    require_keys(
        package,
        location,
        {"id", "resolvedVersion", "vulnerabilities"},
        {"requestedVersion", "autoReferenced"},
    )
    package_id = require_string(package["id"], f"{location}.id")
    resolved_version = require_string(package["resolvedVersion"], f"{location}.resolvedVersion")
    if "requestedVersion" in package:
        require_string(package["requestedVersion"], f"{location}.requestedVersion")
    if "autoReferenced" in package and not isinstance(package["autoReferenced"], bool):
        fail(f"{location}.autoReferenced must be a boolean")

    vulnerabilities = require_array(package["vulnerabilities"], f"{location}.vulnerabilities")
    if not vulnerabilities:
        fail(f"{location}.vulnerabilities must be nonempty in a --vulnerable report")
    for vulnerability_index, vulnerability_value in enumerate(vulnerabilities):
        vulnerability_location = f"{location}.vulnerabilities[{vulnerability_index}]"
        vulnerability = require_object(vulnerability_value, vulnerability_location)
        require_keys(vulnerability, vulnerability_location, {"severity", "advisoryurl"})
        require_string(vulnerability["severity"], f"{vulnerability_location}.severity")
        require_string(vulnerability["advisoryurl"], f"{vulnerability_location}.advisoryurl")
    return package_id, resolved_version, len(vulnerabilities)


def audit_report(
    report_value: object,
    repository_root: pathlib.Path,
    expected_projects: set[str],
    expected_sources: set[str],
) -> list[str]:
    root = repository_root.resolve()
    report = require_object(report_value, "report")
    require_keys(report, "report", {"version", "parameters", "sources", "projects"})
    if type(report["version"]) is not int or report["version"] != 1:
        fail("report.version must be the supported integer value 1")

    parameters = require_string(report["parameters"], "report.parameters")
    try:
        parameter_list = shlex.split(parameters)
    except ValueError as ex:
        raise AuditError("report.parameters is malformed") from ex
    if len(parameter_list) != 2 or set(parameter_list) != {"--vulnerable", "--include-transitive"}:
        fail("report.parameters must prove --vulnerable --include-transitive")

    source_list = [
        require_string(source, f"report.sources[{index}]")
        for index, source in enumerate(require_array(report["sources"], "report.sources"))
    ]
    sources = set(source_list)
    if len(source_list) != len(sources) or not sources or sources != expected_sources:
        fail("report.sources does not exactly match the release vulnerability sources")

    projects = require_array(report["projects"], "report.projects")
    if not projects:
        fail("report.projects must be nonempty")

    actual_projects: set[str] = set()
    findings: list[str] = []
    for project_index, project_value in enumerate(projects):
        project_location = f"report.projects[{project_index}]"
        project = require_object(project_value, project_location)
        require_keys(project, project_location, {"path"}, {"frameworks"})
        project_path = normalized_project_path(root, project["path"], f"{project_location}.path")
        if project_path in actual_projects:
            fail(f"duplicate project in vulnerability report: {project_path}")
        actual_projects.add(project_path)

        if "frameworks" not in project:
            continue
        frameworks = require_array(project["frameworks"], f"{project_location}.frameworks")
        if not frameworks:
            fail(f"{project_location}.frameworks must be omitted rather than empty")
        seen_frameworks: set[str] = set()
        for framework_index, framework_value in enumerate(frameworks):
            framework_location = f"{project_location}.frameworks[{framework_index}]"
            framework = require_object(framework_value, framework_location)
            require_keys(
                framework,
                framework_location,
                {"framework"},
                {"topLevelPackages", "transitivePackages"},
            )
            framework_name = require_string(framework["framework"], f"{framework_location}.framework")
            if framework_name in seen_frameworks:
                fail(f"duplicate framework for {project_path}: {framework_name}")
            seen_frameworks.add(framework_name)

            package_count = 0
            for group in ("topLevelPackages", "transitivePackages"):
                if group not in framework:
                    continue
                packages = require_array(framework[group], f"{framework_location}.{group}")
                if not packages:
                    fail(f"{framework_location}.{group} must be omitted rather than empty")
                for package_index, package_value in enumerate(packages):
                    package_id, resolved_version, vulnerability_count = inspect_package(
                        package_value,
                        f"{framework_location}.{group}[{package_index}]",
                    )
                    package_count += 1
                    findings.append(
                        f"{project_path}:{framework_name}:{package_id}@{resolved_version} "
                        f"({vulnerability_count})"
                    )
            if package_count == 0:
                fail(f"{framework_location} contains no vulnerable packages")

    if actual_projects != expected_projects:
        missing = sorted(expected_projects - actual_projects)
        unexpected = sorted(actual_projects - expected_projects)
        fail(
            "report.projects does not exactly match the release project set; "
            f"missing={missing}, unexpected={unexpected}"
        )
    return findings


def main(arguments: list[str]) -> int:
    if len(arguments) != 3:
        print(
            "FAIL: usage: audit-vulnerabilities.py REPORT.json REPOSITORY_ROOT RELEASE_POLICY.json",
            file=sys.stderr,
        )
        return 2

    report_path = pathlib.Path(arguments[0])
    repository_root = pathlib.Path(arguments[1]).resolve()
    policy_path = pathlib.Path(arguments[2])
    try:
        report = parse_json_document(report_path.read_text(encoding="utf-8"), "report")
        policy = require_object(
            parse_json_document(policy_path.read_text(encoding="utf-8"), "release policy"),
            "release policy",
        )
        expected_project_list = [
            require_string(value, f"release policy.vulnerabilityAuditProjects[{index}]")
            for index, value in enumerate(
                require_array(policy.get("vulnerabilityAuditProjects"), "release policy.vulnerabilityAuditProjects")
            )
        ]
        expected_source_list = [
            require_string(value, f"release policy.vulnerabilitySources[{index}]")
            for index, value in enumerate(
                require_array(policy.get("vulnerabilitySources"), "release policy.vulnerabilitySources")
            )
        ]
        expected_projects = set(expected_project_list)
        expected_sources = set(expected_source_list)
        if (
            not expected_projects
            or not expected_sources
            or len(expected_projects) != len(expected_project_list)
            or len(expected_sources) != len(expected_source_list)
        ):
            fail("release vulnerability project and source sets must be nonempty")
        findings = audit_report(report, repository_root, expected_projects, expected_sources)
    except (AuditError, json.JSONDecodeError, OSError) as ex:
        print(f"FAIL: invalid NuGet vulnerability report: {ex}", file=sys.stderr)
        return 1

    if findings:
        print("FAIL: known vulnerable dependencies: " + ", ".join(findings), file=sys.stderr)
        return 1
    print(
        "Vulnerability audit passed: NuGet reported no known vulnerable direct or transitive package "
        "for the exact release project and source set."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
