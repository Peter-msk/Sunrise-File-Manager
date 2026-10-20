"""R09 regression: fail closed and reuse prior R07 certificate after SHA1 parsing failure.
These tests never access the Windows certificate store or real user documents.
"""
import hashlib
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
B = (ROOT / 'build-tools/EnsureSunriseGroupSigning.cmd').read_text('ascii')
P = (ROOT / 'BUILD_RELEASE_ONEDIR.cmd').read_text('ascii')


class CertificateFingerprintRecoveryR09(unittest.TestCase):
    def test_one_hash_from_hashfile_not_six_dump_sha1_mentions(self):
        # This reproduces the failure class: -dump can contain several SHA1-like values.
        first = 'A1' * 20
        other = ['B2' * 20, 'C3' * 20, 'D4' * 20, 'E5' * 20, 'F6' * 20]
        fake_dump = ''.join(f'SHA1 extension: {hash_}\n' for hash_ in [first, *other])
        self.assertEqual(len(re.findall(r'SHA1 extension: ([A-F0-9]{40})', fake_dump)), 6)
        # R09 instead takes the hash of the single DER-encoded public certificate.
        public_der_bytes = b'public DER input fixture (not a real certificate)'
        digest = hashlib.sha1(public_der_bytes).hexdigest().upper()
        self.assertEqual(len(digest), 40)
        self.assertIn('certutil.exe -hashfile "%HASH_INPUT%" SHA1', B)
        self.assertIn('for /f "usebackq delims=" %%H', B)
        self.assertIn('do call :consider_sha1 "%%H"', B)
        self.assertNotIn('findstr /I /C:"sha1" "%CERT_ROOT%\\R14_certreq_dump.log"', B)
        self.assertIn('if not "%NEW_HASH_COUNT%"=="1" goto :hash_not_found', B)

    def test_base64_and_der_are_supported_without_pfx_or_ps1(self):
        self.assertIn('certutil.exe -decode "%CERT_OUT%" "%CERT_DER%"', B)
        self.assertIn('set "HASH_INPUT=%CERT_OUT%"', B)
        self.assertIn('set "HASH_INPUT=%CERT_DER%"', B)
        self.assertIn('if errorlevel 1 goto :cert_dump_failed', B)
        self.assertIn('CN=Sunrise Group', B)
        self.assertIn('1.3.6.1.5.5.7.3.3', B)

    def test_r07_unpinned_public_cert_is_recovered_before_any_creation(self):
        self.assertIn('R14_generated_group_public.cer', B)
        self.assertIn('STATUS=RECOVERING_PENDING_CERTIFICATE', B)
        self.assertIn('goto :inspect_public_certificate', B)
        self.assertLess(B.index('if exist "%CERT_OUT%" ('), B.index('certreq.exe -new -user -q'))
        self.assertLess(B.index('goto :inspect_public_certificate'), B.index('certreq.exe -new -user -q'))
        self.assertIn('if exist "%CERT_OUT%" (', B)
        self.assertIn('STATUS=READY_TO_CREATE_DURING_BUILD', B)
        self.assertIn('STATUS=PENDING_CERT_IDENTITY_OR_EKU_MISMATCH', B)
        self.assertIn('STATUS=CREATED_CERT_STORE_VERIFY_FAILED', B)
        self.assertIn('STATUS=EXISTING_RELEASE_WITHOUT_PIN', B)
        self.assertIn('pending_group_public.cer', B)

    def test_pin_written_only_after_cert_store_verification(self):
        self.assertLess(B.index('certutil.exe -hashfile'), B.index('FindSunriseCertificate.cmd" >'))
        self.assertLess(B.index('FindSunriseCertificate.cmd" >'), B.index('> "%CERT_PIN%.tmp" echo %NEW_HASH%'))
        self.assertNotIn('if exist "%CERT_OUT%" del /q /f "%CERT_OUT%"', B)
        self.assertIn('STATUS=REUSED_PINNED', B)
        self.assertIn('STATUS=RECOVERED_AND_PINNED', B)
        self.assertIn('STATUS=PINNED_CERT_MISSING_OR_INVALID', B)
        self.assertEqual(B.count('certreq.exe -new -user -q'), 1)

    def test_read_only_probe_and_company_signature_remain_enforced(self):
        self.assertIn('STATUS=READY_TO_RECOVER_PENDING_CERTIFICATE', B)
        self.assertIn('STATUS=READY_TO_CREATE_DURING_BUILD', B)
        self.assertIn('if "%CERT_CHECK_ONLY%"=="1"', B)
        self.assertIn('EnsureSunriseGroupSigning.cmd', P)
        self.assertIn('"%SIGNTOOL%" sign /fd SHA256', P)
        for disallowed in ('Sunrise.Signing.exe', 'Sunrise.GroupSigning.ps1',
                           'powershell.exe', 'certutil.exe -delstore',
                           'certutil.exe -addstore Root', 'Add-MpPreference'):
            self.assertNotIn(disallowed, B)

    def test_cmd_format_preserved(self):
        data = (ROOT / 'build-tools/EnsureSunriseGroupSigning.cmd').read_bytes()
        self.assertTrue(data.isascii())
        self.assertIn(b'\r\n', data)
        self.assertNotIn(b'\n', data.replace(b'\r\n', b''))

if __name__ == '__main__':
    unittest.main()
