import unittest
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
APP=ROOT/'source'/'Sunrise.FileManager'

class R17DriveMergeContract(unittest.TestCase):
 def test_breadcrumb_horizontal_wheel_restored(self):
  x=(APP/'Controls'/'ExplorerPane.xaml').read_text(encoding='utf-8')
  y=(APP/'Controls'/'ExplorerPane.xaml.cs').read_text(encoding='utf-8')
  z=(APP/'Features'/'Explorer'/'ExplorerPane.NavigationAndWatcher.cs').read_text(encoding='utf-8')
  self.assertIn('x:Name="BreadcrumbScroll"',x)
  self.assertIn('PreviewMouseWheel="Breadcrumb_PreviewMouseWheel"',x)
  self.assertIn('void Breadcrumb_PreviewMouseWheel',y)
  self.assertIn('BreadcrumbScroll?.ScrollToRightEnd()',z)
 def test_compact_toolbar_filters_preserved(self):
  x=(APP/'Features'/'Explorer'/'ExplorerPane.ViewAndFiltering.cs').read_text(encoding='utf-8')
  y=(APP/'Controls'/'ExplorerPane.xaml').read_text(encoding='utf-8')
  self.assertIn('DisplayOptionsPanel.Visibility',x)
  self.assertIn('(_compactMode ? 165 : 230)',x)
  self.assertIn('x:Name="DisplayOptionsPanel"',y)
  self.assertIn('Header="파일 유형 필터"',y)
  self.assertIn('Header="표시 항목"',y)
  self.assertIn('Click="ToggleHiddenMenu_Click"',y)
 def test_official_installer_signing_entry_kept(self):
  b=(ROOT/'BUILD_ON_WINDOWS_COMPANY.cmd').read_text(encoding='ascii')
  installer=(ROOT/'BUILD_RELEASE_INSTALLER.cmd').read_text(encoding='ascii')
  iss=(ROOT/'installer'/'SunriseFileManager.iss').read_text(encoding='utf-8')
  self.assertIn('BUILD_RELEASE_INSTALLER.cmd',b)
  self.assertIn('BUILD_RELEASE_ONEDIR.cmd',installer)
  self.assertIn('AppMutex=Local\\SunriseFileManagerCEF',iss)
  self.assertIn('SignedUninstaller=yes',iss)

if __name__=='__main__': unittest.main()
