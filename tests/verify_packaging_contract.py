from pathlib import Path
import json
import re

root = Path(__file__).resolve().parents[1]
src = root / "source"
cmd_data = (root / "BUILD_DEV_FULL_SOURCE.cmd").read_bytes()
cmd = cmd_data.decode("ascii")
sdk_root = json.loads((root / "global.json").read_text(encoding="utf-8"))
sdk_src = json.loads((src / "global.json").read_text(encoding="utf-8"))
checks = {
    "no_powershell_dependency": not any(root.rglob("*.ps1")),
    "ascii_crlf_cmd": b"\r\n" in cmd_data and b"\n" not in cmd_data.replace(b"\r\n", b""),
    "sdk_10_consistent": sdk_root == sdk_src and sdk_root["sdk"] == {"version": "10.0.100", "rollForward": "latestFeature", "allowPrerelease": False},
    "recognizes_dotnet_10": '/C:"10\\."' in cmd,
    "no_downgrade_sdk_8_9": "SDK_MAJOR" not in cmd and "TFM_CONSOLE=" not in cmd,
    "no_framework_dependent_fallback": "--self-contained false" not in cmd and "--self-contained true" in cmd,
    "verified_wpf_runtime": all(x in cmd for x in ('hostfxr.dll', 'coreclr.dll', 'PresentationFramework.dll')),
    "stage_then_publish": 'STAGE_DIR=' in cmd and 'FINAL_DIR=' in cmd,
    "full_source_copy": r'robocopy "%ROOT%\source"' in cmd and 'Sunrise.FileManager.csproj" goto :fail_package' in cmd,
    "zip_validation": 'tar.exe -t -f' in cmd and '04_zip_contents.log' in cmd,
    "error_status": 'STATUS=FAILED' in cmd and 'STATUS=SUCCESS' in cmd,
    "runner_exists": (root / 'BUILD_AND_CAPTURE_WINDOWS.cmd').is_file(),
    "current_installer_company": 'BUILD_RELEASE_INSTALLER.cmd' in (root / 'BUILD_ON_WINDOWS_COMPANY.cmd').read_text(encoding='ascii') and 'BUILD_RELEASE_ONEDIR.cmd' in (root / 'BUILD_RELEASE_INSTALLER.cmd').read_text(encoding='ascii'),
    "source_complete": (src / 'Sunrise.FileManager/MainWindow.xaml').is_file() and (src / 'Sunrise.FileManager/Services/AiOllamaService.cs').is_file(),
    "install_ollama_separate": (root / 'INSTALL_OLLAMA.cmd').is_file() and 'INSTALL_OLLAMA.cmd"' in cmd,
}
for k,v in checks.items():
    print(("PASS" if v else "FAIL") + ": " + k)
assert all(checks.values()), [k for k,v in checks.items() if not v]
print(f"PASS: {len(checks)} CMD packaging contract checks")
