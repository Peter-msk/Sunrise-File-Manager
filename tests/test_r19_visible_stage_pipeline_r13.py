"""R13 build UI regression: no hidden child output, preserve native logs/security gates.
Windows native execution is explicitly not claimed by these source tests.
"""
import unittest
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
class VisiblePipelineR13(unittest.TestCase):
    def test_installer_child_build_not_silenced(self):
        t=(ROOT/'BUILD_RELEASE_INSTALLER.cmd').read_text('ascii')
        self.assertIn('call "%ROOT%\\BUILD_RELEASE_ONEDIR.cmd" --company --no-pause --installer-payload\n', t.replace('\r\n','\n'))
        self.assertNotIn(' --installer-payload > ', t)
        self.assertIn('R15_payload_build.log',t)
    def test_capture_stage_order_publish_then_cert_then_sign(self):
        t=(ROOT/'BUILD_RELEASE_ONEDIR.cmd').read_text('ascii')
        self.assertLess(t.index('"%DOTNET_EXE%" publish'), t.index('call "%ROOT%\\build-tools\\EnsureSunriseGroupSigning.cmd"'))
        self.assertLess(t.index('call "%ROOT%\\build-tools\\EnsureSunriseGroupSigning.cmd"'), t.index('"%SIGNTOOL%" sign /fd SHA256'))
    def test_dotnet_visible_and_file_logged(self):
        t=(ROOT/'BUILD_RELEASE_ONEDIR.cmd').read_text('ascii')
        self.assertIn('"-flp:logfile=%LOG_DIR%\\R14_wpf_publish.log;verbosity=normal"',t)
        self.assertNotIn('> "%LOG_DIR%\\R14_wpf_publish.log" 2>&1',t)
        self.assertIn('STAGE=WPF_PUBLISH',t)
    def test_progress_footprints(self):
        a=(ROOT/'BUILD_RELEASE_INSTALLER.cmd').read_text('ascii')
        b=(ROOT/'BUILD_RELEASE_ONEDIR.cmd').read_text('ascii')
        for step in ['PAYLOAD_BUILD','INSTALLER_COMPILE','INSTALLER_VERIFY','COMMIT']:
            self.assertIn('echo STAGE='+step,a)
        for step in ['SIGNING_PREFLIGHT','WPF_PUBLISH','AUTHENTICODE_SIGN','SIGNATURE_CHECK']:
            self.assertIn('echo STAGE='+step,b)
        for x in [a,b]:
            self.assertIn('R13_CURRENT_STAGE.txt',x)
            self.assertNotIn('set /p',x)
            self.assertNotIn('choice /C',x)
    def test_signer_not_weakened(self):
        a=(ROOT/'BUILD_RELEASE_INSTALLER.cmd').read_text('ascii')
        b=(ROOT/'BUILD_RELEASE_ONEDIR.cmd').read_text('ascii')
        c=(ROOT/'build-tools/EnsureSunriseGroupSigning.cmd').read_text('ascii')
        self.assertIn('"%SIGNTOOL%" sign /fd SHA256 /sha1',b)
        self.assertIn('/SSunriseGroup=$q%SIGNTOOL%$q sign',a)
        self.assertIn('STATUS=PINNED_CERT_MISSING_OR_INVALID',c)
        self.assertEqual(c.count('certreq.exe -new -user -q'),1)
        for disallowed in ('powershell.exe','Add-MpPreference','certutil.exe -delstore'):
            self.assertNotIn(disallowed, a+b+c)
    def test_no_extra_build_entrance(self):
        self.assertEqual({x.name for x in ROOT.glob('BUILD*.cmd')},{
            'BUILD_ON_WINDOWS_COMPANY.cmd','BUILD_DEV_EXE.cmd',
            'BUILD_RELEASE_INSTALLER.cmd','BUILD_RELEASE_ONEDIR.cmd'})
    def test_crlf_all_edited(self):
        for name in ['BUILD_ON_WINDOWS_COMPANY.cmd','CHECK_INSTALLER_BUILD_ENV.cmd',
                     'BUILD_RELEASE_INSTALLER.cmd','BUILD_RELEASE_ONEDIR.cmd']:
            raw=(ROOT/name).read_bytes()
            self.assertTrue(raw.isascii())
            self.assertIn(b'\r\n',raw)
            self.assertNotIn(b'\n',raw.replace(b'\r\n',b''))
if __name__=='__main__':unittest.main()
