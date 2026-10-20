"""Legacy R06 certificate resolver contracts (R07 build calls a project provisioning wrapper)."""
from pathlib import Path
import unittest
ROOT=Path(__file__).resolve().parents[1]
C=(ROOT/'build-tools/FindSunriseCertificate.cmd').read_text('ascii')
P=(ROOT/'BUILD_RELEASE_ONEDIR.cmd').read_text('ascii')
I=(ROOT/'BUILD_RELEASE_INSTALLER.cmd').read_text('ascii')
D=(ROOT/'CHECK_SIGNING_CERT.cmd').read_text('ascii')
class R06SigningPreflight(unittest.TestCase):
    def test_default_remains_group_and_capture_requires_opt_in(self):
        self.assertIn('if not defined SUNRISE_CERT_PROFILE set "SUNRISE_CERT_PROFILE=GROUP"',C)
        self.assertIn('if /I "%SUNRISE_CERT_PROFILE%"=="CAPTURE"',C)
        self.assertIn('Sunrise Capture Self-Signed Code Signing',C)
        self.assertIn('A different publisher requires explicit SUNRISE_CERT_PROFILE=CAPTURE approval.',C)
        self.assertIn('CERT_NOT_FOUND',C)
    def test_cert_selection_fails_closed(self):
        for flag in ['INVALID_CERT_PROFILE','INVALID_EXPLICIT_SHA1','CERT_NOT_FOUND',
                     'SHA1_NOT_PARSED','MULTIPLE_CERTIFICATES','SUBJECT_MISMATCH']:
            self.assertIn('STATUS='+flag,C)
        self.assertIn('for /f "usebackq tokens=1,* delims=:" %%A',C)
        self.assertIn('"%%B"',C)
        self.assertIn('CERT_MATCH_COUNT',C)
        self.assertIn('SUNRISE_CERT_SHA1',C)
        self.assertIn('if errorlevel 1 goto :no_cert',C)
    def test_pinned_identity_used_by_all_artifacts(self):
        self.assertIn('/sha1 "%CERT_THUMBPRINT%" /n "%SUNRISE_CERT_SUBJECT_CN%"',P)
        self.assertEqual(P.count('"%SIGNTOOL%" sign /fd SHA256'),2)
        self.assertIn('for /f "usebackq tokens=1,* delims==" %%A',I)
        self.assertIn('if not defined CERT_SUBJECT goto :fail_payload',I)
        self.assertIn('echo SIGNER=%CERT_SUBJECT%',I)
        self.assertIn('SignedUninstaller=yes',(ROOT/'installer/SunriseFileManager.iss').read_text())
        self.assertIn('/sha1 %CERT_THUMBPRINT%',I)
    def test_no_unsigned_skip_or_cert_provision(self):
        for s in (C,P,I,D):
            for forbidden in ['-ExecutionPolicy Bypass','Add-MpPreference','Set-MpPreference',
                              'Invoke-WebRequest','certreq.exe','Sunrise.Signing.exe',
                              'Sunrise.GroupSigning.ps1']:
                self.assertNotIn(forbidden,s)
        self.assertFalse((ROOT/'build-tools/Sunrise.GroupSigning.ps1').exists())
        self.assertIn('CERT_CREATION_DISABLED',C)
        self.assertIn('where.exe certutil.exe',C)
        self.assertIn('SIGNING_PREFLIGHT',P)
    def test_separate_readonly_preflight_and_diagnostic_propagation(self):
        self.assertIn('CHECK_SIGNING_CERT.cmd',(ROOT/'CHECK_INSTALLER_BUILD_ENV.cmd').read_text('ascii'))
        self.assertIn('call "%CD%\\build-tools\\EnsureSunriseGroupSigning.cmd" --check-only',D)
        self.assertIn('R14_signing_preflight.log',D)
        self.assertIn('R14_signing_preflight.log',P)
        self.assertIn('R14_signing_preflight.log',I)
        for path in ['CHECK_SIGNING_CERT.cmd','build-tools/FindSunriseCertificate.cmd']:
            b=(ROOT/path).read_bytes()
            self.assertIn(b'\r\n',b)
            self.assertNotIn(b'\n',b.replace(b'\r\n',b''))
            self.assertTrue(b.isascii())
if __name__=='__main__': unittest.main()
