# Sunrise File Manager R16 — 코드 분기·모듈화·그룹화 및 설치형 빌드 전면 재감사

- **기준본**: `Sunrise_File_Manager_R15_INSTALLER_FULL_SOURCE.zip` (기능·앱 버전 1.0.0 유지)
- **감사 범위**: C# 57개 파일, WPF XAML 13개 파일, .NET 프로젝트 3개, CMD 빌드 진입점, R14 Authenticode, R15 Inno Setup, 기존 회귀·계약검사, 패키지 구성 및 고지 자료
- **공식 배포 계약**: Windows x64 설치형(Setup EXE). OneDir은 내부 게시 payload, OneFile/구형 Portable은 비공식 레거시 경로
- **회사 보안 계약**: LogicTests/문서 Fixture/본문 읽기/GUI 캡처 **미실행**. 내부 소스의 테스트는 별도의 허용 환경에서만 검사
- **감사 자격**: SOURCE/STATIC PASS. Windows WPF 컴파일·SignTool 서명·Inno Setup 생성·실제 설치/제거는 **PENDING**

## 1. 핵심 판정

**최종 설치형 배포 방향은 적절하나, R15 원본은 검증 계약과 설치 패키지 고지 구성의 보완이 필요했음.**

R15 원본 `python -m unittest discover -s tests -p 'test_*.py'` : **46개 중 39 PASS / 7 FAIL**. 실패는 과거 OneDir 가정을 검사하는 stale test와 CMD LF/CRLF 불일치였음. 보완 후 **49개 전체 PASS**, 패키징 정적검사 **15항목 PASS**, UI/XAML 이벤트 구조 정적검사 **156항목 PASS**. 이 수치는 실제 C#·WPF 빌드 성공을 의미하지 않음.

### 감사 발견 사항과 처분

| ID | 중요도 | R15 원본 위치·현상 | R16 조치 또는 유보 |
|---|---|---|---|
| A-01 | **높음** | `installer/SunriseFileManager.iss` `[Files]`에 앱 파일·주 LICENSE만 선언. R14 OneDir의 `licenses/`는 `app` 바깥에 있어 설치파일에서 PdfPig/.NET 고지가 빠질 가능성 | 설치용 `[Files]`에 `source/licenses/*` 명시하여 고지자료 포함. 신규 계약검사 추가 |
| A-02 | **높음** | `tests/test_company_build_contract.py`, R07/R09/R13 테스트가 R15 회사 빌드의 공식 진입점을 R14 OneDir로 오판함 | 현재 계약인 `회사 진입점 → R15 설치형 → R14 내부 signed payload` 체인 검사로 수정. 과거 기능·스크립트 존재 검사는 유지 |
| A-03 | **중간** | `installer/SunriseFileManager.iss` 앱 실행 감지가 윈도우 제목에 의존 (`FindWindowByWindowName`) | 실제 `App.xaml.cs`의 `Local\SunriseFileManagerCEF` Mutex를 Inno `[Setup] AppMutex`에 추가. 기존 확인창을 보조 방어로 유지 |
| A-04 | **중간** | `source/tests/LogicTests/LogicTests.csproj` PDF Fixture 복사가 파일 부재 시 MSB3030 재발 가능 | 존재 시에만 출력 복사. PDF 없는 개발 환경에서 PDF 전용 테스트만 명시적 SKIPPED. 회사 빌드는 LogicTests 전체 생략 유지 |
| A-05 | **중간** | `BUILD_ON_WINDOWS.cmd`, `RUN_FILE_MANAGER.cmd`의 CRLF 표준 불일치 | ASCII CRLF로 정규화 |
| A-06 | **중간·잔여** | R15 Setup 빌드가 R14 OneDir 폴더 + 별도 ZIP을 만든 다음 Setup을 생성하여 생성시간·중간 파일 증가 | **미변경**. R14 검증 흐름과 롤백 경로를 유지. 다음 최적화에서 signed payload 전용 모드를 최소 diff로 분리하는 방안 권고 |
| A-07 | **중간·잔여** | 실제 서명자 동일성 외에 자체서명 인증서의 OS 신뢰/SmartScreen 평판까지 보장할 수 없음 | **미변경**. 기존 R14 WinTrust 상태 기록 유지. 공인 신뢰·보안제품 오진은 Windows 릴리즈 검증으로 별도 판정 |
| A-08 | **중간·잔여** | Windows 설치/업그레이드/제거, 인스톨러 내부 Uninstaller 서명, 실제 DPI/UI 동작 실기 미검증 | **PENDING**. 소스 정적 검사로 대체하지 않음 |
| A-09 | **낮음** | `RELEASE_STATUS.json`, `source/README.md`에 현재 배포형식과 다른 과거 설명 존재 | 현재 공식 설치형 상태로 갱신. 역사 문서·레거시 도구는 삭제하지 않음 |
| A-10 | **유지보수 관찰** | `MainWindow.xaml.cs` 약 617행, `ExplorerPane.xaml.cs` 약 811행. 부분 클래스 분리가 진행됐으나 화면·상태 조정 집중 | 위험 기록만. 근거 없는 인터페이스·플러그인·프로젝트 분할을 도입하지 않음 |

## 2. 구조 및 의존 방향

```
App / MainWindow (WPF 진입·레이아웃·상태)
    ├── Controls (ExplorerPane, FolderTree, FileInspector, Preview, AI dock)
    ├── Features/Workspace·Explorer·Navigation·Inspector·OnboardAi
    │    └── partial C# 조각: View/Filtering·Clipboard·FileOperations·AI/설정
    ├── Models (FileEntry, ExplorerTab)
    └── Services (파일 탐색·전송·메모·세션·미리보기·AI·복구)

BUILD_ON_WINDOWS[ _COMPANY].cmd
    → BUILD_RELEASE_INSTALLER.cmd
        → BUILD_RELEASE_ONEDIR.cmd (R14 내부 빌드/서명)
            → .NET 10 win-x64 self-contained publish
            → Sunrise 자체 EXE·DLL 서명·검증·해시
        → Inno Setup ISCC.exe (기존 AppId, user-scope 설치)
        → Setup·Uninstaller 그룹서명, Setup 검증·서명 후 SHA-256
        → dist/Sunrise_File_Manager_1.0.0_Setup.exe
```

**모듈화 평가:** 현재 소스는 별도 Plugin Assembly 방식이 아닌 **기능별 폴더 + partial 클래스 + 서비스 분리**임. OneDir/OneFile/설치형 여부는 이 코드의 모듈화 가치를 없애지 않음. `Services`에 31개 C# 파일(약 4,131행)이 모여 있으므로 다음 실제 기능 변경 시 FileSystem / AI / Preview / Session 등 하위 그룹으로 옮길 여지는 있으나, **파일 이동 자체를 이번 공식 릴리즈 변경에 섞지 않음**. WPF/XAML 네임스페이스와 테스트 링크 경로 변경은 컴파일·실기 검증 부담을 늘리므로 분리된 유지보수 단계에서 시행해야 함.

**중복/분기 평가:** 회사용 R15 공식 경로와 구형 `BUILD_DEV_FULL_SOURCE.cmd`, `BUILD_RELEASE_ONEFILE.cmd`, `BUILD_RELEASE_ONEDIR.cmd`, `RUN_FILE_MANAGER.cmd`가 함께 존재. 사용자는 `BUILD_ON_WINDOWS_COMPANY.cmd`만 사용해야 함. 레거시 소스와 스크립트는 삭제하지 않았으며 공식 진입점과 역사 경로를 계약·가이드에서 분리함. 불필요한 변경 확산을 방지하기 위함임.

## 3. 빌드 전면 감사 — 단계별 증거와 잔여 Gate

| 단계 | 실제 경로·검증 | 감사 판정 |
|---|---|---|
| B01 소스/회사 정책 | `COMPANY_SOURCE_ONLY.flag`, 설치형 진입 CMD, 회사 문서 검사 생략 | SOURCE PASS |
| B02 도구 탐색 | .NET 10, Windows SDK SignTool, Inno Setup ISCC, 인증서 | 소스 경로 검사 PASS / 사용자 PC 실기 PENDING |
| B03 게시 | `net10.0-windows10.0.19041.0`, win-x64, self-contained, trim 꺼짐 | 소스 계약 PASS / 컴파일 PENDING |
| B04 그룹서명 | R14, 기존 `CN=Sunrise Group`, EXE + 자체 DLL, SHA256, 기록 | 순서 검사 PASS / 실제 서명 PENDING |
| B05 R14 내부 payload | app 실행파일·필수 런타임, 추가 OneDir ZIP 생성 | 정적 계약 PASS / 생성시간 최적화 과제 |
| B06 Inno Setup | 고정 AppId, user-scope, 바로가기, 제거, 업그레이드 | 소스 계약 PASS / 설치 실기 PENDING |
| B07 실행중 보호 | 기존 Win32 창 확인 + `AppMutex=Local\SunriseFileManagerCEF` | 코드 일치 PASS / 설치중 경합 실기 PENDING |
| B08 고지/사용자 데이터 | PdfPig/.NET 라이선스 포함, `%LOCALAPPDATA%` 설정·`#memo`는 설치파일 대상 아님 | 정적 계약 PASS / 파일 집합 실기 PENDING |
| B09 Setup 서명/검증 | ISCC SignTool, SignedUninstaller, 설치파일 embedded signer 비교, 서명 후 SHA256 | 소스 계약 PASS / Setup·unins 실제 서명 PENDING |
| B10 롤백/업데이트 | 최종 Setup 커밋 전 기존 파일 백업, 실패 시 복구. 새 버전 Setup은 AppId 유지 | 소스 순서 PASS / 실제 업그레이드 PENDING |
| B11 온라인 업데이트 | 공식 URL, manifest, HTTPS 조회/다운로드가 현재 미설정 | **미구현 (기존 의도 유지)** |
| B12 릴리즈 승인 | 서명된 Setup 시작·설치·재실행·제거·설정유지·회사 보안솔루션 | **PENDING**. Static PASS를 릴리즈 승인으로 사용 금지 |

## 4. 빌드 성공 조건과 권고

1. 빌드 PC에서 `BUILD_ON_WINDOWS_COMPANY.cmd`를 실행하여 실제 Setup EXE 생성, 서명, Windows 설치형 등록을 확인해야 함.
2. 실제 앱 종료 상태에서 업그레이드, 실행 중 업그레이드 차단, 사용자 설정 유지, 제거 중 폴더 외부 `#memo` 보존을 검사해야 함.
3. 자체서명 인증서일 경우 **서명 존재**와 **OS 신뢰·AhnLab 평판**을 구분해 별도 확인해야 함.
4. 중간 R14 OneDir ZIP 제거 최적화는 실제 설치형 첫 빌드가 통과한 뒤 적용하고, 정상 동작을 보존할 것.
5. 배포물에는 `source/`, 테스트 PDF/HWPX, 개인키/PFX/토큰을 포함하지 말 것.
6. 업데이트 서버 관련 사항은 URL·manifest 계약 확정 전 임의로 구현하지 말 것.

## 5. 실제 수행한 검증

- Python `unittest` 계약·회귀검사 **49/49 PASS** (회사 문서 열람 없이 소스 텍스트만 검사)
- CMD 패키징 계약 검사 **15/15 PASS**
- XAML·독립 열 및 이벤트 구조 정적 검사 **156항목 PASS**
- R15 기준 파일 변형 대조 및 ZIP SHA-256 무결성 검사는 별도 매니페스트로 기록
- Windows .NET 10, WPF 실제 컴파일, SignTool, ISCC, Setup 설치·업그레이드·제거: **미수행/PENDING** (현재 작업 호스트에 해당 도구가 없음)

**감사 결론:** 모듈 구성은 대체로 재사용 가능한 수준이며 전면 재작성보다 **배포 계약 정합성 보수 + 최소 위험 수리**가 적절. 현재 수정본은 *정적 감사 반영 전체 소스*이며 공식 Windows 설치형 릴리즈 승인을 뜻하지 않음.
