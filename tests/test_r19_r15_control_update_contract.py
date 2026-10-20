"""R15 static contract checks. Does NOT claim Windows build/network/update execution."""
from pathlib import Path
import json
import re
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / 'source' / 'Sunrise.FileManager'

class SunriseControlReleaseContract(unittest.TestCase):
    def test_version_aligned(self):
        feed = json.loads((ROOT / 'latest.json').read_text('utf-8'))
        self.assertEqual(feed['version'], '1.0.0')
        self.assertEqual(feed['latest_version'], '1.0.0')
        self.assertEqual((ROOT/'installer/RELEASE_VERSION.txt').read_text('utf-8').strip(), feed['version'])
        self.assertEqual(feed['product_id'], 'SUNRISE-FILE-MANAGER-WIN-DESKTOP')
        self.assertEqual(feed['app_id'], 'sunrise_file_manager')

    def test_no_phantom_installer(self):
        feed = json.loads((ROOT / 'latest.json').read_text('utf-8'))
        self.assertFalse(feed['download_url'])
        self.assertFalse(feed['sha256'])
        self.assertEqual(feed['file_size_bytes'], 0)
        self.assertFalse(feed['update_endpoint_configured'])

    def test_product_owned_metadata_urls(self):
        source = (APP/'Services/SunriseReleaseService.cs').read_text('utf-8')
        self.assertIn('Sunrise-File-Manager/main/latest.json', source)
        self.assertIn('Sunrise-File-Manager/main/notice.txt', source)
        self.assertNotIn('Sunrise_Control/main/products/sunrise_file_manager/', source)

    def test_control_gate_and_fallback(self):
        s = (APP/'Services/SunriseControlService.cs').read_text('utf-8')
        for v in ['ControlId = "sunrise_file_manager"', 'BlocksEntry', 'RequiresUpdate', 'CachePath', 'cache_ttl_seconds', '"fallback"', '"maintenance"', '"block"']:
            self.assertIn(v, s)
        app = (APP/'App.xaml.cs').read_text('utf-8')
        self.assertIn('policy.BlocksEntry', app)
        self.assertIn('policy.RequiresUpdate', app)
        self.assertIn('main.ApplyControlPolicy(policy)', app)
        x = ET.parse(APP/'App.xaml').getroot()
        self.assertNotIn('StartupUri', x.attrib)

    def test_update_is_explicit_and_verified(self):
        s = (APP/'Services/SunriseReleaseService.cs').read_text('utf-8')
        for v in ['Sha256.Length != 64', 'IncrementalHash.CreateHash(HashAlgorithmName.SHA256)', 'total != release.FileSize', 'github.com', 'Too many download redirects']:
            self.assertIn(v, s)
        dialog = (APP/'Controls/SunriseControlWindow.cs').read_text('utf-8')
        self.assertIn('MessageBoxButton.YesNo', dialog)
        self.assertIn('SunriseReleaseService.DownloadAsync', dialog)
        self.assertIn('Process.Start', dialog)
        self.assertIn('VersionIsOlder(_release.Version, _policy.MinimumVersion)', dialog)
        self.assertIn('_policy.Update == "block"', dialog)

    def test_ui_menu_events(self):
        ET.parse(APP/'MainWindow.xaml')
        x=(APP/'MainWindow.xaml').read_text('utf-8')
        c=(APP/'MainWindow.xaml.cs').read_text('utf-8')
        for name in ['Notice_Click', 'UpdateCheck_Click']:
            self.assertIn('Click="'+name+'"', x)
            self.assertIn('void '+name+'(', c)

if __name__=='__main__':
    unittest.main()
