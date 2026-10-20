"""Read-only R09 contracts. Never opens user documents or company files."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]

class OneFileLegacyContracts(unittest.TestCase):
    def test_company_defaults_to_onedir_and_preserves_legacy_onefile(self):
        s = (ROOT/'BUILD_ON_WINDOWS_COMPANY.cmd').read_text('ascii')
        self.assertIn('BUILD_RELEASE_INSTALLER.cmd', s)
        self.assertIn('BUILD_RELEASE_ONEDIR.cmd', (ROOT/'BUILD_RELEASE_INSTALLER.cmd').read_text('ascii'))
        self.assertIn('--company', s)
        self.assertNotIn('BUILD_RELEASE_ONEFILE.cmd', s)
        self.assertTrue((ROOT/'BUILD_RELEASE_ONEFILE.cmd').is_file())
    def test_bundle_self_contained_and_no_unsafe_trimming(self):
        s = (ROOT/'BUILD_RELEASE_ONEFILE.cmd').read_text('ascii')
        for token in ('--self-contained true', '-p:PublishSingleFile=true',
                      '-p:IncludeNativeLibrariesForSelfExtract=true',
                      '-p:PublishTrimmed=false', '-p:EnableCompressionInSingleFile=false',
                      '-p:DebugSymbols=false', '--runtime win-x64'):
            self.assertIn(token, s)
    def test_no_document_tests_or_implicit_install(self):
        s = (ROOT/'BUILD_RELEASE_ONEFILE.cmd').read_text('ascii')
        for forbidden in ('LogicTests.csproj', 'INSTALL_OLLAMA.cmd', 'OllamaSetup.exe',
                          'UiScreenshot', r'source\tests\LogicTests\Fixtures'):
            self.assertNotIn(forbidden, s)
        self.assertIn('COMPANY_SOURCE_ONLY.flag', s)
    def test_release_has_no_source_folder(self):
        s = (ROOT/'BUILD_RELEASE_ONEFILE.cmd').read_text('ascii')
        self.assertIn('Sunrise File Manager.exe', s)
        self.assertIn(r'"%ZIP_DIR%\_licenses"', s)
        self.assertNotIn(r'robocopy "%ROOT%\source" "%ZIP_DIR%"', s)
        self.assertIn('if exist "%%~F" goto :fail_extra_payload', s)
        self.assertTrue(any(name in s for name in ('R09_RELEASE_RESULT.txt', 'R10_RELEASE_RESULT.txt')), 'The current release result log must be present')
    def test_run_shortcut_no_sdk_needed(self):
        s = (ROOT/'RUN_FILE_MANAGER.cmd').read_text('ascii')
        self.assertIn(r'%LOCALAPPDATA%\Programs\Sunrise File Manager\Sunrise.FileManager.exe', s)
        self.assertNotIn('dotnet', s.lower())
    def test_original_csharp_and_wpf_still_present(self):
        self.assertTrue((ROOT/'source/Sunrise.FileManager/Sunrise.FileManager.csproj').is_file())
        self.assertTrue((ROOT/'source/Sunrise.FileManager/Controls/ExplorerPane.xaml').is_file())
        self.assertTrue((ROOT/'source/Sunrise.FileManager/Services/AiOllamaService.cs').is_file())
        self.assertTrue((ROOT/'COMPANY_SOURCE_ONLY.flag').is_file())

if __name__ == '__main__': unittest.main()
