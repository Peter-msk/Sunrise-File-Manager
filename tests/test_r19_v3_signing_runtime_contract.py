"""V3 signing false-positive mitigation: pure source contracts, no documents or Windows executables."""
from pathlib import Path
import unittest
ROOT=Path(__file__).resolve().parents[1]
ONEDIR=(ROOT/'BUILD_RELEASE_ONEDIR.cmd').read_text('ascii')
INSTALL=(ROOT/'BUILD_RELEASE_INSTALLER.cmd').read_text('ascii')
PS=(ROOT/'build-tools/Sunrise.GroupSigning.ps1').read_text('utf-8')
CS=(ROOT/'source/build-tools/Sunrise.Signing/Sunrise.Signing.csproj').read_text('utf-8')
class SecurityToolRegression(unittest.TestCase):
 def test_no_new_unsigned_signing_executable_launched(self):
  for text in [ONEDIR,INSTALL]:
   self.assertNotIn('dotnet run --project "%signing_project%"',text.lower())
   self.assertNotIn('set "SIGNING_PROJECT=',text)
  self.assertIn('<OutputType>Library</OutputType>',CS)
  self.assertNotIn('Sunrise.Signing.exe',INSTALL)
 def test_no_v3_or_script_policy_bypass(self):
  for text in [ONEDIR,INSTALL,PS]:
   for banned in ['-ExecutionPolicy Bypass', 'Add-MpPreference','Set-MpPreference', 'Invoke-WebRequest', 'Start-BitsTransfer']:
    self.assertNotIn(banned,text)
  self.assertIn('-NoProfile -NonInteractive -File',ONEDIR)
  self.assertIn('-NoProfile -NonInteractive -File',INSTALL)
 def test_release_still_signs_and_checks_group_certificate(self):
  self.assertIn('CurrentUser\\My',PS)
  self.assertIn("CN\\s*=\\s*Sunrise Group",PS)
  self.assertIn('HasPrivateKey',PS)
  self.assertIn('1.3.6.1.5.5.7.3.3',PS)
  self.assertIn('KeySize -ge 3072',PS)
  self.assertIn('CreateFromSignedFile',PS)
  self.assertIn('SignerCertificate.Thumbprint',PS)
  self.assertIn('/fd SHA256 /sha1 "%CERT_THUMBPRINT%" /s My',ONEDIR)
  self.assertIn('/SSunriseGroup=',INSTALL)
  self.assertIn('SignedUninstaller=yes', (ROOT/'installer/SunriseFileManager.iss').read_text())
 def test_build_script_fail_closed_on_policy_and_certificate_errors(self):
  self.assertIn('if errorlevel 1 goto :fail_signing',ONEDIR)
  self.assertIn('if errorlevel 1 goto :fail_sign',INSTALL)
  self.assertIn('SUNRISE_ALLOW_CERT_CREATE%',ONEDIR)
  self.assertIn('disabled',ONEDIR)
  self.assertNotIn('New-SelfSignedCertificate',PS)
  self.assertNotIn('Set-ExecutionPolicy',PS)
 def test_no_business_document_reads_in_build(self):
  for content in [ONEDIR,INSTALL,PS]:
   self.assertNotIn('LogicTests.csproj',content)
   self.assertNotIn('UiSmokeTests',content)
 if __name__=='__main__': unittest.main()
