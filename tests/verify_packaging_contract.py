"""R12 current product packaging contract; no Windows commands executed."""
from pathlib import Path
import json
root = Path(__file__).resolve().parents[1]
src = root / 'source'
main = (root/'BUILD_ON_WINDOWS_COMPANY.cmd').read_text('ascii')
installer = (root/'BUILD_RELEASE_INSTALLER.cmd').read_text('ascii')
payload = (root/'BUILD_RELEASE_ONEDIR.cmd').read_text('ascii')
unsigned = (root/'BUILD_DEV_EXE.cmd').read_text('ascii')
signer = (root/'build-tools/EnsureSunriseGroupSigning.cmd').read_text('ascii')
checks = {
    'single_official_installer_entry': 'BUILD_RELEASE_INSTALLER.cmd' in main and 'BUILD_RELEASE_ONEDIR.cmd' in installer,
    'signed_setup_and_uninstaller': '/SSunriseGroup=' in installer and 'SignedUninstaller=yes' in (root/'installer/SunriseFileManager.iss').read_text(),
    'no_manual_signing_input': 'APPROVE_EXISTING_GROUP_SIGNER.cmd' not in main and 'choice /C' not in main and 'set /p' not in main,
    'automatic_first_signer': 'certreq.exe -new -user -q' in signer and 'pending_group_public.cer' in signer,
    'stable_pin': 'group_signer.sha1' in signer and 'STATUS=REUSED_PINNED' in signer,
    'fail_closed_pin_missing': 'STATUS=PINNED_CERT_MISSING_OR_INVALID' in signer,
    'fail_closed_prior_release': 'STATUS=EXISTING_RELEASE_WITHOUT_PIN' in signer,
    'no_powershell_or_v3_bypass': not any(x in main+installer+payload+signer for x in ('powershell.exe','-ExecutionPolicy Bypass','Add-MpPreference','certutil.exe -addstore Root')),
    'source_and_sdk_consistent': json.loads((root/'global.json').read_text()) == json.loads((src/'global.json').read_text()),
    'dotnet_10_requirement': 'FindDotNet10.cmd' in (root/'CHECK_INSTALLER_BUILD_ENV.cmd').read_text('ascii'),
    'signed_wpf_is_self_contained': '--self-contained true' in payload and '-p:PublishSingleFile=false' in payload,
    'unsigned_dev_independent': 'SignTool / certificate / Inno Setup are NOT needed' in unsigned,
    'staging_and_rollback': 'STAGE' in installer and 'BACKUP_SETUP' in installer and 'fail_restore' in installer,
    'no_deprecated_builders': all(not (root/name).exists() for name in ('BUILD_DEV_FULL_SOURCE.cmd','BUILD_RELEASE_ONEFILE.cmd','BUILD_ON_WINDOWS.cmd','BUILD_AND_CAPTURE_WINDOWS.cmd','CAPTURE_UI_WINDOWS.cmd','APPROVE_EXISTING_GROUP_SIGNER.cmd')),
    'app_src_present': (src/'Sunrise.FileManager/MainWindow.xaml').is_file() and (src/'Sunrise.FileManager/Services/AiOllamaService.cs').is_file(),
}
for k,v in checks.items():print(('PASS' if v else 'FAIL') + ': ' + k)
assert all(checks.values()),[k for k,v in checks.items() if not v]
print(f'PASS: {len(checks)} R12 packaging contract checks')
