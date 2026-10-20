"""R05 source-only native certutil/SignTool signing contracts."""
import unittest
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
CERT=(ROOT/'build-tools/FindSunriseCertificate.cmd').read_text('ascii')
PAYLOAD=(ROOT/'BUILD_RELEASE_ONEDIR.cmd').read_text('ascii')
SETUP=(ROOT/'BUILD_RELEASE_INSTALLER.cmd').read_text('ascii')
PREFLIGHT=(ROOT/'CHECK_INSTALLER_BUILD_ENV.cmd').read_text('ascii')
class CaptureSigningR05(unittest.TestCase):
 def test_capture_certutil_and_sha1_method(self):
  self.assertIn('certutil.exe -user -store My',CERT)
  self.assertIn('FindSunriseCertificate.cmd', PAYLOAD)
  self.assertIn('CN=Sunrise Group',CERT)
  self.assertIn('CERT_MATCH_COUNT',CERT)
  self.assertIn('40 hexadecimal characters', CERT)
  self.assertIn('SUNRISE_CERT_SHA1',CERT)
 def test_sha1_label_does_not_poison_hash(self):
  self.assertIn('tokens=1,* delims=:',CERT)
  self.assertIn('call :consider_hash "%%B"',CERT)
  self.assertIn('if defined SUNRISE_CERT_SHA1 goto :explicit_thumbprint',CERT)
  self.assertIn('if not "%CERT_MATCH_COUNT%"=="1" goto :ambiguous_cert',CERT)
  self.assertIn('if "%CERT_MATCH_COUNT%"=="0" goto :missing_sha1',CERT)
  self.assertNotIn('tokens=* delims=0123456789abcdef',CERT)
 def test_both_signing_targets_and_uninstaller(self):
  self.assertEqual(PAYLOAD.count('"%SIGNTOOL%" sign /fd SHA256'),2)
  self.assertIn('/sha1 %CERT_THUMBPRINT%',SETUP)
  self.assertIn('SignTool=SunriseGroup',(ROOT/'installer/SunriseFileManager.iss').read_text())
  self.assertIn('SignedUninstaller=yes',(ROOT/'installer/SunriseFileManager.iss').read_text())
  self.assertIn('R15_verify_setup.log',SETUP)
 def test_absent_ps1_and_untrusted_not_misreported(self):
  self.assertFalse((ROOT/'build-tools/Sunrise.GroupSigning.ps1').exists())
  self.assertIn('WINTRUST=UNTRUSTED_SELF_SIGNED',SETUP)
  self.assertIn('WINTRUST=UNTRUSTED_SELF_SIGNED',PAYLOAD)
  self.assertIn('if errorlevel 1 goto :fail_sign',SETUP)
 def test_preflight_and_installer_no_exe_helper(self):
  self.assertIn('where.exe certutil.exe',PREFLIGHT)
  self.assertIn('FindSunriseCertificate.cmd',PREFLIGHT)
  for code in (CERT,PAYLOAD,SETUP,PREFLIGHT):
   self.assertNotIn('powershell.exe',code.lower())
   self.assertNotIn('Sunrise.Signing.exe',code)
   self.assertNotIn('-ExecutionPolicy Bypass',code)
 def test_cmd_bytes_crlf(self):
  for name in ('build-tools/FindSunriseCertificate.cmd','CHECK_INSTALLER_BUILD_ENV.cmd','BUILD_RELEASE_ONEDIR.cmd','BUILD_RELEASE_INSTALLER.cmd'):
   b=(ROOT/name).read_bytes()
   self.assertTrue(b.isascii())
   self.assertIn(b'\r\n',b)
   self.assertNotIn(b'\n',b.replace(b'\r\n',b''))
if __name__=='__main__': unittest.main()
