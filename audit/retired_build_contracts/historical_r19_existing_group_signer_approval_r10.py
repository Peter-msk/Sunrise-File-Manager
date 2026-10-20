"""R10 signer-approval gate: static source-contract tests, not Windows signing."""
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
A = (ROOT / 'APPROVE_EXISTING_GROUP_SIGNER.cmd').read_text('ascii')
E = (ROOT / 'build-tools/EnsureSunriseGroupSigning.cmd').read_text('ascii')
C = (ROOT / 'CHECK_SIGNING_CERT.cmd').read_text('ascii')
I = (ROOT / 'BUILD_RELEASE_INSTALLER.cmd').read_text('ascii')


class ExistingSignerApprovalR10(unittest.TestCase):
    def test_cannot_pin_without_explicit_full_sha1_and_confirmation(self):
        tokens = ['set /p "APPROVED_SHA1=', '!APPROVED_SHA1:~39,1!',
                  '!APPROVED_SHA1:~40,1!', '[^0123456789ABCDEFabcdef]',
                  'FindSunriseCertificate.cmd', '/I not "!SUNRISE_CERT_THUMBPRINT!"',
                  '1.3.6.1.5.5.7.3.3', 'set /p "CONFIRM_LAST8=',
                  'set /p "FINAL_CONFIRM=', '=="APPROVE"',
                  'group_signer.sha1']
        for token in tokens:
            with self.subTest(token=token):
                self.assertIn(token, A)
        self.assertLess(A.index('if not errorlevel 1 goto :invalid_or_cancelled'),
                        A.index('call "%ROOT%\\build-tools\\FindSunriseCertificate.cmd"'))
        self.assertLess(A.index('findstr /L /C:"1.3.6.1.5.5.7.3.3"'),
                        A.index('> "!PIN!.tmp" echo(!APPROVED_SHA1!'))
        self.assertLess(A.index('if /I not "!FINAL_CONFIRM!"=="APPROVE"'),
                        A.index('> "!PIN!.tmp" echo(!APPROVED_SHA1!'))

    def test_does_not_override_pinned_identity_or_previous_pending_certificate(self):
        self.assertIn('if exist "%PIN%" goto :already_pinned', A)
        self.assertIn('if exist "%LOG%\\R14_generated_group_public.cer" goto :pending_cert', A)
        self.assertIn('if exist "!PIN!" goto :already_pinned', A)
        self.assertIn('STATUS=PIN_ALREADY_EXISTS', A)
        self.assertIn('STATUS=PENDING_PUBLIC_CERT_MUST_RECOVER_FIRST', A)
        self.assertIn('STATUS=EXISTING_GROUP_CERT_APPROVED_AND_PINNED', A)
        self.assertIn('STATUS=REUSED_PINNED', E)
        self.assertIn('STATUS=UNPINNED_GROUP_CERT_MANUAL_REVIEW', E)
        self.assertIn('APPROVE_EXISTING_GROUP_SIGNER.cmd', E)
        self.assertIn('EnsureSunriseGroupSigning.cmd" --check-only', C)

    def test_does_not_change_signing_or_build_contract(self):
        self.assertIn('FindSignTool.cmd', A)
        self.assertIn('certutil.exe -v -user -store My', A)
        self.assertIn('CurrentUser', (ROOT / 'README.md').read_text('utf-8'))
        self.assertIn('SignTool', I)
        for forbidden in ('certreq.exe -new', 'certutil.exe -delstore', '-addstore Root',
                          'Sunrise.Signing.exe', 'Sunrise.GroupSigning.ps1',
                          'powershell.exe', 'Add-MpPreference', 'Export-PfxCertificate',
                          'certutil.exe -exportPFX'):
            self.assertNotIn(forbidden, A)

    def test_certificate_list_may_contain_multiple_valid_looking_identity_records(self):
        sample = '\n'.join([
            'Certificate Hash(sha1): ' + 'A' * 40,
            'Certificate Hash(sha1): ' + 'B' * 40,
            'Certificate Hash(sha1): ' + 'C' * 40,
        ])
        matches = re.findall('[ABC]{40}', sample)
        self.assertEqual(len(matches), 3)
        self.assertIn('set /p "APPROVED_SHA1=', A)
        self.assertNotIn('for /f', A)
        self.assertNotIn('last match', A.lower())

    def test_windows_batch_encoding_and_release_docs(self):
        for p in [ROOT / 'APPROVE_EXISTING_GROUP_SIGNER.cmd',
                  ROOT / 'build-tools/EnsureSunriseGroupSigning.cmd']:
            data = p.read_bytes()
            self.assertTrue(data.isascii())
            self.assertIn(b'\r\n', data)
            self.assertNotIn(b'\n', data.replace(b'\r\n', b''))
        self.assertTrue((ROOT / 'docs/R19_EXISTING_SIGNER_APPROVAL_R10.md').is_file())


if __name__ == '__main__':
    unittest.main()
