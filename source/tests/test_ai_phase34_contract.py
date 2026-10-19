"""Phase 3/4 source contract checks."""
from pathlib import Path
from xml.etree import ElementTree as ET
import unittest

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / "Sunrise.FileManager"

class AgentPhase34Contract(unittest.TestCase):
    def test_wpf_panel_content_consent_control(self):
        xaml = APP / "Controls/AiCommandPanel.xaml"
        root = ET.parse(xaml).getroot()
        assert any(element.attrib.get("{http://schemas.microsoft.com/winfx/2006/xaml}Name") == "ContentReadApproval"
                   for element in root.iter())
        code = (APP / "Controls/AiCommandPanel.xaml.cs").read_text()
        self.assertIn("contentApproved", code)
        self.assertIn("recycleApproved", code)

    def test_original_tools_reused(self):
        agent = (APP / "Services/AiAgentOrchestrator.cs").read_text()
        planner = (APP / "Services/AiNaturalLanguageService.cs").read_text()
        execution = (APP / "Services/AiPlanService.cs").read_text()
        self.assertIn("AiScope.Capture", agent)
        self.assertIn("AiPlanService.Validate", agent)
        self.assertIn('new AiOperation("rename"', planner)
        self.assertIn('new AiOperation("recycle"', planner)
        self.assertIn('FileActionsService.Rename', execution)
        self.assertIn('FileActionsService.Recycle', execution)
        self.assertIn('TransferService.RunAsync', execution)
        self.assertIn('recycleApproved', execution)
        self.assertIn('FileMemoStore.ListForFile', execution)

    def test_content_read_local_and_limited(self):
        scan = (APP / "Services/AiDocumentSearchService.cs").read_text()
        preview = (APP / "Services/FilePreviewService.cs").read_text()
        self.assertIn("MaxFiles = 500", scan)
        self.assertIn("MaxTotalBytes = 256L * 1024 * 1024", scan)
        self.assertIn("ReadSearchableDocumentText", preview)
        self.assertIn("for (int page = 1", preview)
        self.assertIn("ReadOfficeZip", preview)
        self.assertNotIn("HttpClient", scan)
        self.assertNotIn("Process.Start", scan)
        self.assertIn('if (!approved)', scan)

    def test_zip_fixture_content(self):
        fixture = ROOT / 'tests/LogicTests/Fixtures/river_report.pdf'
        company_mode = (ROOT.parent / 'COMPANY_SOURCE_ONLY.flag').exists()
        if company_mode:
            self.assertFalse(fixture.exists(), 'Company source must not bundle test PDFs')
        else:
            self.assertTrue(fixture.exists())
            self.assertTrue(fixture.read_bytes().startswith(b'%PDF'))
        source = (ROOT / 'tests/LogicTests/AiAgentPhase34Tests.cs').read_text()
        self.assertIn('AiAgentPhase34Tests.RunAsync()', (ROOT / 'tests/LogicTests/Program.cs').read_text())
        self.assertIn('memo', source)

    def test_open_source_unversioned(self):
        project = (APP / "Sunrise.FileManager.csproj").read_text()
        self.assertNotIn("<Version>", project)
        self.assertIn('Title="Sunrise File Manager"', (APP / 'MainWindow.xaml').read_text())

if __name__ == '__main__': unittest.main()
