"""Regression guard for the CS1001/CS1002/CS8997 CSV quote literal breakage.

Source inspection only. The actual C# compilation and CSV behavior are checked
by BUILD_ON_WINDOWS.cmd / LogicTests on Windows with .NET 10 SDK.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
FILE = ROOT / 'Sunrise.FileManager/Features/FileSystem/FileActionsService.ArchiveAndShell.cs'
LOGIC_TEST = ROOT / 'tests/LogicTests/AiOllamaAndExplorerTests.cs'


class CsvExportBuildRegression(unittest.TestCase):
    def test_escaped_quote_literals_are_valid(self):
        content = FILE.read_text(encoding='utf-8-sig')
        self.assertIn('string safeName = "\\\"" + item.Name.Replace("\\\"", "\\\"\\\"") + "\\\"";', content)
        self.assertIn('string safePath = "\\\"" + item.FullPath.Replace("\\\"", "\\\"\\\"") + "\\\"";', content)
        self.assertNotIn('string safeName = """', content)
        self.assertNotIn('string safePath = """', content)

    def test_windows_logic_tests_cover_csv_quote_escaping(self):
        tests = LOGIC_TEST.read_text(encoding='utf-8-sig')
        self.assertIn('CSV export escapes commas and embedded quotes in names and paths', tests)
        self.assertIn('FileActionsService.FormatDirectoryListingCsv(root, new[] { specialCsvEntry })', tests)


if __name__ == '__main__':
    unittest.main()
