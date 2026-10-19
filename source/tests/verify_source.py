"""Supplemental XAML wiring and Open Source + Ollama release contract audit."""
from pathlib import Path
from xml.etree import ElementTree as ET
import json
import re

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / 'Sunrise.FileManager'
checks = []
X = '{http://schemas.microsoft.com/winfx/2006/xaml}'
EVENTS = {'Click', 'KeyDown', 'TextChanged', 'SelectionChanged', 'SelectedItemChanged', 'MouseDoubleClick',
          'Sorting', 'Closing', 'PreviewDragOver', 'PreviewDrop', 'PreviewMouseLeftButtonDown',
          'PreviewMouseMove', 'PreviewKeyDown', 'DragLeave', 'PreviewMouseWheel', 'ValueChanged', 'LoadingRow', 'PreviewMouseRightButtonDown',
          'Checked', 'Unchecked'}

def check(name, condition):
    checks.append({'name': name, 'status': 'PASS' if condition else 'FAIL'})

roots = {p: ET.parse(p).getroot() for p in PROJECT.rglob('*.xaml')}
keys = {node.get(X+'Key') for root in roots.values() for node in root.iter() if node.get(X+'Key')}
for path, root in roots.items():
    check('XAML parses: ' + path.name, True)
    names = [n.get(X+'Name') for n in root.iter() if n.get(X+'Name')]
    check('unique names: ' + path.name, len(names) == len(set(names)))
    codepath = Path(str(path) + '.cs')
    code = codepath.read_text() if codepath.exists() else ''
    for node in root.iter():
        for attribute, value in node.attrib.items():
            if attribute.split('}')[-1] in EVENTS:
                check('handler: ' + path.name + ':' + value, bool(re.search(r'\b' + re.escape(value) + r'\s*\(', code)))
            for key in re.findall(r'\{StaticResource\s+([\w-]+)\}', value):
                check('resource: ' + path.name + ':' + key, key in keys)

allcode = '\n'.join(p.read_text() for p in PROJECT.rglob('*.cs') if not {'bin', 'obj'}.intersection(p.parts))
proj = (PROJECT / 'Sunrise.FileManager.csproj').read_text()
main = (PROJECT / 'MainWindow.xaml.cs').read_text()
mainx = (PROJECT / 'MainWindow.xaml').read_text()
app = (PROJECT / 'App.xaml.cs').read_text()
check('unversioned open source csproj', '<Version>' not in proj and 'Open Source Edition' in proj)
check('retired browser dependency absent', 'CefSharp' not in proj and 'ChromePane' not in allcode and 'Cef.Initialize' not in app)
check('gemini completely removed', not (PROJECT / 'Services/AiGeminiPlanner.cs').exists() and 'AiGeminiPlanner' not in allcode)
check('ollama onboard service present', (PROJECT / 'Services/AiOllamaService.cs').exists() and (PROJECT / 'Controls/OllamaSetupDialog.xaml').exists())
ollama = (PROJECT / 'Services/AiOllamaService.cs').read_text()
check('ollama uses cmd.exe and never powershell', 'FileName = "cmd.exe"' in ollama and 'powershell.exe' not in ollama.lower() and 'pwsh' not in ollama.lower())
check('ollama downloads from github ollama release', 'https://github.com/ollama/ollama/releases/latest/download/OllamaSetup.exe' in ollama)
check('single explorer default', 'private int _visiblePanels = 1;' in main)
check('all split choices retained', all(x + '_Click' in main for x in ['One', 'Two', 'Three', 'Four']))
check('AI starts collapsed', 'x:Name="AiPanel"' in mainx and 'Visibility="Collapsed"' in mainx)
check('AI connect button in main window', 'x:Name="AiConnectButton"' in mainx and 'AiConnect_Click' in main)
check('plans do not refresh away selection', 'if (!busy) RefreshAllPanes()' not in main and 'FileWorkCompleted = RefreshAllPanes' in main)
check('legacy session reader accepts unknown properties', 'JsonSerializer.Deserialize<AppSession>' in (PROJECT/'Services/SessionStore.cs').read_text())
check('preview renderer retained', (PROJECT/'Services/WindowsPdfRenderer.cs').exists() and (PROJECT/'Controls/PreviewWindow.xaml').exists())
journal = (PROJECT/'Services/AiJournalStore.cs').read_text()
recovery = (PROJECT/'Services/AiRecoveryService.cs').read_text()
check('native per-user protection', 'CryptProtectData' in journal and 'CryptUnprotectData' in journal)
check('durable intent and result flush', 'stream.Flush(true)' in journal)
check('record chain verification', 'entry.PreviousHash != snapshot.LastHash' in journal)
check('history UI exists', (PROJECT/'Controls/AiHistoryWindow.xaml').exists())
check('interruption startup check', 'CheckInterruptedAsync' in main)
check('undo reuses file owners', 'FileActionsService.Rename' in recovery and 'TransferService.RunAsync' in recovery)
check('no destructive inverse', 'File.Delete' not in recovery and 'Directory.Delete' not in recovery)
check('separate recycle approval', 'recycleApproved' in (PROJECT/'Services/AiPlanService.cs').read_text())
check('native recycle-only flag', 'RecycleOnly = 0x00080000' in (PROJECT/'Services/WindowsRecycleService.cs').read_text())
check('legacy recycle API retired', 'Microsoft.VisualBasic.FileIO' not in (PROJECT/'Services/FileActionsService.cs').read_text())
check('local inspection UI', (PROJECT/'Controls/AiInspectionWindow.xaml').exists())
inspection = (PROJECT/'Services/AiInspectionService.cs').read_text()
content = (PROJECT/'Services/AiContentClassifier.cs').read_text()
check('size first then SHA256', 'GroupBy(x => x.Length)' in inspection and 'HashAlgorithmName.SHA256' in inspection)
check('inspection is read only', not re.search(r'File\.(Write|Delete|Move|Copy)|Directory\.(Delete|Move)', inspection.split('public static class AiInspectionSettings')[0]))
check('no content network client', 'HttpClient' not in content)
session = (PROJECT/'Services/SessionStore.cs').read_text()
check('bounded session input and output', 'SerializeToUtf8Bytes' in session and 'data.Length > 1024 * 1024' in session and 'new FileInfo(path).Length > 1024 * 1024' in session)
check('unique session staging', 'Guid.NewGuid().ToString("N")' in session)
check('transfer terminal result stops pending activity', main.index('progress.Dispose();') < main.index('JobStatus.Text = result.Summary;'))
panel = (PROJECT/'Controls/AiCommandPanel.xaml.cs').read_text()
check('AI terminal result stops pending activity', panel.index('activity.Dispose();') < panel.index('PlanGrid.ItemsSource = result.Rows;'))
report = {'product': 'Sunrise File Manager', 'edition': 'Open Source', 'result': 'FAIL' if any(c['status']=='FAIL' for c in checks) else 'PASS',
          'count': len(checks), 'checks': checks}
(ROOT/'reports').mkdir(exist_ok=True)
(ROOT/'reports/STATIC_RESULT.json').write_text(json.dumps(report, ensure_ascii=False, indent=2))
print(f"Static audit {report['result']}: {len(checks)} contracts")
for item in checks:
    if item['status'] == 'FAIL': print('FAIL', item['name'])
raise SystemExit(report['result'] != 'PASS')
