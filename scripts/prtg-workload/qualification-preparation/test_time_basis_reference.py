import json
import tempfile
import unittest
from pathlib import Path

from e2_preflight import is_safe_time_basis_reference, validate_monitoring_plan_reference


class TimeBasisReferenceContractTests(unittest.TestCase):
    def test_length_boundaries_match_dotnet_utf16_string_length(self):
        self.assertTrue(is_safe_time_basis_reference("x"))
        self.assertTrue(is_safe_time_basis_reference("x" * 128))
        self.assertFalse(is_safe_time_basis_reference("x" * 129))
        self.assertTrue(is_safe_time_basis_reference("😀" * 64))
        self.assertFalse(is_safe_time_basis_reference("😀" * 65))

    def test_whitespace_controls_and_url_separators_are_rejected(self):
        for value in ("", " \t", "a\x00b", "a\x7fb", "a\x85b",
                      "a://b", "a/b", "a\\b", "a?b", "a#b", "a@b"):
            with self.subTest(value=repr(value)):
                self.assertFalse(is_safe_time_basis_reference(value))

    def test_python_plan_preflight_uses_the_same_safe_reference_contract(self):
        with tempfile.TemporaryDirectory(prefix="e2-time-reference-") as folder:
            path = Path(folder) / "monitoring-plan.json"
            plan = {"schema": "round54-e2-monitoring-plan-v1", "timeBasisEvidenceReference": "r" * 128}
            path.write_text(json.dumps(plan), encoding="utf-8")
            validate_monitoring_plan_reference(path)
            for invalid in ("r" * 129, "r://x", "r\\x", "r\nx"):
                plan["timeBasisEvidenceReference"] = invalid
                path.write_text(json.dumps(plan), encoding="utf-8")
                with self.subTest(value=repr(invalid)), self.assertRaisesRegex(ValueError, "safe-reference contract"):
                    validate_monitoring_plan_reference(path)


if __name__ == "__main__":
    unittest.main()
