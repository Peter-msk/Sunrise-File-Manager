"""Static, source-level R19 memo contracts. Not proof of Windows runtime behavior."""
import pathlib
import unittest
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parents[1]
SRC = ROOT / "source" / "Sunrise.FileManager"


class MemoR19Contract(unittest.TestCase):
    def test_author_control(self):
        xml = (SRC / "Controls/FileInspector.xaml").read_text(encoding="utf-8")
        ET.fromstring(xml)
        self.assertIn('x:Name="MemoAuthorInput"', xml)
        self.assertIn('MaxLength="40"', xml)
        self.assertIn('MemoBackupsEnabled', xml)
        self.assertIn('RestoreMemoButton', xml)

    def test_pc_fallback(self):
        code = (SRC / "Controls/FileInspector.xaml.cs").read_text(encoding="utf-8")
        self.assertIn('Environment.UserName : MemoAuthorInput.Text.Trim()', code)
        self.assertIn('userName: author', code)

    def test_no_main_memo_encryption(self):
        code = (SRC / "Services/FileMemoStore.cs").read_text(encoding="utf-8")
        self.assertIn('SUNRISE_MEMO_V1', code)
        self.assertIn('GZipStream', code)
        self.assertIn('File.Move(temporary, path, overwrite: true)', code)

    def test_dpapi_local_only(self):
        audit = (SRC / "Services/MemoIntegrityBaseline.cs").read_text(encoding="utf-8")
        self.assertIn('CryptProtectData', audit)
        self.assertIn('CryptUnprotectData', audit)
        self.assertIn('LocalApplicationData', audit)
        self.assertIn('SHA256.HashData', audit)

    def test_opt_in_snapshots(self):
        code = (SRC / "Services/MemoLocalSnapshots.cs").read_text(encoding="utf-8")
        self.assertIn('File.ReadAllText(Setting).Trim() == "yes"', code)
        self.assertIn('MemoIntegrityBaseline.Protect', code)
        self.assertIn('DateTime.Now.ToString("yyyy-MM-dd")', code)
        self.assertIn('MaxEntriesPerMemo = 25', code)
        self.assertIn('250L * 1024 * 1024', code)

    def test_integrity_warning_not_hide(self):
        code = (SRC / "Services/FileMemoStore.cs").read_text(encoding="utf-8")
        self.assertIn('HasIntegrityWarning', code)
        self.assertIn('comparison.Warning', code)
        self.assertIn('return new MemoRecord(path, payload.UserName', code)

    def test_explicit_restore(self):
        ui = (SRC / "Controls/FileInspector.xaml.cs").read_text(encoding="utf-8")
        self.assertIn('MessageBoxButton.YesNo, MessageBoxImage.Warning', ui)
        self.assertIn('MemoLocalSnapshots.Restore(record.Path, selectedVersion, entry.FullPath)', ui)
        self.assertIn('MemoVersionPickerWindow', ui)
        self.assertTrue((SRC / 'Controls/MemoVersionPickerWindow.cs').exists())
        self.assertIn('AcceptCurrentAsReference', ui)

    def test_build_logic_tests_link(self):
        csproj = (ROOT / "source/tests/LogicTests/LogicTests.csproj").read_text(encoding="utf-8")
        self.assertIn('MemoIntegrityBaseline.cs', csproj)
        self.assertIn('MemoLocalSnapshots.cs', csproj)


if __name__ == "__main__":
    unittest.main()
