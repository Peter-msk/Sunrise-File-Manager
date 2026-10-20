"""R11 source contracts only; Windows CMD, signing and installation are not exercised."""
import unittest
from pathlib import Path

R = Path(__file__).resolve().parents[1]
B = (R / 'BUILD_ON_WINDOWS_COMPANY.cmd').read_text('ascii')
I = (R / 'BUILD_RELEASE_INSTALLER.cmd').read_text('ascii')
A = (R / 'APPROVE_EXISTING_GROUP_SIGNER.cmd').read_text('ascii')

class OneClickSigningR11(unittest.TestCase):
    def test_env_certificate_installer_in_order(self):
        self.assertLess(B.index('CHECK_INSTALLER_BUILD_ENV.cmd'), B.index('CHECK_SIGNING_CERT.cmd'))
        self.assertLess(B.index('CHECK_SIGNING_CERT.cmd'), B.index('BUILD_RELEASE_INSTALLER.cmd'))
        self.assertIn('BUILD_RELEASE_INSTALLER.cmd" --company --no-pause', B)

    def test_only_exact_ambiguous_cert_state_offers_approval(self):
        self.assertIn('findstr /X /C:"STATUS=UNPINNED_GROUP_CERT_MANUAL_REVIEW"', B)
        self.assertLess(B.index('if errorlevel 1 goto :cert_failed'), B.index(':run_approval'))
        self.assertIn('APPROVE_EXISTING_GROUP_SIGNER.cmd" --no-pause', B)
        self.assertIn('if "%NONINTERACTIVE%"=="1" goto :manual_required', B)
        self.assertIn('if errorlevel 2 goto :manual_required', B)
        self.assertIn('if errorlevel 1 goto :cert_failed', B)

    def test_recheck_after_approval_and_reuse_no_prompt(self):
        self.assertIn('if not errorlevel 1 goto :run_installer', B)
        self.assertIn('call "%ROOT%\\CHECK_SIGNING_CERT.cmd" --no-pause', B)
        self.assertEqual(B.count('call "%ROOT%\\CHECK_SIGNING_CERT.cmd" --no-pause'), 2)
        self.assertLess(B.index('call "%ROOT%\\APPROVE_EXISTING_GROUP_SIGNER.cmd"'),
                        B.rindex('call "%ROOT%\\CHECK_SIGNING_CERT.cmd"'))
        self.assertIn('set "BUILD_RC=%ERRORLEVEL%"', B)
        self.assertIn('exit /b %BUILD_RC%', B)

    def test_no_unsafe_signing_or_changed_release_route(self):
        self.assertIn('SignTool', I)
        for bad in ('certutil.exe -delstore', 'Sunrise.Signing.exe',
                    'Sunrise.GroupSigning.ps1', 'powershell.exe',
                    'certreq.exe -new', 'Add-MpPreference', 'setx '):
            self.assertNotIn(bad, B)
        self.assertIn('set /p "APPROVED_SHA1=', A)
        self.assertIn('"APPROVE"', A)

    def test_cmd_format(self):
        for path in [R/'BUILD_ON_WINDOWS_COMPANY.cmd']:
            raw = path.read_bytes()
            self.assertTrue(raw.isascii())
            self.assertIn(b'\r\n', raw)
            self.assertNotIn(b'\n',raw.replace(b'\r\n',b''))

if __name__ == '__main__': unittest.main()
