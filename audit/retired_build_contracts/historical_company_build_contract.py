"""Static contract checks; does not execute, read, or parse document fixtures."""
from pathlib import Path
import re
import unittest

ROOT=Path(__file__).resolve().parents[1]
CMD=(ROOT/'BUILD_DEV_FULL_SOURCE.cmd').read_text(encoding='ascii')
WRAPPER=(ROOT/'BUILD_ON_WINDOWS_COMPANY.cmd').read_text(encoding='ascii')
CSPROJ=(ROOT/'source/tests/LogicTests/LogicTests.csproj').read_text(encoding='utf-8')

class CompanyBuildContract(unittest.TestCase):
    def test_company_entrypoint(self):
        self.assertIn('BUILD_RELEASE_INSTALLER.cmd" --company',WRAPPER)
        self.assertIn('BUILD_RELEASE_ONEDIR.cmd" --company', (ROOT/'BUILD_RELEASE_INSTALLER.cmd').read_text(encoding='ascii'))
        release=(ROOT/'BUILD_RELEASE_ONEDIR.cmd').read_text(encoding='ascii')
        self.assertIn('COMPANY_SOURCE_ONLY.flag',release)
        self.assertIn('--self-contained true',release)
        self.assertIn('PublishSingleFile=false',release)
        self.assertIn('set "COMPANY_MODE=0"',CMD)
        self.assertIn('if /I "%~1"=="--company" set "COMPANY_MODE=1"',CMD)
    def test_tests_skipped_not_run_in_company_branch(self):
        begin=CMD.index('if "%COMPANY_MODE%"=="1" (\n    echo [1/4] SKIPPED:')
        end=CMD.index('echo.\n\nset "FAILED_STAGE=WPF_BUILD"',begin)
        branch=CMD[begin:end]
        self.assertIn('SKIPPED=COMPANY_DOCUMENT_ACCESS_POLICY',branch)
        self.assertRegex(branch,r'\) else \(\s*echo \[1/4\] Running')
        self.assertEqual(branch.count('dotnet'),0)  # command uses DOTNET_EXE, so assert no separate path
        self.assertEqual(branch.count('"%DOTNET_EXE%" run --project'),1)
        self.assertLess(branch.index(') else ('),branch.index('"%DOTNET_EXE%" run --project'))
    def test_fixture_not_copied_by_company_test_build(self):
        self.assertIn("'$(SunriseCompanyBuild)' != '1' And Exists('Fixtures/river_report.pdf')", CSPROJ)
        self.assertIn('Fixtures/river_report.pdf',CSPROJ)
    def test_company_packing_excludes_document_bytes(self):
        self.assertIn(' /XD bin obj .vs Fixtures /XF *.pdf *.hwp *.hwpx *.doc *.docx',CMD)
        self.assertIn('if exist "%STAGE_DIR%\\source\\tests\\LogicTests\\Fixtures\\river_report.pdf" goto :fail_package',CMD)
        self.assertIn('findstr /I /R /C:"\\.pdf$"',CMD)
    def test_publishing_remains_required_and_self_contained(self):
        self.assertIn('build "%ROOT%\\source\\Sunrise.FileManager\\Sunrise.FileManager.csproj"',CMD)
        self.assertIn('--runtime win-x64 --self-contained true',CMD)
        for lib in ('hostfxr.dll','coreclr.dll','PresentationFramework.dll'):
            self.assertIn(lib,CMD)
    def test_company_output_separate_from_standard(self):
        self.assertIn('SunriseFileManager_Portable_COMPANY_R04',CMD)
        self.assertIn('SunriseFileManager_Portable_R02',CMD)
        self.assertIn('MODE=%COMPANY_MODE%',CMD)
    def test_no_powershell_or_document_content_scanning(self):
        self.assertNotIn('powershell',CMD.lower())
        self.assertNotIn('pdftotext',CMD.lower())
        self.assertNotIn('ocr',CMD.lower())
        self.assertNotIn('AiDocumentSearchService',CMD)
    def test_company_archive_prevents_accidental_normal_test_runs(self):
        self.assertIn('if exist "%~dp0COMPANY_SOURCE_ONLY.flag" set "COMPANY_MODE=1"',CMD)
        self.assertIn('"%STAGE_DIR%\\COMPANY_SOURCE_ONLY.flag"',CMD)
        ui=(ROOT/"BUILD_AND_CAPTURE_WINDOWS.cmd").read_text(encoding="ascii")
        self.assertIn('if exist "%~dp0COMPANY_SOURCE_ONLY.flag"',ui)
    def test_cmd_ascii_crlf(self):
        for f in ('BUILD_ON_WINDOWS_COMPANY.cmd','BUILD_ON_WINDOWS.cmd','BUILD_DEV_FULL_SOURCE.cmd','BUILD_RELEASE_ONEDIR.cmd','BUILD_RELEASE_ONEFILE.cmd','RUN_FILE_MANAGER.cmd'):
            b=(ROOT/f).read_bytes()
            self.assertTrue(b.isascii())
            self.assertNotIn(b'\n',b.replace(b'\r\n',b''))

if __name__=='__main__': unittest.main()
