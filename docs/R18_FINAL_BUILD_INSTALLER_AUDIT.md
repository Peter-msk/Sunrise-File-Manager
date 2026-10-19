# Sunrise File Manager 1.0.0 R18 — 전면 재감사 보고서

- 작성일: 2026-10-20
- 기준 ZIP: `Sunrise_File_Manager_1.0.0_R17_R01.zip`
- 기준본 SHA-256: `9417e9a4cfbbca5b67577537d846585ec85590c8d160b0e33dff50912fb8a884`
- 배포 계약: **Windows x64 / .NET 10 WPF / 설치형 Inno Setup / Sunrise Group Authenticode**
- 범위: 코드 구조·회귀, CMD 명령 파싱·전달, dotnet 게시, 서명, Inno 구성, 패키징, 설치/제거/업그레이드 설계, 사용자 데이터 보존.
- 증거 범위: **정적 코드·Python 계약검증**. Linux 환경이므로 Windows 실빌드·Inno 컴파일·실서명·설치/제거/업그레이드 검증은 불가.

## 1. 최종 감사 의견

**SOURCE / STATIC CONTRACT PASS**. R17_R01에서 확인된 실질적인 설치형 빌드 위험을 수정한 **R18 소스 수정본**임.
**WINDOWS BUILD / SIGN / INSTALL RELEASE APPROVAL = PENDING**. 본문에서 검증 가능한 동작을 실제 성공으로 표현하지 않음.

### 검증된 구조

`BUILD_ON_WINDOWS_COMPANY.cmd` → `BUILD_RELEASE_INSTALLER.cmd` → `BUILD_RELEASE_ONEDIR.cmd --company --no-pause --installer-payload` → .NET 10 WPF self-contained win-x64 게시 → Group Authenticode EXE/DLL 서명 → Inno Setup(ISCC) 컴파일·Setup/Uninstaller 서명 → Setup 검증·SHA256 → dist 커밋.

**설치형 전용 모드에서는 tar/ZIP 패키징을 수행하지 않는다.** 독립형 OneDir 수동 빌드 명령은 보존됨.

## 2. 발견사항과 시정내역

| 등급 | R17_R01 실제 문제/위험 | R18 시정 또는 판단 | 관련 파일 |
|---|---|---|---|
| P1 | 설치형인데 중간 OneDir ZIP을 의무 생성하여 tar.exe 오류, ZIP 검사 실패가 Setup 빌드를 차단함 | `--installer-payload` 전용 분기. 프로그램 게시·서명·검증 후 서명된 app 폴더만 Inno에 전달. OneDir 수동 배포는 그대로 유지 | `BUILD_RELEASE_ONEDIR.cmd`, `BUILD_RELEASE_INSTALLER.cmd` |
| P2 | 설치 프로그램 진입 전 창 제목 문자열 검색. 다른 프로그램의 동일 제목, UI 현지화 등에 취약 | 중복 `FindWindowByWindowName` 제거. 실제 앱과 동일한 AppMutex를 Inno가 확인 | `installer/SunriseFileManager.iss` |
| P2 | RELEASE_VERSION.txt를 CMD 변수에 무검증 대입. 잘못된 버전이 빌드 경로나 ISCC를 망가뜨릴 수 있음 | `findstr /R /X` 로 숫자 3단 버전만 읽음 | `BUILD_RELEASE_INSTALLER.cmd` |
| P2 | 설치형인데 Onedir 패키지 산출물·라이선스 복사를 매번 중복 수행 | 설치형 전용 단계에서 중간 배포 제거. 라이선스는 ISS `[Files]`가 직접 포함 | BUILD + ISS |
| 검증 대기 | 자체서명 인증서의 Windows 신뢰/SmartScreen/V3 평판은 다른 문제 | 서명 정보가 있어도 사용자 PC에서 신뢰되지 않을 수 있으므로 Release Gate로 기록 | SignTool, 인증서, 보안 정책 |
| 검증 대기 | 업데이트/재설치 시 제거되지 않은 과거 앱 폴더 DLL과 파일 잠금 등 | 디스크 덮어쓰기·설치·제거 실기 시험 필요. 사용자 데이터 전체 삭제용 wildcard 미사용 | ISS, Windows OS |

## 3. 모듈 경계 및 응용 코드 감사

- C# 파일 57개, XAML 파일 13개; Controls, Features, Services, Models, App 등 그룹 구조 유지.
- R17_R01 기존 파일 **211개 전부 보존**. 의도적 수정 파일 4개 이외의 변경 없음.
- WPF XAML 13개가 XML 구조 파서에서 통과하고 XAML 이벤트 226개에 대응되는 소스 메서드 참조가 확인됨. 이는 **실제 WPF 컴파일 보증이 아님**.
- 사용자 세션은 `%LOCALAPPDATA%\Sunrise\FileManager\session.json`에 쓰며 설치 경로와 분리. 설치형 [Files]에는 소스·문서 Fixture 미포함. `[UninstallDelete]` 없음.
- 회사 공식 빌드에는 테스트 PDF/HWPX/LogicTests 실행이 없음. 이 감사에서도 업무 문서 읽기·테스트를 실행하지 않음.
- WPF C# 구조의 MainWindow/ExplorerPane 책임 집중은 R16 후속 개선 사항이며 이번 빌드 감사의 수정을 위해 기능 코드를 무리하게 재구성하지 않음.

## 4. 빌드·서명·설치 단계별 이론적 검토

| 단계 | 파일·명령 | 정적 판단 | Windows 실기 |
|---|---|---|---|
| 진입점 | `BUILD_ON_WINDOWS_COMPANY.cmd` → 설치 빌드 | 설치형 호출·오류코드 보존 | PENDING |
| 도구 감지 | dotnet SDK 10, ISCC 6/7, Windows SDK signtool | 탐색 루틴·실패 지점 있음 | 설치 경로별 PENDING |
| 소스 게시 | `dotnet publish ... --runtime win-x64 --self-contained true -p:PublishSingleFile=false` | WPF 일관된 플랫폼 설정 | PENDING |
| 인증서 | CurrentUser/My, `CN=Sunrise Group`, Code Signing EKU, RSA>=3072 | 키 미내보내기·인증서 재사용 경로 유지 | PENDING |
| 그룹서명 | `signtool sign /fd SHA256 /tr <timestamp> /td SHA256` | EXE/DLL 서명 및 체크 | PENDING |
| Inno 컴파일 | `ISCC /SSunriseGroup=...` + .iss | Inno 공식 SignTool 문법과 정합 | PENDING |
| Setup/Uninstaller | `SignTool=SunriseGroup`, `SignedUninstaller=yes` | Inno 공식 지원 방식 | PENDING |
| 설치 경로 | `PrivilegesRequired=lowest`, localappdata Programs | 계정별 비관리자 설치 | PENDING |
| 업그레이드 | 고정 AppId + UsePreviousAppDir | 기존 설치 인식의 필요조건 | PENDING |
| 파일 사용 중 설치 | AppMutex=Local\SunriseFileManagerCEF | 앱 생성 mutex와 대소문자 일치 | PENDING |
| 설치 제거 | Windows 제거 등록, 소스 정의 외의 사용자 데이터 유지 | 위험한 전체 폴더 삭제 지시 없음 | PENDING |
| 결과 커밋 | staging→signed setup→SHA256→final, 실패 복원 | 명령상 이전 Setup 백업·복원 있음 | PENDING |

## 5. Windows Release Gate (실제로 해야 함)

1. 새 디렉터리에 R18 ZIP 압축해제(압축 내부 소스/빌드 경로는 변경하지 않음).
2. Windows x64에서 `.NET 10 SDK`, `SignTool`, `Inno Setup ISCC` 설치·접근 확인.
3. 인증서가 있으면 `Cert:\CurrentUser\My`에서 `CN=Sunrise Group` 유효 개인키와 SHA256 Code Signing EKU 확인. `SUNRISE_ALLOW_CERT_CREATE=1`은 승인된 내부 테스트 환경에서만 사용.
4. `BUILD_ON_WINDOWS_COMPANY.cmd` 더블클릭 또는 `cmd /c BUILD_RELEASE_INSTALLER.cmd --company --no-pause` 실행. **실패하면 콘솔 로그와 `build-logs/R15_INSTALLER_BUILD_RESULT.txt` 확보.**
5. `dist/Sunrise_File_Manager_1.0.0_Setup.exe` 존재 확인, 서명자·타임스탬프·해시 확인. 사내 보안 검사 통과 전 공식 배포하지 않음.
6. **Windows 시험용 가상환경/사용자 계정**에서 설치 → 실행 → 시작메뉴/바탕화면 바로가기 → 다른 폴더/테마 저장 → 재실행 복원 → 앱 실행 중 업그레이드 차단 → 종료 후 업그레이드 → 제거 → `%LOCALAPPDATA%` 사용자 데이터 보존을 확인.
7. `100%/125%/150% DPI`에서 메뉴 대비, 창 표시, 설치 UI 확인. 회사 보안정책상 문서 본문 열기·테스트가 금지된 PC에서는 이를 수행하지 않음.

## 6. 남아 있는 제한과 리스크

- **PENDING**: Windows에서 실제 `ISCC.exe` 컴파일과 Authenticode 서명은 실행해보지 않았음.
- **PENDING**: 기존 설치 업그레이드 시 예전 버전에서 제거된 DLL이 남는 문제는 Windows 설치 테스트 없이는 확정 불가. 무조건 `{app}` 전체 삭제로 해결하면 사용자 데이터 손실 위험이 있으므로 금지.
- **PENDING**: 잘못된 사용자 PC 인증서 신뢰 상태, 타임스탬프 네트워크/프록시, AhnLab/V3 정책 영향.
- **PENDING**: 현재 구현은 새 Setup을 사용자가 승인해 설치하는 방식. 온라인 업데이트 서버/자동 업데이트는 구현됐다고 주장하지 않음.
- **PENDING**: Windows 실기 검증 없이 "최종 배포 승인" 등급으로 올리지 않음.

## 7. 검증 요약

- R18 설치·그룹서명 계약검사: **58/58 PASS**
- 기존 소스 회귀검사: **59/59 PASS**
- CMD 패키징 계약검사: **15/15 PASS**
- XAML XML: **13/13 PASS**, 코드 이벤트 이름 참조 **226/226 존재**
- R17_R01 원본 누락: **0건**, 의도적 수정 4개 외 기존 파일 변경: **0건**
- Windows WPF 컴파일/실행·SignTool·Inno 설치/제거/업그레이드: **PENDING**

검증 결과는 실기 빌드 성공을 보장하지 않음. 실제 Windows 출력 로그를 확인해 필요한 수정을 마지막으로 검증해야 함.
