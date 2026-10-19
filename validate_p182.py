from pathlib import Path
import re
from lxml import etree

r = Path(__file__).resolve().parent / 'source/Sunrise.FileManager'
ns = '{http://schemas.microsoft.com/winfx/2006/xaml/presentation}'
fx = '{http://schemas.microsoft.com/winfx/2006/xaml}'
N = 0

def check(value, message):
    global N
    N += 1
    if not value:
        raise AssertionError(message)

for p in r.rglob('*.xaml'):
    etree.parse(str(p))
    N += 1

for name in ['MainWindow', 'Controls/FileInspector', 'Controls/FilePreviewPanel', 'Controls/OllamaSetupDialog']:
    x = r / (name + '.xaml')
    code = (r / (name + '.xaml.cs')).read_text()
    root = etree.parse(str(x)).getroot()
    xnames = []
    for e in root.iter():
        n = e.get(fx + 'Name')
        if n:
            check(n not in xnames, f'duplicate XAML name {name} {n}')
            xnames.append(n)
        for k, v in e.attrib.items():
            if k.startswith('{'):
                continue
            if k in ['Click', 'SelectionChanged', 'TextChanged', 'Closing', 'PreviewKeyDown'] and v and not v.startswith('{'):
                check(re.search(r'\b' + re.escape(v) + r'\s*\(', code) is not None, f'{name} event {v} missing')
    xclass = root.get(fx + 'Class')
    check(xclass and xclass.split('.')[-1] == name.rsplit('/', 1)[-1], f'{name} XAML class')

main = etree.parse(str(r / 'MainWindow.xaml'))
find = lambda name: main.xpath('//*[@x:Name="' + name + '"]', namespaces={'x': 'http://schemas.microsoft.com/winfx/2006/xaml'})
inspector = find('Inspector')[0]
pane = find('PreviewPane')[0]
shell = find('FileAreaShell')[0]
outer = find('WorkspaceShell')[0]
check(inspector.get('Grid.Column') == '2', 'inspector inner col')
check(pane.get('Grid.Column') == '2', 'preview outer col')
check(inspector.getparent() is shell, 'info/memo inside inner shell')
check(pane.getparent() is outer, 'preview directly under outer shell')
check(shell.getparent() is outer, 'nested shell under outer root')
check(len(find('PreviewColumnSplitter')) == 1, 'independent preview divider')
check(len(find('InspectorSplitter')) == 1, 'independent inspector divider')
fi = etree.parse(str(r / 'Controls/FileInspector.xaml'))
check(not fi.xpath('//*[@x:Name="PreviewRegion"]', namespaces={'x': 'http://schemas.microsoft.com/winfx/2006/xaml'}), 'inspector no preview')
fp = etree.parse(str(r / 'Controls/FilePreviewPanel.xaml'))
check(fp.xpath('//*[@x:Name="PreviewRegion"]', namespaces={'x': 'http://schemas.microsoft.com/winfx/2006/xaml'}), 'preview standalone own')
cs = (r / 'MainWindow.xaml.cs').read_text()
check('PreviewPane.SetPreviewVisible(visible)' in cs, 'preview toggle independently')
check('if (visible && !_inspectorOpen)' not in cs, 'preview does not force inspector open')
check('x:Name="AiViewport" Grid.Row="1" Grid.Column="0"' in (r / 'MainWindow.xaml').read_text(), 'AI always below explorer')
check('Grid.SetColumn(AiViewport' not in cs, 'AI no longer occupies a right dock column')
check('Inspector.ConfirmLeavingMemo()' in cs, 'unsaved memo protection')
check('LayoutVersion = 19' in cs or 'LayoutVersion = 20' in cs, 'new layout persisted (v19/v20)')
check('Inspector.SelectFile(' in cs and 'PreviewPane.SelectFile(' in cs, 'both receive selection')
check('new PreviewWindow(' not in cs, 'no popups in main')
check('PreviewPane.DisposePending()' in cs, 'pending async canceled on close')
check('PreviewColumn.Width = visible ? new GridLength(_previewPreferredWidth)' in cs, 'preview dedicated column shown')
check('InspectorColumn.Width = visible ? new GridLength(_inspectorPreferredWidth)' in cs, 'details dedicated column shown')
check('PreviewPane.Visibility = PreviewColumnSplitter.Visibility = visible' in cs, 'preview hidden independent')
check('Inspector.Visibility = InspectorSplitter.Visibility = visible' in cs, 'info/memo hidden independent')
check('Grid.SetColumn(previewPane)' not in cs, 'preview always rightmost no reparent')
check('<Version>' not in (r / 'Sunrise.FileManager.csproj').read_text(), 'unversioned open source csproj')
print(f'PASS: {N} independent column / XAML / handler / lifecycle static checks')
