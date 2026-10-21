"""Regression: WPF generated temp project must explicitly resolve IOException.

The 1.1.0 first Windows build (SDK 10.0.301) returned CS0246 in ThemeManager.cs.
"""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "source/Sunrise.FileManager"

class ExplicitIoImports(unittest.TestCase):
    def test_theme_manager_ioexception_resolves_without_generated_global_usings(self):
        code = (SRC / "Services/ThemeManager.cs").read_text(encoding="utf-8")
        self.assertRegex(code, r"(?m)^using System\.IO;$")
        self.assertIn("catch (IOException)", code)

    def test_unqualified_ioexception_handlers_in_application_code_have_explicit_import(self):
        for file in SRC.rglob("*.cs"):
            code = file.read_text(encoding="utf-8")
            if re.search(r"\bcatch\s*\(\s*(?:IOException|FileNotFoundException|DirectoryNotFoundException)\b", code):
                with self.subTest(file=str(file.relative_to(SRC))):
                    self.assertRegex(code, r"(?m)^using System\.IO;$")

    def test_installer_and_control_release_remain_untouched(self):
        self.assertEqual((ROOT / "installer/RELEASE_VERSION.txt").read_text(encoding="ascii").strip(), "1.2.0")
        self.assertEqual(__import__("json").loads((ROOT / "latest.json").read_text(encoding="utf-8"))["latest_version"], "1.0.0")

if __name__ == "__main__":
    unittest.main()
