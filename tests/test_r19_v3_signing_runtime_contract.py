"""R05: no custom signing EXE/PS1, keep Windows native signing gates."""
from pathlib import Path
import unittest
ROOT=Path(__file__).resolve().parents[1]
ONEDIR=(ROOT/'BUILD_RELEASE_ONEDIR.cmd').read_text('ascii')
INSTALL=(ROOT/'BUILD_RELEASE_INSTALLER.cmd').read_text('ascii')
NATIVE=(ROOT/'build-tools/FindSunriseCertificate.cmd').read_text('ascii')
CS=(ROOT/'source/build-tools/Sunrise.Signing/Sunrise.Signing.csproj').read_text('utf-8')
class SigningRegression(unittest.TestCase):
 def test_no_custom_signing_executable_or_powershell(self):
  self.assertNotIn('Sunrise.Signing.exe',ONEDIR+INSTALL)
  self.assertNotIn('powershell.exe',ONEDIR+INSTALL+NATIVE)
  self.assertNotIn('.ps1',ONEDIR+INSTALL+NATIVE)
  self.assertFalse((ROOT/'build-tools/Sunrise.GroupSigning.ps1').exists())
  self.assertIn('<OutputType>Library</OutputType>',CS)
 def test_signatures_required(self):
  self.assertIn('EnsureSunriseGroupSigning.cmd',ONEDIR)
  self.assertIn('/fd SHA256 /sha1 "%CERT_THUMBPRINT%" /n "%SUNRISE_CERT_SUBJECT_CN%" /s My',ONEDIR)
  self.assertIn('/SSunriseGroup=',INSTALL)
  self.assertIn('SignedUninstaller=yes',(ROOT/'installer/SunriseFileManager.iss').read_text())
  self.assertIn('verify /pa /v',ONEDIR)
  self.assertIn('verify /pa /v',INSTALL)
 def test_no_policy_bypass_or_auto_cert(self):
  for s in (ONEDIR,INSTALL,NATIVE):
   for bad in ('-ExecutionPolicy Bypass','Add-MpPreference','Set-MpPreference','Invoke-WebRequest','certreq.exe'):
    self.assertNotIn(bad,s)
  self.assertIn('RequestType = Cert',(ROOT/'build-tools/Sunrise.GroupCodeSigning.inf').read_text())
 def test_no_customer_document_access(self):
  for s in (ONEDIR,INSTALL,NATIVE):
   self.assertNotIn('LogicTests.csproj',s)
   self.assertNotIn('UiSmokeTests',s)
if __name__=='__main__': unittest.main()
