#!/usr/bin/env python3

import importlib.util
import pathlib
import unittest


SCRIPT = pathlib.Path(__file__).parents[2] / "scripts" / "audit-vulnerabilities.py"
SPEC = importlib.util.spec_from_file_location("audit_vulnerabilities", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class VulnerabilityReportTests(unittest.TestCase):
    root = pathlib.Path("/standalone-plugin")
    projects = {"src/Provider.csproj", "tests/Provider.Tests.csproj"}
    sources = {"https://api.nuget.org/v3/index.json"}

    def clean_report(self):
        return {
            "version": 1,
            "parameters": "--vulnerable --include-transitive",
            "sources": sorted(self.sources),
            "projects": [
                {"path": str(self.root / project)} for project in sorted(self.projects)
            ],
        }

    def audit(self, report):
        return MODULE.audit_report(report, self.root, self.projects, self.sources)

    def assert_rejected(self, report):
        with self.assertRaises(MODULE.AuditError):
            self.audit(report)

    def test_accepts_genuine_no_vulnerability_shape(self):
        self.assertEqual([], self.audit(self.clean_report()))

    def test_reports_a_well_formed_vulnerable_package(self):
        report = self.clean_report()
        report["projects"][0]["frameworks"] = [{
            "framework": "net10.0",
            "topLevelPackages": [{
                "id": "Example.Package",
                "requestedVersion": "[1.0.0, )",
                "resolvedVersion": "1.0.0",
                "vulnerabilities": [{
                    "severity": "High",
                    "advisoryurl": "https://example.invalid/advisory",
                }],
            }],
        }]
        self.assertEqual(1, len(self.audit(report)))

    def test_rejects_empty_or_wrong_top_level_json(self):
        self.assert_rejected({})
        self.assert_rejected([])

    def test_strict_json_parser_rejects_duplicates_and_non_json_constants(self):
        with self.assertRaises(ValueError):
            MODULE.parse_json_document("{", "report")
        with self.assertRaises(ValueError):
            MODULE.parse_json_document("", "report")
        with self.assertRaises(MODULE.AuditError):
            MODULE.parse_json_document('{"version": 1, "version": 1}', "report")
        with self.assertRaises(MODULE.AuditError):
            MODULE.parse_json_document('{"version": NaN}', "report")

    def test_rejects_wrong_parameters_sources_and_project_set(self):
        for key, value in (
            ("parameters", "--include-transitive"),
            ("parameters", "--vulnerable"),
            ("parameters", "--vulnerable --include-transitive --vulnerable"),
            ("sources", []),
            ("sources", ["https://mirror.invalid/v3/index.json"]),
            ("sources", ["https://api.nuget.org/v3/index.json"] * 2),
            ("projects", [{"path": str(self.root / "src/Provider.csproj")}]),
        ):
            with self.subTest(key=key, value=value):
                report = self.clean_report()
                report[key] = value
                self.assert_rejected(report)

    def test_rejects_schema_drift_and_malformed_nested_shapes(self):
        candidates = []
        report = self.clean_report()
        report["unexpected"] = True
        candidates.append(report)

        report = self.clean_report()
        report["projects"][0]["frameworks"] = {}
        candidates.append(report)

        report = self.clean_report()
        report["projects"][0]["frameworks"] = [{"framework": "net10.0"}]
        candidates.append(report)

        report = self.clean_report()
        report["projects"][0]["frameworks"] = [{
            "framework": "net10.0",
            "transitivePackages": [{
                "id": "Example.Package",
                "resolvedVersion": "1.0.0",
                "vulnerabilities": [],
            }],
        }]
        candidates.append(report)

        for report in candidates:
            with self.subTest(report=report):
                self.assert_rejected(report)

    def test_rejects_duplicate_and_escaping_project_paths(self):
        duplicate = self.clean_report()
        duplicate["projects"].append(duplicate["projects"][0].copy())
        self.assert_rejected(duplicate)

        escaping = self.clean_report()
        escaping["projects"][0]["path"] = "/outside/Provider.csproj"
        self.assert_rejected(escaping)


if __name__ == "__main__":
    unittest.main()
