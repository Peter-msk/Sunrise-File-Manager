"""R05 source-only regression. No document files are opened."""
from pathlib import Path
import re
import xml.etree.ElementTree as ET
ROOT=Path(__file__).resolve().parents[2]
XAML=ROOT/'source/Sunrise.FileManager/Controls/AiCommandPanel.xaml'
CS=ROOT/'source/Sunrise.FileManager/Controls/AiCommandPanel.xaml.cs'

def test_selection_summary_xaml_contract():
    tree=ET.parse(XAML)
    names={v for e in tree.iter() for k,v in e.attrib.items() if k.endswith('}Name')}
    code=CS.read_text(encoding='utf-8-sig')
    assert 'ScopeLabel' in names
    assert 'ScopeLabel.Text = $' in code
    assert 'SelectionSummary.Text' not in code
    assert 'SelectionSummary' not in names
