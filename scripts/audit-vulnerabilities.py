#!/usr/bin/env python3
"""Fail when dotnet package list JSON contains a known vulnerability."""

from __future__ import annotations

import json
import pathlib
import sys


if len(sys.argv) != 2:
    raise SystemExit("FAIL: usage: audit-vulnerabilities.py REPORT.json")

report = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))
findings: list[str] = []
for project in report.get("projects", []):
    for framework in project.get("frameworks", []):
        for group in ("topLevelPackages", "transitivePackages"):
            for package in framework.get(group, []):
                vulnerabilities = package.get("vulnerabilities", [])
                if vulnerabilities:
                    findings.append(
                        f"{project.get('path', '<project>')}:{package.get('id', '<package>')}@"
                        f"{package.get('resolvedVersion', '<version>')} ({len(vulnerabilities)})"
                    )

if findings:
    raise SystemExit("FAIL: known vulnerable dependencies: " + ", ".join(findings))
print("Vulnerability audit passed: NuGet reported no known vulnerable direct or transitive package.")
