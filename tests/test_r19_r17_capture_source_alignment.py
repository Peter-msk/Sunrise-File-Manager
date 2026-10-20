"""R17: comparison against actual Sunrise Capture v4.2.0 Drive source (static contract)."""
import json
import unittest
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]

class CaptureSourceReleaseAlignment(unittest.TestCase):
    def test_capture_style_release_outputs(self):
        g=(ROOT/'build-tools/Sunrise.FileManager.ReleaseMetadata/Program.cs').read_text(encoding='utf8')
        for x in ['release_info.json','sunrise_file_manager_product_manifest.json','UPLOAD_TO_GITHUB.txt',
                  'SIGNATURE_VERIFY_RESULT.txt','latest.json','notice.txt','release-metadata','SHA256.HashData(stream)',
                  'update_endpoint_configured = false','download_url = ""','R15_verify_setup.log','R14_signing_certificate.txt']:
            with self.subTest(token=x): self.assertIn(x,g)
    def test_order_and_required_artifact_gate(self):
        script=(ROOT/'BUILD_RELEASE_INSTALLER.cmd').read_text(encoding='ascii')
        self.assertLess(script.index('STAGE=INSTALLER_VERIFY'),script.index('STAGE=RELEASE_METADATA'))
        self.assertLess(script.index('STAGE=RELEASE_METADATA'),script.index('STAGE=RELEASE_ARTIFACT_VERIFY'))
        self.assertLess(script.index('STAGE=RELEASE_ARTIFACT_VERIFY'),script.index('echo STAGE=COMPLETE'))
        self.assertIn('fc /b "%ROOT%\\latest.json"',script)
        self.assertIn('fc /b "%ROOT%\\notice.txt"',script)
        self.assertIn('goto :fail_metadata',script)
        raw=(ROOT/'BUILD_RELEASE_INSTALLER.cmd').read_bytes()
        self.assertNotIn(b'\n',raw.replace(b'\r\n',b''))
    def test_control_and_product_separated(self):
        g=(ROOT/'build-tools/Sunrise.FileManager.ReleaseMetadata/Program.cs').read_text(encoding='utf8')
        self.assertNotIn('Sunrise_Control',g)
        self.assertIn('source = \"central control.txt\"',g)
        self.assertIn('Sunrise-File-Manager/main/latest.json',g)
        self.assertIn('Sunrise-File-Manager/main/notice.txt',g)
        self.assertNotIn('Sunrise_Control/main/products/sunrise_file_manager',g)
        payload=json.loads((ROOT/'latest.json').read_text('utf8'))
        self.assertFalse(payload['update_endpoint_configured'])
        self.assertEqual(payload['latest_version'],'1.0.0')

if __name__=='__main__': unittest.main()
