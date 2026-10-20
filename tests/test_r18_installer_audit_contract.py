"""R18 target-specific source contracts: validates installer pipeline without running customer data."""
from pathlib import Path
import unittest
ROOT = Path(__file__).resolve().parents[1]
BUILD = (ROOT / "BUILD_RELEASE_INSTALLER.cmd").read_text(encoding="ascii")
ONEDIR = (ROOT / "BUILD_RELEASE_ONEDIR.cmd").read_text(encoding="ascii")
ISS = (ROOT / "installer" / "SunriseFileManager.iss").read_text(encoding="utf-8")
class R18ReleaseGate(unittest.TestCase):
    def test_installer_uses_signed_payload_mode(self):
        self.assertIn('BUILD_RELEASE_ONEDIR.cmd" --company --no-pause --installer-payload', BUILD)
        self.assertIn('set "APP_DIR=%DIST%\\_R18_installer_payload\\app"', BUILD)
        self.assertIn('if "%INSTALLER_PAYLOAD%"=="1" goto :installer_payload_ready', ONEDIR)
        self.assertIn('FORMAT=SIGNED_INSTALLER_PAYLOAD', ONEDIR)
        self.assertLess(ONEDIR.index('goto :installer_payload_ready'), ONEDIR.index('tar.exe -a -c'))
    def test_manual_portable_release_remains(self):
        self.assertIn('SunriseFileManager_OneDir_Windows_x64_R14.zip', ONEDIR)
        self.assertIn('tar.exe -a -c', ONEDIR)
        self.assertIn('set "INSTALLER_PAYLOAD=0"', ONEDIR)
    def test_version_filename_sanitized(self):
        self.assertIn('call :validate_release_version', BUILD)
        self.assertIn('for /f "usebackq delims="', BUILD)
        self.assertIn('REASON=%VERSION_PROBLEM%', BUILD)
    def test_appmutex_from_real_application(self):
        app=(ROOT / 'source/Sunrise.FileManager/App.xaml.cs').read_text()
        self.assertIn(r'Local\SunriseFileManagerCEF',app)
        self.assertIn(r'AppMutex=Local\SunriseFileManagerCEF',ISS)
        self.assertNotIn('FindWindowByWindowName(',ISS)
    def test_user_data_protected(self):
        self.assertIn('PrivilegesRequired=lowest',ISS)
        self.assertIn('UsePreviousAppDir=yes',ISS)
        self.assertNotIn('[UninstallDelete]',ISS)
        self.assertNotIn('[InstallDelete]',ISS)
    def test_installer_signed_and_fails_closed(self):
        self.assertIn('SignTool=SunriseGroup',ISS)
        self.assertIn('SignedUninstaller=yes',ISS)
        self.assertIn('R15_verify_setup.log',BUILD)
        self.assertIn('STATUS=FAILED',BUILD)
        self.assertIn('STATUS=SUCCESS',BUILD)
if __name__=='__main__': unittest.main()
