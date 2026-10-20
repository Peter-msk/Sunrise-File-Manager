"""R07 source-only contracts; never touch Windows cert stores or company documents."""
import unittest
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
ENSURE=(ROOT/'build-tools/EnsureSunriseGroupSigning.cmd').read_text('ascii')
INF=(ROOT/'build-tools/Sunrise.GroupCodeSigning.inf').read_text('ascii')
BUILD=(ROOT/'BUILD_RELEASE_ONEDIR.cmd').read_text('ascii')
INSTALL=(ROOT/'BUILD_RELEASE_INSTALLER.cmd').read_text('ascii')
CHECK=(ROOT/'CHECK_SIGNING_CERT.cmd').read_text('ascii')
class InBuildSelfSigningR07(unittest.TestCase):
    def test_native_self_signed_certificate_generation_with_code_signing_eku(self):
        for token in ('RequestType = Cert','Subject = "CN=Sunrise Group"','KeyAlgorithm = RSA',
                      'KeyLength = 3072','HashAlgorithm = sha256','KeyUsage = 0x80',
                      '1.3.6.1.5.5.7.3.3','Exportable = FALSE','MachineKeySet = FALSE',
                      'ProviderName = "Microsoft Software Key Storage Provider"'):
            self.assertIn(token,INF)
        self.assertIn('certreq.exe -new -user -q',ENSURE)
        self.assertIn('certutil.exe -dump',ENSURE)
        self.assertIn('FindSunriseCertificate.cmd',ENSURE)
    def test_first_build_provisions_then_signs_both_payload_and_installer(self):
        self.assertIn('EnsureSunriseGroupSigning.cmd',BUILD)
        self.assertLess(BUILD.index('call "%ROOT%\\build-tools\\EnsureSunriseGroupSigning.cmd"'),BUILD.index('"%DOTNET_EXE%" publish'))
        self.assertEqual(BUILD.count('"%SIGNTOOL%" sign /fd SHA256'),2)
        self.assertIn('/sha1 "%CERT_THUMBPRINT%"',BUILD)
        self.assertIn('/SSunriseGroup=',INSTALL)
        self.assertIn('/sha1 %CERT_THUMBPRINT%',INSTALL)
        self.assertIn('SignedUninstaller=yes',(ROOT/'installer/SunriseFileManager.iss').read_text())
    def test_pinned_identity_survives_updates_without_silent_rotation(self):
        self.assertIn('%LOCALAPPDATA%\\SunriseGroup\\FileManager\\Signing',ENSURE)
        self.assertIn('group_signer.sha1',ENSURE)
        self.assertIn('if exist "%CERT_PIN%" goto :pinned_certificate',ENSURE)
        self.assertIn('if errorlevel 1 goto :pinned_cert_unavailable',ENSURE)
        self.assertIn('STATUS=PINNED_CERT_MISSING_OR_INVALID',ENSURE)
        self.assertIn('STATUS=REUSED_PINNED',ENSURE)
        self.assertIn('STATUS=CREATED_AND_PINNED',ENSURE)
        self.assertIn('move /y "%CERT_PIN%.tmp" "%CERT_PIN%"',ENSURE)
    def test_readonly_probe_does_not_create(self):
        self.assertIn('EnsureSunriseGroupSigning.cmd" --check-only',CHECK)
        self.assertIn('if "%CERT_CHECK_ONLY%"=="1"',ENSURE)
        self.assertLess(ENSURE.index('if "%CERT_CHECK_ONLY%"=="1"'),ENSURE.index('certreq.exe -new'))
    def test_no_untrusted_signer_replacement_or_security_bypass(self):
        for text in (ENSURE,BUILD,INSTALL,CHECK):
            for banned in ('powershell.exe','Sunrise.Signing.exe','Sunrise.GroupSigning.ps1',
                           'Set-MpPreference','Add-MpPreference','-ExecutionPolicy Bypass',
                           'certutil.exe -addstore Root','Import-PfxCertificate'):
                self.assertNotIn(banned,text)
        self.assertIn('STATUS=EXTERNAL_SHA1_REJECTED',ENSURE)
        self.assertIn('STATUS=INVALID_PROFILE',ENSURE)
        self.assertNotIn('Exportable = TRUE',INF)
    def test_company_policy_and_package_private_key_exclusion(self):
        self.assertIn('COMPANY_SOURCE_ONLY.flag',BUILD)
        self.assertNotIn('LogicTests.csproj',BUILD)
        self.assertNotIn('UiSmokeTests',BUILD)
        self.assertNotIn('Sunrise.GroupCodeSigning.inf',(ROOT/'installer/SunriseFileManager.iss').read_text())
        for file in ('build-tools/EnsureSunriseGroupSigning.cmd','build-tools/Sunrise.GroupCodeSigning.inf',
                     'CHECK_SIGNING_CERT.cmd','BUILD_RELEASE_ONEDIR.cmd'):
            b=(ROOT/file).read_bytes()
            self.assertIn(b'\r\n',b)
            self.assertNotIn(b'\n',b.replace(b'\r\n',b''))
            self.assertTrue(b.isascii())
if __name__=='__main__':unittest.main()
