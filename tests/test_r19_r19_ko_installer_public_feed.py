"""R19: Korean-only installer UI and customer-facing public metadata."""
import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
FORBIDDEN = ("개발", "소스", "SDK", "빌드", "검증", "내부", "정적", "GitHub Actions", "SHA-256", "Sunrise Control", "R18", "R19", "PENDING", "WPF")

class UserFacingReleaseTest(unittest.TestCase):
    def test_korean_inno_ui(self):
        iss=(ROOT/'installer/SunriseFileManager.iss').read_text('utf-8')
        self.assertIn('Name: "korean"; MessagesFile: "compiler:Languages\\Korean.isl"',iss)
        self.assertNotIn('Name: "english";',iss)
        self.assertIn('SignedUninstaller=yes',iss)
        self.assertIn('PrivilegesRequired=lowest',iss)
        self.assertIn('DefaultDirName={localappdata}',iss)
    def test_public_notice_has_customer_content_only(self):
        body=(ROOT/'release/NOTICE_BODY.txt').read_text('utf-8')
        notice=(ROOT/'notice.txt').read_text('utf-8')
        self.assertEqual(notice,'Sunrise File Manager 1.0.0\n\n'+body)
        self.assertIn('정식 버전이 출시되었습니다',notice)
        self.assertIn('공식 설치파일',notice)
        for word in FORBIDDEN:
            with self.subTest(word=word):self.assertNotIn(word,notice)
    def test_latest_is_only_update_metadata(self):
        d=json.loads((ROOT/'latest.json').read_text('utf-8'))
        self.assertEqual(d['latest_version'],'1.0.0')
        self.assertTrue(d['update_endpoint_configured'])
        self.assertEqual(d['file_size_bytes'],51251864)
        self.assertEqual(d['sha256'],'eb866da915459cb1c61df22039957f6e07c9d863293a406a40ea5fcb47d62cdd')
        self.assertIn('/releases/download/v1.0.0/',d['download_url'])
        self.assertIn('/releases/tag/v1.0.0',d['release_url'])
        for word in FORBIDDEN:
            with self.subTest(word=word):self.assertNotIn(word,d['release_notes'])
    def test_future_build_release_notes_are_customer_facing(self):
        gen=(ROOT/'build-tools/Sunrise.FileManager.ReleaseMetadata/Program.cs').read_text('utf-8')
        self.assertIn('release_notes = releaseNotes + "."',gen)
        self.assertNotIn('게시된 설치파일과 해시·크기 일치.',gen)
        self.assertNotIn('현재 로컬 빌드는 공개 설치파일과 일치하지 않아',gen)
        self.assertIn('NOTICE_BODY_{version}.txt', gen)
    def test_no_unsafe_distribution_changes(self):
        publish=(ROOT/'BUILD_RELEASE_ONEDIR.cmd').read_text('ascii')
        self.assertIn('--self-contained true',publish)
        self.assertIn('-p:PublishTrimmed=false',publish)
        self.assertIn('PdfPig', (ROOT/'source/Sunrise.FileManager/Sunrise.FileManager.csproj').read_text('utf-8'))

if __name__=='__main__':unittest.main()
