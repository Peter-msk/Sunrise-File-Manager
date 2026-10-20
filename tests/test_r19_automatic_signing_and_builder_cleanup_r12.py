"""R12 targeted contract tests (source-only; Windows dynamic verification remains pending)."""
from pathlib import Path
import unittest
R=Path(__file__).resolve().parents[1]
B=(R/'BUILD_ON_WINDOWS_COMPANY.cmd').read_text('ascii')
P=(R/'build-tools/EnsureSunriseGroupSigning.cmd').read_text('ascii')
class AutomaticSigningR12(unittest.TestCase):
    def test_no_interactive_signing_choices_or_manual_fingerprint(self):
        for forbidden in ('choice /C','set /p','APPROVE_EXISTING_GROUP_SIGNER.cmd','CHECK_SIGNING_CERT.cmd','\npause\n'):
            self.assertNotIn(forbidden,B)
        self.assertIn('BUILD_RELEASE_INSTALLER.cmd',B)
    def test_stable_profile_pending_and_pin_reuse(self):
        self.assertIn('group_signer.sha1',P)
        self.assertIn('pending_group_public.cer',P)
        self.assertLess(P.index('if exist "%CERT_PIN%" goto :pinned_certificate'),P.index('certreq.exe -new -user -q'))
        self.assertIn('STATUS=REUSED_PINNED',P)
        self.assertIn('STATUS=PINNED_CERT_MISSING_OR_INVALID',P)
        self.assertIn('STATUS=CONFLICTING_PENDING_PUBLIC_CERTIFICATES',P)
        self.assertIn('STATUS=EXISTING_RELEASE_WITHOUT_PIN',P)
        self.assertIn('certutil.exe -hashfile "%HASH_INPUT%" SHA1',P)
        self.assertEqual(P.count('certreq.exe -new -user -q'),1)
    def test_no_unrelated_same_cn_autoselection(self):
        self.assertNotIn('R14_unpinned_group_probe.log',P)
        self.assertNotIn('SUNRISE_CERT_SHA1=%%',P)
        self.assertNotIn('APPROVE_EXISTING_GROUP_SIGNER.cmd',P)
    def test_builder_surface_is_trimmed(self):
        remain={'BUILD_ON_WINDOWS_COMPANY.cmd','BUILD_DEV_EXE.cmd','BUILD_RELEASE_INSTALLER.cmd','BUILD_RELEASE_ONEDIR.cmd'}
        self.assertEqual({x.name for x in R.glob('BUILD*.cmd')},remain)
        for name in ['BUILD_ON_WINDOWS.cmd','BUILD_RELEASE_ONEFILE.cmd','CHECK_SIGNING_CERT.cmd',
                     'APPROVE_EXISTING_GROUP_SIGNER.cmd','CAPTURE_UI_WINDOWS.cmd','CHECK_SDK.cmd']:
            self.assertFalse((R/name).exists(),name)
        for name in ['RUN_FILE_MANAGER.cmd','INSTALL_OLLAMA.cmd','CHECK_INSTALLER_BUILD_ENV.cmd']:
            self.assertTrue((R/name).exists(),name)
    def test_cmd_ascii_crlf(self):
        for name in ['BUILD_ON_WINDOWS_COMPANY.cmd','build-tools/EnsureSunriseGroupSigning.cmd']:
            data=(R/name).read_bytes()
            self.assertTrue(data.isascii(),name)
            self.assertIn(b'\r\n',data)
            self.assertNotIn(b'\n',data.replace(b'\r\n',b''))
    def test_company_security_boundary(self):
        self.assertIn('COMPANY_SOURCE_ONLY.flag',(R/'BUILD_RELEASE_INSTALLER.cmd').read_text('ascii'))
        for forbidden in ('powershell.exe','Add-MpPreference','certutil.exe -delstore','certutil.exe -addstore Root'):
            self.assertNotIn(forbidden,P)
if __name__=='__main__':unittest.main()
