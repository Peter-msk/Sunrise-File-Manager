"""Release v1.0.0 metadata checks against GitHub release API read on 2026-10-21."""
from pathlib import Path
import json, unittest, re
R=Path(__file__).resolve().parents[1]
ASSET="Sunrise_File_Manager_1.0.0_Setup.exe"
HASH="eb866da915459cb1c61df22039957f6e07c9d863293a406a40ea5fcb47d62cdd"
SIZE=51251864
BASE="https://github.com/Peter-msk/Sunrise-File-Manager/releases"
class PublishedReleaseContract(unittest.TestCase):
    def test_published_feed_matches_release_asset_digest(self):
        f=json.loads((R/'latest.json').read_text('utf-8'))
        p=json.loads((R/'release/PUBLISHED_RELEASE.json').read_text('utf-8'))
        self.assertTrue(f['update_endpoint_configured'])
        self.assertEqual(f['latest_version'],'1.0.0')
        self.assertEqual(f['download_url'],f'{BASE}/download/v1.0.0/{ASSET}')
        self.assertEqual(f['release_url'],f'{BASE}/tag/v1.0.0')
        self.assertEqual(f['sha256'],HASH)
        self.assertEqual(f['file_size_bytes'],SIZE)
        self.assertEqual(p['sha256'],f['sha256'])
        self.assertEqual(p['file_size_bytes'],f['file_size_bytes'])
        self.assertEqual(p['download_url'],f['download_url'])
    def test_current_notice_announces_published_release(self):
        notice=(R/'notice.txt').read_text('utf-8')
        self.assertIn('정식 버전이 출시되었습니다',notice)
        self.assertNotIn('설치파일의 실기 빌드·서명·설치 검증은 별도',notice)
        self.assertEqual(notice, 'Sunrise File Manager 1.0.0\n\n'+(R/'release/NOTICE_BODY.txt').read_text('utf-8'))
    def test_runtime_enforces_explicit_enablement_and_release_path(self):
        code=(R/'source/Sunrise.FileManager/Services/SunriseReleaseService.cs').read_text('utf-8')
        self.assertIn('UpdateConfigured && IsApprovedDownload(this)',code)
        self.assertIn('"update_endpoint_configured"',code)
        self.assertIn('releases/download/v{release.Version}/{release.FileName}',code)
        self.assertIn('IncrementalHash.CreateHash(HashAlgorithmName.SHA256)',code)
    def test_future_build_cannot_overwrite_published_feed(self):
        g=(R/'build-tools/Sunrise.FileManager.ReleaseMetadata/Program.cs').read_text('utf-8')
        self.assertIn('publicationMatched',g)
        self.assertIn('PUBLISHED_RELEASE.json',g)
        self.assertIn('Get("sha256").Equals(hash, StringComparison.OrdinalIgnoreCase)',g)
        self.assertNotIn('[Path.Combine(root, "latest.json")]',g)
        self.assertNotIn('[Path.Combine(root, "notice.txt")]',g)
        build=(R/'BUILD_RELEASE_INSTALLER.cmd').read_text('ascii')
        self.assertIn('Published feed preserved',build)
if __name__=='__main__': unittest.main()
