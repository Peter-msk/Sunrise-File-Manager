"""Static contract checks: no customer document access and no Windows tools executed."""
from pathlib import Path
import unittest
ROOT=Path(__file__).resolve().parents[1]
INSTALL=(ROOT/'BUILD_RELEASE_INSTALLER.cmd').read_text('ascii')
RESOLVE=(ROOT/'build-tools/FindInnoSetup.cmd').read_text('ascii')
CHECK=(ROOT/'CHECK_INSTALLER_BUILD_ENV.cmd').read_text('ascii')
SIGN=(ROOT/'build-tools/FindSignTool.cmd').read_text('ascii')
class InstallerPipelineR03(unittest.TestCase):
 def test_env_override_and_search_paths(self):
  self.assertIn('if defined SUNRISE_ISCC',RESOLVE)
  self.assertIn('where.exe ISCC.exe',RESOLVE)
  self.assertIn('LOCALAPPDATA',RESOLVE)
  self.assertIn('InstallLocation',RESOLVE)
  self.assertIn('reg.exe query',RESOLVE)
  self.assertIn('exit /b 1',RESOLVE)
 def test_shared_signtool_discovery(self):
  self.assertIn('SUNRISE_SIGNTOOL',SIGN)
  self.assertIn('where.exe signtool.exe',SIGN)
  self.assertIn('FindSignTool.cmd',INSTALL)
  self.assertIn('FindSignTool.cmd',CHECK)
  self.assertIn('FindSignTool.cmd',(ROOT/'BUILD_RELEASE_ONEDIR.cmd').read_text('ascii'))
 def test_current_supported_strict_x64_identifiers(self):
  iss=(ROOT/'installer/SunriseFileManager.iss').read_text('utf-8')
  self.assertIn('ArchitecturesAllowed=x64os',iss)
  self.assertIn('ArchitecturesInstallIn64BitMode=x64os',iss)
 def test_shared_search_utilized(self):
  self.assertIn('FindInnoSetup.cmd',INSTALL)
  self.assertIn('FindInnoSetup.cmd',CHECK)
  self.assertIn('R15_inno_probe.log',INSTALL)
  self.assertIn('R15_inno_probe.log',CHECK)
 def test_no_unnecessary_sdk_second_probe(self):
  self.assertNotIn('set "DOTNET_EXE="',INSTALL)
  self.assertIn('BUILD_RELEASE_ONEDIR.cmd',INSTALL)
 def test_no_security_bypass_or_auto_install(self):
  for s in (INSTALL,RESOLVE,CHECK,SIGN):
   for b in ('-ExecutionPolicy Bypass','winget install','Invoke-WebRequest','curl.exe','Add-MpPreference'):
    self.assertNotIn(b,s)
 def test_company_security_retained(self):
  self.assertIn('COMPANY_SOURCE_ONLY.flag',INSTALL)
  self.assertIn('SignedUninstaller=yes',(ROOT/'installer/SunriseFileManager.iss').read_text('utf-8'))
  self.assertNotIn('LogicTests.csproj',CHECK)
  self.assertNotIn('UiSmokeTests',CHECK)
 def test_double_click_check_remains_visible(self):
  self.assertIn('if "%NO_PAUSE%"=="0" pause',CHECK)
  self.assertIn('--no-pause',CHECK)
 def test_preflight_no_actions(self):
  self.assertNotIn('dotnet publish',CHECK)
  self.assertNotIn('signtool.exe" sign',CHECK)
  self.assertNotIn('ISCC%"',CHECK)
if __name__=='__main__': unittest.main()
