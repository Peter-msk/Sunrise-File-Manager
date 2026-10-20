"""R15 installer contract: purely textual, no user documents / fixtures opened."""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]
ISS = (ROOT / 'installer/SunriseFileManager.iss').read_text(encoding='utf-8')
BUILD = (ROOT / 'BUILD_RELEASE_INSTALLER.cmd').read_text(encoding='utf-8')
BACKEND = (ROOT / 'BUILD_RELEASE_ONEDIR.cmd').read_text(encoding='utf-8')
ENTRY = (ROOT / 'BUILD_ON_WINDOWS_COMPANY.cmd').read_text(encoding='utf-8')


class InstallerContractTests(unittest.TestCase):
    def test_default_entry_uses_installer(self):
        self.assertIn('BUILD_RELEASE_INSTALLER.cmd', ENTRY)
        self.assertNotIn('BUILD_RELEASE_ONEFILE.cmd', ENTRY)
        self.assertIn('BUILD_ON_WINDOWS_COMPANY.cmd', (ROOT / 'BUILD_ON_WINDOWS.cmd').read_text())

    def test_stable_app_id_and_version(self):
        self.assertRegex(ISS, r'(?m)^AppId=\{\{[0-9A-F-]{36}\}$')
        self.assertIn('AppVersion={#ApplicationVersion}', ISS)
        self.assertRegex((ROOT / 'installer/RELEASE_VERSION.txt').read_text(encoding='utf-8').strip(), r'^[0-9]+\.[0-9]+\.[0-9]+$')
        self.assertIn('SUNRISE_INSTALLER_VERSION', BUILD)

    def test_non_admin_and_start_menu_uninstall(self):
        self.assertIn('PrivilegesRequired=lowest', ISS)
        self.assertIn('DefaultDirName={localappdata}\\Programs\\Sunrise File Manager', ISS)
        self.assertIn('Name: "{userprograms}\\Sunrise File Manager"', ISS)
        self.assertIn('Name: "{userdesktop}\\Sunrise File Manager"', ISS)
        self.assertIn('Tasks: desktopicon', ISS)
        self.assertIn('UninstallDisplayIcon=', ISS)
        self.assertIn('UsePreviousAppDir=yes', ISS)
        self.assertNotIn('OneDir_R14', (ROOT / 'RUN_FILE_MANAGER.cmd').read_text())

    def test_source_and_user_data_are_outside_installer(self):
        self.assertIn('Source: "{#ReleaseSource}\\*"', ISS)
        self.assertNotIn('source\\tests', ISS.lower())
        self.assertNotIn('session.json', ISS.lower())
        self.assertNotIn('\\#memo', ISS.lower())
        self.assertNotIn('[InstallDelete]', ISS)
        self.assertNotIn('[UninstallDelete]', ISS)
        self.assertIn('COMPANY_SOURCE_ONLY.flag', BUILD)

    def test_r14_signed_payload_and_company_mode(self):
        self.assertIn('BUILD_RELEASE_ONEDIR.cmd" --company --no-pause --installer-payload', BUILD)
        self.assertIn('STATUS=SUCCESS', BUILD)
        self.assertIn('Sunrise.FileManager.exe', BUILD)
        self.assertIn('Sunrise.FileManager.dll', BUILD)
        self.assertIn('-p:PublishSingleFile=false', BACKEND)
        self.assertIn('-p:Version=%SUNRISE_RELEASE_VERSION%', BACKEND)
        self.assertIn('SKIPPED', BACKEND)

    def test_setup_and_uninstaller_signed_during_compile(self):
        self.assertIn('SignTool=SunriseGroup', ISS)
        self.assertIn('SignedUninstaller=yes', ISS)
        self.assertIn('/SSunriseGroup=$q%SIGNTOOL%$q sign', BUILD)
        self.assertIn('/sha1 %CERT_THUMBPRINT%', BUILD)
        self.assertIn('/tr %TIMESTAMP_URL%', BUILD)
        self.assertIn('R15_verify_setup.log', BUILD)
        self.assertIn('R15_SIGNED_SETUP_SHA256.txt', BUILD)
        self.assertIn('STATUS=FAILED', BUILD)

    def test_update_requires_user_exit_and_no_auto_run(self):
        self.assertIn('CloseApplications=no', ISS)
        self.assertNotIn('FindWindowByWindowName(', ISS)
        self.assertIn('AppMutex=Local\\SunriseFileManagerCEF', ISS)
        self.assertIn('RestartApplications=no', ISS)
        self.assertIn('Flags: nowait postinstall skipifsilent unchecked', ISS)
        self.assertNotIn('uninsneveruninstall', ISS.lower())

    def test_licenses_included_in_setup(self):
        self.assertIn('Source: "..\\source\\licenses\\*"', ISS)
        for f in ('PDFPIG_LICENSE.txt', 'DOTNET_RUNTIME_LICENSE.txt',
                  'WINDOWSDESKTOP_RUNTIME_LICENSE.txt'):
            self.assertTrue((ROOT / 'source' / 'licenses' / f).is_file(), f)

    def test_app_mutex_matches_application(self):
        self.assertIn('AppMutex=Local\\SunriseFileManagerCEF', ISS)
        app = (ROOT / 'source/Sunrise.FileManager/App.xaml.cs').read_text(encoding='utf-8')
        self.assertIn(r'Local\SunriseFileManagerCEF', app)

    def test_active_installer_entry_is_consistent(self):
        active = (ROOT / 'BUILD_ON_WINDOWS.cmd').read_text(encoding='ascii')
        self.assertIn('BUILD_ON_WINDOWS_COMPANY.cmd', active)
        self.assertIn('BUILD_RELEASE_INSTALLER.cmd', ENTRY)
        self.assertIn('BUILD_RELEASE_ONEDIR.cmd', BUILD)

    def test_no_automatic_installer_download(self):
        self.assertNotIn('winget install', BUILD.lower())
        self.assertIn('EnsureSunriseGroupSigning.cmd', BUILD)
        self.assertNotIn('-ExecutionPolicy', BUILD)
        self.assertNotIn('Invoke-WebRequest', BUILD)
        self.assertNotIn('curl ', BUILD.lower())
        self.assertNotIn('http', ISS.lower())


if __name__ == '__main__':
    unittest.main()
