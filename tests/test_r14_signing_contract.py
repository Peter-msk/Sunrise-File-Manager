"""Build signing source-only regression; no private keys or business documents opened."""
from pathlib import Path
import unittest
ROOT=Path(__file__).resolve().parents[1]
class SigningContract(unittest.TestCase):
 def test_signing_sequence(self):
  s=(ROOT/'BUILD_RELEASE_ONEDIR.cmd').read_text('ascii')
  steps=['set "FAILED_STAGE=SIGNING_PREFLIGHT"','set "FAILED_STAGE=WPF_PUBLISH"','set "FAILED_STAGE=AUTHENTICODE_SIGN"','set "FAILED_STAGE=SIGNATURE_CHECK"','set "FAILED_STAGE=PACKAGE"','set "FAILED_STAGE=COMMIT"']
  self.assertEqual(sorted(s.index(t) for t in steps),[s.index(t) for t in steps]); self.assertIn('SignTool',s)
 def test_signing_targets_and_no_vendor_resign(self):
  s=(ROOT/'BUILD_RELEASE_ONEDIR.cmd').read_text('ascii')
  self.assertIn('/fd SHA256 /sha1 "%CERT_THUMBPRINT%" /n "Sunrise Group" /s My',s)
  self.assertEqual(s.count('"%SIGNTOOL%" sign /fd SHA256'),2)
  self.assertIn('Sunrise.FileManager.exe',s)
  self.assertIn('Sunrise.FileManager.dll',s)
  for d in ('hostfxr.dll','coreclr.dll','PresentationFramework.dll'):
   self.assertNotIn('sign /fd SHA256 /sha1 "%CERT_THUMBPRINT%" /n "Sunrise Group" /s My /tr "%TIMESTAMP_URL%" /td SHA256 /v "%STAGE%\\app\\'+d+'"',s)
 def test_certificate_store_and_no_key_export(self):
  p=(ROOT/'source/build-tools/Sunrise.Signing/Program.cs').read_text()
  self.assertIn('StoreLocation.CurrentUser',p)
  self.assertIn('CN=Sunrise Group',p)
  self.assertIn('CngExportPolicies.None',p)
  self.assertIn('1.3.6.1.5.5.7.3.3',p)
  self.assertNotIn('Export(X509ContentType.Pfx',p)
  self.assertIn('verify-embedded',p)
  self.assertIn('<OutputType>Library</OutputType>', (ROOT/'source/build-tools/Sunrise.Signing/Sunrise.Signing.csproj').read_text())
 def test_company_skips_documents(self):
  s=(ROOT/'BUILD_RELEASE_ONEDIR.cmd').read_text('ascii')
  self.assertNotIn('LogicTests.csproj',s)
  self.assertNotIn('R14_document',s)
  self.assertIn('COMPANY_SOURCE_ONLY.flag',s)
  self.assertIn('PublishSingleFile=false',s)
 def test_public_publisher_metadata(self):
  csproj=(ROOT/'source/Sunrise.FileManager/Sunrise.FileManager.csproj').read_text()
  self.assertIn('<Company>Sunrise Group</Company>',csproj)
  self.assertIn('<Product>Sunrise File Manager</Product>',csproj)
  self.assertIn('Copyright © 2026 Sunrise Group',csproj)
if __name__ == '__main__': unittest.main()
