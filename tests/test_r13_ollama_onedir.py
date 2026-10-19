"""R13 company-safe read-only static contracts. Never opens work documents."""
from pathlib import Path
import unittest
ROOT=Path(__file__).resolve().parents[1]
SRC=ROOT/'source'/'Sunrise.FileManager'

class R13OneDirOllamaContracts(unittest.TestCase):
    def test_company_entrypoint_is_onedir(self):
        for name in ('BUILD_ON_WINDOWS_COMPANY.cmd','BUILD_ON_WINDOWS.cmd'):
            data=(ROOT/name).read_text('ascii')
            self.assertIn('BUILD_' + ('RELEASE_INSTALLER.cmd' if name=='BUILD_ON_WINDOWS_COMPANY.cmd' else 'ON_WINDOWS_COMPANY.cmd'), data)
            self.assertNotIn('BUILD_RELEASE_ONEFILE.cmd', data)
        self.assertIn('BUILD_RELEASE_ONEDIR.cmd', (ROOT/'BUILD_RELEASE_INSTALLER.cmd').read_text('ascii'))

    def test_onedir_wpf_compiles_without_fixture_access(self):
        script=(ROOT/'BUILD_RELEASE_ONEDIR.cmd').read_text('ascii')
        for needed in ('publish "%PROJECT%"', '-p:PublishSingleFile=false',
                       '--self-contained true', '--runtime win-x64',
                       '-p:SunriseCompanyBuild=%COMPANY_MODE%',
                       'COMPANY_SOURCE_ONLY.flag','SKIPPED',
                       'R14_wpf_publish.log', 'R14_BUILD_RESULT.txt'):
            self.assertIn(needed,script)
        for forbidden in ('LogicTests.csproj','DocumentSearchService','OllamaSetup.exe','BUILD_RELEASE_ONEFILE.cmd',
                          'robocopy "%ROOT%\\source" "%STAGE%"'):
            self.assertNotIn(forbidden,script)

    def test_customer_root_is_compact_with_internal_runtime(self):
        script=(ROOT/'BUILD_RELEASE_ONEDIR.cmd').read_text('ascii')
        self.assertIn('Sunrise File Manager.cmd',script)
        self.assertIn('app\\Sunrise.FileManager.exe',script)
        for dep in ('app\\hostfxr.dll','app\\coreclr.dll','app\\PresentationFramework.dll'):
            self.assertIn(dep,script)
        self.assertIn('findstr /I /R /C:"\\.cs$"',script)
        self.assertIn('SunriseFileManager_OneDir_Windows_x64_R14.zip',script)

    def test_ollama_official_brand_ui(self):
        for file in ('Controls/OllamaSetupDialog.xaml', 'Controls/OllamaSetupDialog.xaml.cs',
                     'Controls/AiCommandPanel.xaml','Services/AiOllamaService.cs'):
            content=(SRC/file).read_text('utf-8')
            self.assertNotIn('ollema',content.lower())
        self.assertIn('Ollama', (SRC/'Controls/OllamaSetupDialog.xaml').read_text('utf-8'))
        self.assertIn('https://github.com/ollama/ollama', (SRC/'Services/AiOllamaService.cs').read_text('utf-8'))

    def test_modular_sources_unchanged(self):
        for file in ('Features/Explorer/ExplorerPane.FileOperations.cs',
                     'Features/Explorer/ExplorerPane.ViewAndFiltering.cs',
                     'Features/Workspace/WorkspaceSplitLayoutEngine.cs',
                     'Services/FileMemoStore.cs','Services/AiOllamaService.cs',
                     'Controls/FilePreviewPanel.xaml'):
            self.assertTrue((SRC/file).is_file())

    def test_cmd_ascii_crlf(self):
        for file in ('BUILD_RELEASE_ONEDIR.cmd', 'BUILD_ON_WINDOWS_COMPANY.cmd',
                     'BUILD_ON_WINDOWS.cmd','BUILD_DEV_FULL_SOURCE.cmd','RUN_FILE_MANAGER.cmd'):
            data=(ROOT/file).read_bytes()
            self.assertTrue(data.isascii(),file)
            self.assertNotIn(b'\n',data.replace(b'\r\n',b''),file)

if __name__=='__main__': unittest.main()
