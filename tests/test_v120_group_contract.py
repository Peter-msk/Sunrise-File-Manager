"""Sunrise Group v2.8 source-level contracts for File Manager 1.2.0.
These are not Windows runtime or installer lifecycle proofs.
"""
from pathlib import Path
import json
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / 'source/Sunrise.FileManager'

def source(rel):
    return (APP/rel).read_text(encoding='utf-8')

class GroupProduct120(unittest.TestCase):
    def test_single_version_across_wpf_and_installer(self):
        self.assertEqual((ROOT/'installer/RELEASE_VERSION.txt').read_text().strip(), '1.2.0')
        self.assertIn('<Version>1.2.0</Version>', source('Sunrise.FileManager.csproj'))
        self.assertIn('public const string Version = "1.2.0"', source('Services/SunriseControlService.cs'))
        self.assertIn('Sunrise File Manager 1.2.0', source('MainWindow.xaml'))
        self.assertIn('Sunrise File Manager 1.2.0', source('Controls/SunriseControlWindow.cs'))

    def test_control_policy_only_and_product_owns_metadata(self):
        control=source('Services/SunriseControlService.cs')
        release=source('Services/SunriseReleaseService.cs')
        self.assertIn('Sunrise_Control/main/control.txt', control)
        self.assertIn('ControlId = "sunrise_file_manager"', control)
        for leaf in ('latest.json', 'notice.txt'):
            self.assertIn('/Sunrise-File-Manager/main/'+leaf,release)
            self.assertNotIn('Sunrise_Control/main/'+leaf,release)
        self.assertIn('policy.BlocksEntry',source('App.xaml.cs'))
        self.assertIn('policy.RequiresUpdate',source('App.xaml.cs'))

    def test_install_only_after_application_exit(self):
        ui=source('Controls/SunriseControlWindow.cs')
        self.assertIn('WaitForExit(60000)',ui)
        self.assertLess(ui.index('WaitForExit(60000)'), ui.index('Start-Process -FilePath'))
        self.assertLess(ui.index('LaunchSetupAfterExit(path);'),ui.index('Application.Current.Shutdown();'))
        self.assertNotIn('Process.Start(new ProcessStartInfo(path)',ui)
        self.assertIn('CanExitForUpdate()',ui)
        self.assertIn('CanExitForUpdate()',source('MainWindow.xaml.cs'))
        self.assertIn('Inspector.ConfirmLeavingMemo()',source('MainWindow.xaml.cs'))

    def test_local_helper_cannot_download_or_be_server_controlled(self):
        ui=source('Controls/SunriseControlWindow.cs')
        helper=ui.split('private static void LaunchSetupAfterExit',1)[1].split('private async Task DownloadAsync',1)[0]
        self.assertIn('Environment.ProcessId',helper)
        self.assertIn('Encoding.Unicode.GetBytes(command)',helper)
        self.assertIn('Environment.SpecialFolder.System',helper)
        self.assertIn('ProcessStartInfo(host)',helper)
        for dangerous in ('Invoke-WebRequest','DownloadString','-ExecutionPolicy Bypass','http://','https://','TaskScheduler'):
            self.assertNotIn(dangerous,helper)

    def test_central_policy_rechecked_before_download_and_handoff(self):
        ui=source('Controls/SunriseControlWindow.cs')
        code=ui.split('private async Task DownloadAsync',1)[1]
        self.assertGreaterEqual(code.count('await SunriseControlService.CheckAsync()'),2)
        self.assertIn('currentPolicy.BlocksEntry',code)
        self.assertIn('currentPolicy.Update == "block"',code)
        self.assertLess(code.index('currentPolicy = await SunriseControlService.CheckAsync();'),code.index('LaunchSetupAfterExit(path);'))

    def test_verified_download_preserved_and_exclusive_handle_closed(self):
        code=source('Services/SunriseReleaseService.cs')
        self.assertIn('IsApprovedDownload(this)',code)
        self.assertIn('digest.AppendData',code)
        self.assertIn('SHA256 mismatch',code)
        self.assertLess(code.index('await file.FlushAsync()'),code.index('File.Move(temp, target, true)'))
        self.assertIn('AllowAutoRedirect = false',code)

    def test_published_110_is_not_overwritten_by_source_120(self):
        # Local historical metadata is not the operating GitHub feed; do not fabricate a 1.2.0 setup hash.
        feed=json.loads((ROOT/'latest.json').read_text(encoding='utf-8'))
        self.assertLess(tuple(map(int,feed['latest_version'].split('.'))),(1,2,0))
        self.assertFalse((ROOT/'release/PUBLISHED_RELEASE.json').read_text().find('"version": "1.2.0"')!=-1)
        self.assertTrue((ROOT/'release/NOTICE_BODY_1.2.0.txt').is_file())

    def test_build_sign_and_inno_stable(self):
        iss=(ROOT/'installer/SunriseFileManager.iss').read_text()
        self.assertIn('AppMutex=Local\\SunriseFileManagerCEF',iss)
        self.assertIn('Name: "korean"',iss)
        self.assertIn('SignedUninstaller=yes',iss)
        self.assertIn('PrivilegesRequired=lowest',iss)
        self.assertIn('CloseApplications=no',iss)

if __name__=='__main__': unittest.main()
