"""1.1.0 UI/source contracts. Static tests are not a substitute for Windows WPF smoke tests."""
import json, re, unittest
from pathlib import Path
import xml.etree.ElementTree as ET
ROOT=Path(__file__).resolve().parents[1]
APP=ROOT/'source/Sunrise.FileManager'

class V110ThemeAndRelease(unittest.TestCase):
    def test_version_is_1_1_0_but_public_feed_remains_published_1_0_0(self):
        self.assertEqual((ROOT/'installer/RELEASE_VERSION.txt').read_text('ascii').strip(),'1.2.0')
        self.assertIn('public const string Version = "1.2.0";', (APP/'Services/SunriseControlService.cs').read_text())
        self.assertIn('Sunrise File Manager 1.2.0',(APP/'MainWindow.xaml').read_text())
        self.assertIn('1.2.0',(APP/'Sunrise.FileManager.csproj').read_text())
        feed=json.loads((ROOT/'latest.json').read_text())
        self.assertEqual(feed['latest_version'],'1.0.0')
        self.assertEqual(feed['sha256'],'eb866da915459cb1c61df22039957f6e07c9d863293a406a40ea5fcb47d62cdd')
        self.assertEqual(feed['file_size_bytes'],51251864)
    def test_windows_app_theme_setting_and_change_event(self):
        manager=(APP/'Services/ThemeManager.cs').read_text()
        shell=(APP/'MainWindow.xaml.cs').read_text()
        self.assertIn('AppsUseLightTheme', manager)
        self.assertIn('Registry.GetValue(', manager)
        self.assertIn('choice == "System" && SystemUsesDark()', manager)
        self.assertIn('SystemEvents.UserPreferenceChanged += SystemThemeChanged', shell)
        self.assertIn('SystemEvents.UserPreferenceChanged -= SystemThemeChanged', shell)
        self.assertIn('Dispatcher.BeginInvoke',shell)
        self.assertIn('"System" => "◐ PC 테마"',shell)
        self.assertIn('public string Theme { get; set; } = "System";', (APP/'Services/SessionStore.cs').read_text())
    def test_3_theme_modes_quick_button_and_split_contrast(self):
        view=(APP/'MainWindow.xaml').read_text(); cs=(APP/'MainWindow.xaml.cs').read_text()
        for item in ['ThemeSystemMenuItem', 'ThemeLightMenuItem','ThemeDarkMenuItem', 'HeaderThemeButton']:
            self.assertIn('x:Name="'+item+'"', view)
        for method in ['ThemeSystem_Click','ThemeLight_Click','ThemeDark_Click','ThemeCycle_Click']:
            self.assertIn('void '+method+'(',cs)
        self.assertIn('SetResourceReference(Control.BackgroundProperty, selected ? "ThemeSplitActive" : "ThemeSplitInactive")',cs)
        self.assertIn('SetResourceReference(Control.ForegroundProperty, selected ? "ThemeSplitActiveText" : "ThemeSplitText")',cs)
        palette=(APP/'Services/ThemeManager.cs').read_text()
        for k in ['ThemeSplitActiveText','ThemeSplitText','ThemeScrollThumb','ThemeComboPopup','ThemeComboSelected']:
            self.assertIn('["'+k+'"]',palette)
    def test_dropdown_and_tree_selection_have_theme_aware_templates(self):
        app=(APP/'App.xaml').read_text()
        for snippet in ['<ControlTemplate TargetType="ComboBox">','<ControlTemplate TargetType="ComboBoxItem">',
                        '{DynamicResource ThemeComboPopup}', '{DynamicResource ThemeComboSelected}',
                        '<ControlTemplate TargetType="TreeViewItem">', '{DynamicResource Selected}',
                        '<ControlTemplate TargetType="ScrollBar">','<Track x:Name="PART_Track"']:
            self.assertIn(snippet,app)
        tree=(APP/'Controls/FolderTree.xaml.cs').read_text()
        self.assertIn('FolderItems.AddHandler(TreeViewItem.ExpandedEvent',tree)
    def test_icons_compact_menu_and_ai_pane(self):
        a=(APP/'App.xaml').read_text()
        model=(APP/'Models/FileEntry.cs').read_text()
        explorer=(APP/'Controls/ExplorerPane.xaml').read_text()
        ai=(APP/'Controls/AiCommandPanel.xaml').read_text()
        self.assertIn('Background="{Binding IconColor}"',a)
        self.assertIn('FontSize="9" FontWeight="Bold"',a)
        self.assertIn('public string IconColor',model)
        self.assertIn('CompactContextMenuItem',explorer)
        self.assertIn('MinHeight" Value="24"',a)
        self.assertIn('IsExpanded="False" Margin="0,5,0,0"',ai)
        self.assertIn('ThemeComboPopup',a)
    def test_bottom_notice_update_and_existing_handlers(self):
        view=(APP/'MainWindow.xaml').read_text()
        cs=(APP/'MainWindow.xaml.cs').read_text()
        for label,method in [('공지사항','Notice_Click'),('업데이트','UpdateCheck_Click')]:
            self.assertIn('Content="'+label+'"',view)
            self.assertIn('Click="'+method+'"',view)
            self.assertIn('void '+method+'(',cs)
        dialog=(APP/'Controls/SunriseControlWindow.cs').read_text()
        self.assertIn('SetResourceReference(BackgroundProperty, "PanelBackground")',dialog)
        self.assertIn('SetResourceReference(ForegroundProperty, "TextPrimary")',dialog)
    def test_notice_is_version_specific_not_overwrite_published_version(self):
        g=(ROOT/'build-tools/Sunrise.FileManager.ReleaseMetadata/Program.cs').read_text()
        self.assertIn('NOTICE_BODY_{version}.txt',g)
        self.assertIn('File.Exists(noticeTemplate)',g)
        self.assertNotIn('[Path.Combine(root, "latest.json")]',g)
        self.assertNotIn('[Path.Combine(root, "notice.txt")]',g)
        self.assertIn('1.1.0이 출시', (ROOT/'release/NOTICE_BODY_1.1.0.txt').read_text())
        self.assertEqual((ROOT/'release/NOTICE_BODY_1.0.0.txt').read_bytes(),(ROOT/'release/NOTICE_BODY.txt').read_bytes())
    def test_all_wpf_markup_is_well_formed(self):
        for x in APP.rglob('*.xaml'):
            with self.subTest(x=x.name): ET.parse(x)

if __name__=='__main__': unittest.main()
