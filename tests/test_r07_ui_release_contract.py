"""R07 source checks. Never reads or opens user documents or fixtures."""
import re
import unittest
from pathlib import Path
from xml.etree import ElementTree as ET
ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT/'source'/'Sunrise.FileManager'

class R07UiReleaseContract(unittest.TestCase):
    def test_every_xaml_is_well_formed(self):
        for p in SRC.rglob('*.xaml'):
            with self.subTest(p=str(p.relative_to(ROOT))):
                ET.parse(p)

    def test_theme_palette_resources_present(self):
        app=(SRC/'App.xaml').read_text('utf8')
        manager=(SRC/'Services/ThemeManager.cs').read_text('utf8')
        keys=set(re.findall(r'x:Key="(Theme\w+|AppBackground|TextPrimary|TextMuted|PanelBackground|PanelBorder|ControlBackground|Accent|Selected|Brand)"',app))
        for p in SRC.rglob('*.xaml'):
            for k in re.findall(r'\{DynamicResource (Theme\w+|AppBackground|TextPrimary|TextMuted|PanelBackground|PanelBorder|ControlBackground|Accent|Selected|Brand)\}',p.read_text('utf8')):
                self.assertIn(k,keys,f'{p.name} references unknown palette {k}')
        for k in keys:
            self.assertIn(f'["{k}"]',manager)

    def test_menu_and_splitter_have_branded_styles(self):
        app=(SRC/'App.xaml').read_text('utf8')
        explorer=(SRC/'Controls/ExplorerPane.xaml').read_text('utf8')
        shell=(SRC/'Features/Workspace/MainWindow.PanelCoordination.cs').read_text('utf8')
        for key in ['SunrisePaneMenu','SunriseVerticalSplitter','SunriseHorizontalSplitter']:
            self.assertIn(f'x:Key="{key}"',app)
        self.assertIn('Style="{StaticResource SunrisePaneMenu}"',explorer)
        self.assertIn('new GridLength(11)',shell)
        self.assertIn('"SunriseVerticalSplitter" : "SunriseHorizontalSplitter"',shell)
        self.assertIn('IsMouseOver',app)

    def test_theme_session_and_commands(self):
        session=(SRC/'Services/SessionStore.cs').read_text('utf8')
        main=(SRC/'MainWindow.xaml.cs').read_text('utf8')
        xaml=(SRC/'MainWindow.xaml').read_text('utf8')
        self.assertIn('public string Theme { get; set; } = "Light";',session)
        self.assertIn('ThemeManager.Apply(_currentTheme)',main)
        self.assertIn('Theme = _currentTheme',main)
        self.assertIn('ThemeSelector_SelectionChanged',xaml)

    def test_release_sources_are_separated(self):
        script=(ROOT/'BUILD_RELEASE_ONEDIR.cmd').read_text('ascii')
        self.assertIn('--self-contained true', script)
        self.assertIn('-p:PublishSingleFile=false', script)
        self.assertIn('SunriseFileManager_OneDir_R14',script)
        self.assertIn('SunriseFileManager_OneDir_Windows_x64_R14.zip',script)
        self.assertIn('if not exist "%STAGE%\\app\\coreclr.dll"', script)
        self.assertIn('findstr /I /R /C:"\\.cs$"',script)
        self.assertNotIn('robocopy "%ROOT%\\source" "%STAGE%"',script)

    def test_company_build_preserved(self):
        company=(ROOT/'BUILD_ON_WINDOWS_COMPANY.cmd').read_text('ascii')
        self.assertIn('BUILD_RELEASE_INSTALLER.cmd" --company',company)
        self.assertIn('BUILD_RELEASE_ONEDIR.cmd" --company', (ROOT/'BUILD_RELEASE_INSTALLER.cmd').read_text(encoding='ascii'))
        self.assertTrue((ROOT/'BUILD_RELEASE_ONEFILE.cmd').is_file())
        self.assertTrue((ROOT/'COMPANY_SOURCE_ONLY.flag').is_file())
        self.assertFalse((ROOT/'source/tests/LogicTests/Fixtures/river_report.pdf').exists())

if __name__=='__main__': unittest.main()
