"""Regression for R19 installer version preflight (no Windows execution)."""
from pathlib import Path
import re
import unittest
ROOT = Path(__file__).resolve().parents[1]
CMD = (ROOT / "BUILD_RELEASE_INSTALLER.cmd").read_text(encoding="ascii")
VERSION = (ROOT / "installer/RELEASE_VERSION.txt").read_text(encoding="ascii").strip()
class VersionHotfixContract(unittest.TestCase):
    def test_release_file_uses_supported_three_segment_version(self):
        self.assertRegex(VERSION, r"^[0-9]+\.[0-9]+\.[0-9]+$")
    def test_missing_files_and_invalid_values_have_distinct_errors(self):
        for s in ("VERSION_FILE_MISSING", "INNO_SCRIPT_MISSING", "VERSION_FILE_EMPTY", "VERSION_INVALID_EXPECTED_THREE_NUMERIC_SEGMENTS"):
            self.assertIn(s, CMD)
    def test_version_read_is_independent_of_findstr_regex(self):
        self.assertIn('for /f "usebackq delims=" %%V in ("%VERSION_FILE%")', CMD)
        self.assertIn('call :validate_release_version "%SFM_APP_VERSION%"', CMD)
        self.assertNotIn("in ('findstr /R /X", CMD)
    def test_digits_and_dot_structure_verification(self):
        self.assertIn('tokens=1-4 delims=.', CMD)
        self.assertIn(':check_decimal_segment', CMD)
        self.assertIn('delims=0123456789', CMD)
        self.assertIn('if not "%VRAW%"=="%VMAJOR%.%VMINOR%.%VPATCH%"', CMD)
    def test_build_failure_result_preserves_diagnostic_reason(self):
        self.assertIn('DETAIL=%VERSION_PROBLEM%', CMD)
        self.assertIn('R15_version_preflight.log', CMD)
    def test_company_build_and_signing_remain_required(self):
        self.assertIn('COMPANY_SOURCE_ONLY.flag', CMD)
        self.assertIn('SUNRISE_INSTALLER_PAYLOAD=', CMD)
        self.assertIn('SunriseGroup=$q%SIGNTOOL%$q', CMD)
    def test_no_document_test_invocation(self):
        self.assertNotIn('LogicTests.csproj', CMD)
        self.assertNotIn('UiSmokeTests', CMD)
if __name__ == '__main__': unittest.main()
