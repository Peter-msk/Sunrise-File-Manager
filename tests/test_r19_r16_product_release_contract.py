"""R16 static assertions: product-owned metadata and build integration.
Windows .NET build / private GitHub HTTPS availability are not exercised.
"""
import json
import re
import unittest
from pathlib import Path
from xml.etree import ElementTree as ET

ROOT=Path(__file__).resolve().parents[1]
APP=ROOT/'source/Sunrise.FileManager'

class ProductReleaseContract(unittest.TestCase):
    def test_metadata_owned_by_product(self):
        src=(APP/'Services/SunriseReleaseService.cs').read_text('utf-8')
        self.assertIn('Sunrise-File-Manager/main/latest.json',src)
        self.assertIn('Sunrise-File-Manager/main/notice.txt',src)
        self.assertNotIn('Sunrise_Control/main/products/sunrise_file_manager',src)
        self.assertIn('private', src)
        central=(APP/'Services/SunriseControlService.cs').read_text('utf-8')
        self.assertIn('Sunrise_Control/main/control.txt',central)
        self.assertNotIn('latest.json',central)
        self.assertNotIn('notice.txt',central)
    def test_generated_metadata_is_after_signed_setup(self):
        script=(ROOT/'BUILD_RELEASE_INSTALLER.cmd').read_text('ascii')
        self.assertLess(script.index('"%SIGNTOOL%" verify /pa /v "%UNSIGNED_SETUP%"'),script.index('STAGE=RELEASE_METADATA'))
        self.assertLess(script.index('certutil -hashfile "%FINAL_SETUP%" SHA256'),script.index('STAGE=RELEASE_METADATA'))
        self.assertLess(script.index('STAGE=RELEASE_METADATA'),script.index('echo STAGE=COMPLETE'))
        self.assertIn('R16_release_metadata.log',script)
        self.assertIn('Sunrise.FileManager.ReleaseMetadata.csproj',script)
        self.assertIn(':fail_metadata',script)
        self.assertNotIn('powershell.exe',script.lower())
        self.assertNotIn('set /p',script.lower())
        raw=(ROOT/'BUILD_RELEASE_INSTALLER.cmd').read_bytes()
        self.assertNotIn(b'\n',raw.replace(b'\r\n',b''))
    def test_generator_hashes_real_signed_setup(self):
        code=(ROOT/'build-tools/Sunrise.FileManager.ReleaseMetadata/Program.cs').read_text('utf-8')
        for token in ['SHA256.HashData(stream)', 'info.Length', 'RELEASE_VERSION.txt', 'NOTICE_BODY.txt', 'latest.json', 'notice.txt', 'release-metadata', 'update_endpoint_configured = false', 'download_url = ""']:
            self.assertIn(token,code)
        self.assertNotIn('Process.Start',code)
        self.assertNotIn('Sunrise_Control',code)
        self.assertFalse(re.search(r'(?i)\b(private_key|personal_access_token)\s*=',code))
        self.assertIn('<TargetFramework>net10.0</TargetFramework>',(ROOT/'build-tools/Sunrise.FileManager.ReleaseMetadata/Sunrise.FileManager.ReleaseMetadata.csproj').read_text('utf-8'))
    def test_source_release_is_publishing_gated(self):
        feed=json.loads((ROOT/'latest.json').read_text('utf-8'))
        self.assertEqual(feed['latest_version'],(ROOT/'installer/RELEASE_VERSION.txt').read_text('utf-8').strip())
        self.assertFalse(feed['download_url'])
        self.assertFalse(feed['update_endpoint_configured'])
        self.assertIn('Sunrise-File-Manager/main/notice.txt',feed['notice_url'])
        self.assertNotIn('Sunrise_Control',json.dumps(feed))
        self.assertGreater((ROOT/'release/NOTICE_BODY.txt').stat().st_size, 80)
    def test_runtime_requires_approval_and_verifies_artifact(self):
        src=(APP/'Services/SunriseReleaseService.cs').read_text('utf-8')
        self.assertIn('IncrementalHash.CreateHash(HashAlgorithmName.SHA256)',src)
        self.assertIn('total != release.FileSize',src)
        self.assertIn('Sunrise-File-Manager/releases/download/',src)
        dialog=(APP/'Controls/SunriseControlWindow.cs').read_text('utf-8')
        self.assertIn('MessageBoxButton.YesNo',dialog)
        self.assertIn('SunriseReleaseService.DownloadAsync',dialog)
        ET.parse(APP/'App.xaml')

if __name__=='__main__':unittest.main()
