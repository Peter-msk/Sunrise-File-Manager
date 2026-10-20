# Sunrise File Manager

Sunrise Group의 Windows 데스크톱 파일 관리 프로그램입니다. **.NET 10 / WPF** 기반이며 1~4분할 탐색기, 파일 미리보기, 파일별 `#memo`, 선택형 로컬 Ollama 연동을 지원합니다.

> **현재 소스 기준: 1.0.0 R19**  
> **공식 배포 방식: 설치형 Setup EXE (Inno Setup)**  
> **검증 상태: 소스·정적 검사 기준. Windows 실기 빌드, 그룹서명, 설치·업그레이드·제거 검증은 미완료(PENDING).**

## 주요 기능

- 1~4개 탐색기 분할, 탭, 파일 검색·분류, 폴더 트리, 파일 복사·이동·이름변경·휴지통 삭제
- 상세정보·미리보기 패널, PDF/HWPX/DOCX/이미지 등 미리보기 경로
- 파일 옆 `#memo` 폴더에 공유 가능한 `.sunmemo` 저장
- R19: 메모 작성자 직접 입력(비워두면 Windows PC 사용자명), 로컬 최초 관측 기준의 변경 경고
- R19: 사용자 **선택 시에만** DPAPI로 보호되는 날짜별 메모 복원 이력 생성
- 로컬 Ollama AI 연결은 선택 기능이며, AI 없이도 파일 관리 가능
- 창·탭·분할 및 일부 탐색 상태 재실행 복원

### R19 설치형 버전검사 핫픽스 R01

`VERSION_OR_INSTALLER_CONFIG` 오류를 세부 진단하도록 개선했습니다. 정상 `installer/RELEASE_VERSION.txt`는 `1.0.0`이며, 문제 발생 시 `build-logs/R15_version_preflight.log`와 `R15_INSTALLER_BUILD_RESULT.txt`를 확인합니다. 상세 내용은 `docs/R19_INSTALLER_VERSION_HOTFIX_R01.md`를 참조하세요. Windows 실빌드 결과는 아직 검증되지 않았습니다.

### R19 V3 그룹서명 실행파일 경고 개선 R02

과거 미서명 `Sunrise.Signing.exe`는 R02에서 제거했으며 R05에서는 V3 검사 대상으로 표시된 `Sunrise.GroupSigning.ps1`도 공식 빌드 경로와 전체 소스에서 제거했습니다. 현재는 **Sunrise Capture 방식**인 Windows `certutil.exe` 기반의 기존 인증서 지문 선택과 SDK `signtool.exe` 서명·검증을 사용합니다. V3 예외 등록이나 보안정책 변경은 하지 않으며, 실제 V3 재검증은 미완료입니다. `docs/R19_V3_SIGNING_EXECUTABLE_HOTFIX_R02.md`를 참조하십시오.

### R19 Sunrise Capture 방식 그룹서명 R05

`build-tools/FindSunriseCertificate.cmd`는 기존 `CN=Sunrise Group`의 SHA-1 지문을 `certutil -user -store My` 출력에서 읽고 검증합니다. 동명이인 인증서가 여러 개면 자동 선택하지 않고 중단하며, IT가 승인한 지문만 `SUNRISE_CERT_SHA1`로 지정할 수 있습니다. 그룹서명용 PowerShell·자체 보조 EXE를 실행하지 않습니다. 최초 Windows 실기 검증 전입니다.

### R19 설치형 빌드 사전점검 R03

`INNO_SETUP_NOT_FOUND` 진단을 위해 회사용 빌드 전에 `CHECK_INSTALLER_BUILD_ENV.cmd`를 실행할 수 있습니다. Inno Setup이 없으면 설치/사용승인을 받은 후 설치해야 하며 빌드 스크립트는 자동 다운로드·설치를 하지 않습니다. 기본 위치/현재 사용자 위치/설치 레지스트리/PATH와 명시적인 `SUNRISE_ISCC` 경로를 탐색합니다. `build-logs/R15_inno_probe.log`에서 진단합니다. 실서명·Windows 설치 검증은 여전히 별도로 필요합니다.

### R19 .NET 10 SDK 공백 경로 핫픽스 R04

사전점검에서 `Inno Setup`과 `SignTool`은 PASS인데 `'C:\Program'은(는) ...`와 `[FAIL] .NET 10 SDK not found`가 함께 발생하던 CMD 인용부호 오류를 수정했습니다. `build-tools/FindDotNet10.cmd`가 SDK 후보를 직접 실행해 `--version` 결과를 로그로 저장한 뒤 판독하며, 사전점검과 실제 WPF 게시 경로가 동일한 탐색기를 사용합니다. 실제 SDK 설치 여부가 확인되지 않으면 설치를 단정하지 않습니다. 자세한 내용은 `docs/R19_DOTNET_SDK_PATH_HOTFIX_R04.md`를 참조하세요.

## 빌드 환경

**Windows x64 빌드 PC**에서 다음 개발도구가 필요합니다.

1. .NET 10 SDK (WPF Windows 타깃)
2. Inno Setup 6/7 및 `ISCC.exe`
3. Windows SDK `signtool.exe`
4. Windows `certutil.exe` 및 빌드 PC의 기존 코드서명 인증서 (`CN=Sunrise Group`, 개인키 포함). PowerShell은 필요하지 않습니다.

명령 프롬프트에서 프로젝트 루트의 다음 파일을 실행합니다.

```cmd
CHECK_INSTALLER_BUILD_ENV.cmd
BUILD_ON_WINDOWS_COMPANY.cmd
```

Inno Setup이 다른 위치에 설치된 경우 빌드를 실행하는 CMD 창에서 명시적으로 설정할 수 있습니다.

```cmd
set "SUNRISE_ISCC=C:\YourApprovedTools\Inno Setup 6\ISCC.exe"
BUILD_ON_WINDOWS_COMPANY.cmd
```


빌드는 `BUILD_RELEASE_INSTALLER.cmd` → `BUILD_RELEASE_ONEDIR.cmd --installer-payload` → WPF 게시 → Authenticode 서명 → Inno Setup 설치파일 생성·서명 → 검증 순으로 진행됩니다. 빌드 성공 시 `dist\Sunrise_File_Manager_1.0.0_Setup.exe`를 기대합니다(버전은 `installer\RELEASE_VERSION.txt`를 기준으로 생성). 회사 빌드는 문서 내용 검사·문서 Fixture·UI 캡처를 실행하지 않습니다.

**현재 저장소에 Windows 실행파일, 인증서, 설치 프로그램, 암호 또는 개인키는 포함하지 않습니다.** 이 저장소의 소스를 받은 것만으로 Windows 릴리즈가 검증되었다는 의미는 아닙니다.

## 소스 구조

| 경로 | 역할 |
| --- | --- |
| `source/` | WPF 애플리케이션, 파일 처리·메모·AI 서비스, 프로젝트 파일 및 계약 검사 |
| `installer/` | 설치형 버전 및 Inno Setup 스크립트 |
| `docs/` | R17 인수인계·개발계획, R18 빌드 감사, R14 서명 안내 |
| `tests/` | 개발·배포 정적/계약 테스트 |
| `ui-audit/` | HTML 기반 UI 검토 자료 (Windows 실행 검증과 별개) |
| `BUILD_ON_WINDOWS_COMPANY.cmd` | 공식 회사용 설치형 빌드 진입점 |
| `R19_MEMO_AUTHOR_INTEGRITY_HANDOFF.md` | 메모 작성자·변경 경고·이력 기능 설계와 보안 한계 |

## 메모 보안 모델 주의

- 메모 작성자 이름은 **신원 인증이 아닙니다**. 임의 입력이 가능합니다.
- 공유 `.sunmemo` 파일은 암호화하지 않습니다. 원본 변경 경고는 **해당 PC에서 최초 관측한 기준**과 비교하며, 최초 관측 이전의 변경이나 동일 Windows 사용자 권한을 가진 공격자의 조작을 입증할 수 없습니다.
- DPAPI 이력은 사용자가 활성화한 경우에만 사용자 계정의 로컬 AppData에 저장됩니다. 업무상 정책으로 메모 복제가 금지되는 PC에서는 활성화하면 안 됩니다.
- `Ollama`는 기본적인 파일 관리 기능에 필수이지 않습니다.

## 개발 및 릴리즈 상태

현재 저장소는 **개발 소스 기준본**입니다. 다음 단계는 실제 Windows 환경에서 Setup 파일 생성, Sunrise Group 서명 확인, 최초 설치·업그레이드·제거, 파일 메모 공유·복원, 회사 보안 솔루션 연동에 대한 테스트입니다.

참고 문서: `docs/R18_FINAL_BUILD_INSTALLER_AUDIT.md`, `R19_MEMO_AUTHOR_INTEGRITY_HANDOFF.md`, `docs/Sunrise_File_Manager_1.0.0_R17_작업인수인계서.md`, `docs/Sunrise_File_Manager_1.0.0_R17_개발계획서.md`.

## 라이선스

원본 프로젝트는 `LICENSE`에서 Apache License 2.0을 표기하고 있습니다. 공식 공개/재배포 전에는 포함된 외부 라이브러리의 라이선스와 고지 의무를 별도로 검토하십시오.
