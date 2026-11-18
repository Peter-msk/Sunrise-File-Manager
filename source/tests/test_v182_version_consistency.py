"""Sunrise File Manager v1.8.2 current release-candidate contract (source-only checks)."""
from pathlib import Path
import json
import re
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
APP = ROOT / "source/Sunrise.FileManager"
V = "1.8.2"


def test_all_active_product_versions_match():
    project = ET.parse(APP / "Sunrise.FileManager.csproj").getroot()
    props = project.find("PropertyGroup")
    assert props is not None
    assert {p.tag: p.text for p in props if p.tag in ("Version", "AssemblyVersion", "FileVersion")} == {
        "Version": V, "AssemblyVersion": V + ".0", "FileVersion": V + ".0"}
    manifest = ET.parse(APP / "app.manifest").getroot()
    identity = next(p for p in manifest.iter() if p.tag.endswith("assemblyIdentity"))
    assert identity.attrib["version"] == V + ".0"
    assert (ROOT / "installer/RELEASE_VERSION.txt").read_text(encoding="utf-8").strip() == V


def test_runtime_control_and_wpf_surfaces_match():
    control = (APP / "Services/SunriseControlService.cs").read_text(encoding="utf-8")
    window = (APP / "MainWindow.xaml").read_text(encoding="utf-8")
    eiass = (APP / "Controls/EiaWorkspaceExecutionReviewWindow.xaml").read_text(encoding="utf-8")
    assert f'const string Version = "{V}"' in control
    assert f'Title="Sunrise File Manager {V}"' in window
    assert f'Text="v{V}"' in window
    assert f'Title="EIASS 사업별 폴더 생성 · v{V}"' in eiass
    assert 'sunrise_file_manager' in control


def test_release_note_is_version_scoped_but_stable_feed_is_untouched():
    notice = ROOT / f"release/NOTICE_BODY_{V}.txt"
    assert notice.exists() and f"Sunrise File Manager v{V}" in notice.read_text(encoding="utf-8")
    if (ROOT / "latest.json").exists():
        published = json.loads((ROOT / "latest.json").read_text(encoding="utf-8"))
        assert published["version"] == published["latest_version"] == V
        assert published["official_release"] is True
        assert published["download_file_name"] == f"Sunrise_File_Manager_{V}_Setup.exe"
        assert published["sha256"] == "90253f8e4d0e4dec8aee5a49cc44ff3d83e07f14a6c87e6840b25526d2a6c421"
        assert published["file_size_bytes"] == 52665816
    assert 'version="1.8.1.0"' not in (APP / "app.manifest").read_text(encoding="utf-8")


def test_version_migration_did_not_change_immutable_update_abi():
    installer = (ROOT / "installer/SunriseFileManager.iss").read_text(encoding="utf-8")
    assert 'AppId={{5305DEB7-62CF-4ED7-8A5B-8B605AD7090A}' in installer
    assert 'ApplicationVersion GetEnv("SUNRISE_INSTALLER_VERSION")' in installer
    assert 'OutputBaseFilename=Sunrise_File_Manager_{#ApplicationVersion}_Setup' in installer
    writer = (APP / "Features/EiaWorkspace/EiaWorkspaceProductionWriter.cs").read_text(encoding="utf-8")
    assert 'Directory.Delete(' not in writer  # Directory-only creation never removes user data


def test_handover_and_windows_gate_report_current_version():
    assert (ROOT / "docs/HANDOVER_EIASS_v1.8.2.md").is_file()
    current = (ROOT / "CURRENT_SOURCE.md").read_text(encoding="utf-8")
    assert 'v1.8.2 R00' in current
    powershell = (ROOT / "validation/R05/RUN_WINDOWS_GATES.ps1").read_text(encoding="utf-8")
    assert "version='1.8.2 R00'" in powershell
    assert '--v181-r03-transfer-safety' in powershell, 'Preserve historical native smoke CLI contract'


def test_eiass_menu_uses_current_reviewed_creation_copy_not_archived_prelaunch_copy():
    root = ET.parse(APP / "MainWindow.xaml").getroot()
    menus = [node for node in root.iter() if node.attrib.get("Click") == "EiaWorkspacePlanning_Click"]
    assert len(menus) == 1
    tip = menus[0].attrib.get("ToolTip", "")
    assert "승인된 누락 폴더만 생성" in tip
    assert "NAS/SMB" in tip
    assert "운영 생성 불가" not in tip
