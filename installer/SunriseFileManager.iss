; Sunrise File Manager R15 - installer is the ONLY official user distribution.
; The WPF app + dependencies originate from a previously signed self-contained publish.
; Inno Setup Compiler ISCC.exe must run on the Windows BUILD machine.
; No application data or user files are read or modified by installer compilation.
#define ReleaseSource GetEnv("SUNRISE_INSTALLER_PAYLOAD")
#define InstallerOutput GetEnv("SUNRISE_INSTALLER_OUTPUT")
#define ApplicationVersion GetEnv("SUNRISE_INSTALLER_VERSION")
#if ReleaseSource == ""
  #error "SUNRISE_INSTALLER_PAYLOAD missing: use BUILD_ON_WINDOWS_COMPANY.cmd"
#endif
#if InstallerOutput == ""
  #error "SUNRISE_INSTALLER_OUTPUT missing: use BUILD_ON_WINDOWS_COMPANY.cmd"
#endif
#if ApplicationVersion == ""
  #error "SUNRISE_INSTALLER_VERSION missing: use BUILD_ON_WINDOWS_COMPANY.cmd"
#endif

[Setup]
; Stable AppId: DO NOT CHANGE in subsequent versions. Enables in-place upgrades.
AppId={{5305DEB7-62CF-4ED7-8A5B-8B605AD7090A}
AppName=Sunrise File Manager
AppVersion={#ApplicationVersion}
AppVerName=Sunrise File Manager {#ApplicationVersion}
AppPublisher=Sunrise Group
VersionInfoCompany=Sunrise Group
VersionInfoDescription=Sunrise File Manager Setup
VersionInfoProductName=Sunrise File Manager
VersionInfoVersion={#ApplicationVersion}
VersionInfoProductVersion={#ApplicationVersion}
DefaultDirName={localappdata}\Programs\Sunrise File Manager
UsePreviousAppDir=yes
; Matches the mutex created in App.OnStartup. Protects install AND uninstall.
AppMutex=Local\SunriseFileManagerCEF
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19041
WizardStyle=modern
SetupIconFile=..\source\Sunrise.FileManager\Assets\SunriseFileManager.ico
UninstallDisplayIcon={app}\Sunrise.FileManager.exe
OutputDir={#InstallerOutput}
OutputBaseFilename=Sunrise_File_Manager_{#ApplicationVersion}_Setup
; Inno signs Setup + its generated uninstaller through the supplied build-only SignTool.
SignTool=SunriseGroup
SignedUninstaller=yes
Compression=lzma2
SolidCompression=yes
CloseApplications=no
RestartApplications=no
RestartIfNeededByRun=no
; Never auto-elevate, auto-run, restart or delete the user's LocalAppData settings.

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕화면에 바로가기 만들기"; Flags: unchecked

[Files]
Source: "{#ReleaseSource}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}\licenses"; Flags: ignoreversion
; Required third-party and bundled .NET runtime notices (not business documents).
Source: "..\source\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Sunrise File Manager"; Filename: "{app}\Sunrise.FileManager.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\Sunrise File Manager"; Filename: "{app}\Sunrise.FileManager.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Sunrise.FileManager.exe"; Description: "Sunrise File Manager 실행"; Flags: nowait postinstall skipifsilent unchecked

; AppMutex protects both Setup and Uninstall without relying on localized window titles.
