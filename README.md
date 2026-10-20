# Sunrise File Manager 1.0.0 (R19 R15 · Sunrise Control 중앙화)

Sunrise Group의 Windows 10/11 x64용 .NET 10 WPF 파일 관리 프로그램입니다. 분할 탐색기, 탭, 폴더 검색, PDF/HWPX/DOCX·이미지 미리보기, 공유 메모 `#memo`, 선택형 Ollama 기능을 포함합니다.

## 현행 빌드 실행 파일 — 2개

1. **`BUILD_ON_WINDOWS_COMPANY.cmd`**: 공식 서명 설치형 Setup EXE를 빌드합니다. Windows x64, .NET 10 SDK, Inno Setup, Windows SDK SignTool, Windows 기본 certutil/certreq가 필요합니다. 회사 정책에서는 문서 fixture·업무파일 로직 테스트를 실행하지 않습니다.
2. **`BUILD_DEV_EXE.cmd`**: .NET 10 SDK를 이용한 **미서명 개발 확인용 EXE**를 게시합니다. 회사 정책에 따른 사용 여부를 판단해야 하며, 공식 배포물로 사용하지 않습니다.

나머지 `BUILD_RELEASE_INSTALLER.cmd`, `BUILD_RELEASE_ONEDIR.cmd`, `CHECK_INSTALLER_BUILD_ENV.cmd`, `build-tools/*.cmd`는 공식 빌드가 내부에서 호출하는 보조 스크립트입니다. 수동 인증서 지문 선택 도구나 별도 인증서 사전검사 실행은 필요하지 않습니다.

### R13: 진행 화면과 단계별 로그 보완

`BUILD_ON_WINDOWS_COMPANY.cmd`를 실행하면 먼저 환경을 확인합니다. 이후 **WPF 게시 → Sunrise Group 서명 인증서 확인·최초 생성 → EXE/DLL 서명·검증 → Inno Setup 설치파일·Uninstaller 서명 → 검증·결과 저장** 순서로 작업합니다. 각 단계는 CMD 화면에 출력됩니다. 하위 OneDir 빌드 출력 전체를 로그로 숨기는 종전 R12 동작을 제거했습니다.

빌드창이 멈춘 것처럼 보일 때는 **같은 소스 폴더의 `build-logs/R13_CURRENT_STAGE.txt`**를 먼저 확인하십시오. `STAGE=WPF_PUBLISH`라면 컴파일/패키지 복원 과정이고, `SIGNING_PREFLIGHT`라면 인증서 확인, `AUTHENTICODE_SIGN`이라면 서명 및 타임스탬프 통신, `INSTALLER_COMPILE`이라면 Inno Setup 컴파일 과정입니다. 자세한 오류는 `R14_BUILD_RESULT.txt`, `R15_INSTALLER_BUILD_RESULT.txt`와 단계별 `R14_*.log`, `R15_*.log`에 남습니다.

**공식 설치파일 기대 위치:** `dist/Sunrise_File_Manager_1.0.0_Setup.exe` (실제 Windows 빌드 성공 시에만 존재).

### Capture 참조 범위

GitHub `Peter-msk/Sunrise_capture`의 최신 릴리스 `v4.2.0`(2026-09-30)은 `SunriseCapture_Setup.exe`만 배포하며 해당 GitHub 태그의 소스 ZIP에는 `README.md`, `latest.json`, `notice.txt`만 들어 있습니다. 따라서 **Capture의 실제 `build_pipeline.py`를 문자 그대로 이식한 것이 아닙니다.** 검증된 v4.2.0 인수인계서에 기록된 단계별 CMD, 먼저 패키징 후 서명, `certreq/certutil`, SignTool, Inno의 운영 설계만 참조했습니다. 자세한 근거는 `docs/R19_R13_VISIBLE_BUILD_AND_CAPTURE_COMPARISON.md`입니다.

### 보안 및 검증 한계

기존에 고정된 인증서는 계속 재사용하고, 다른 Sunrise 제품의 동명 CN 인증서는 자동 선택하지 않습니다. 서명자의 개인키를 내보내지 않으며 인증서 저장소 삭제, Trusted Root 등록, 백신 예외 설정을 하지 않습니다. 고정된 인증서가 유실되거나 프로젝트의 공식 서명 신원이 불명확하면 안전하게 실패합니다. 자체서명 인증서는 조직 신원 보증이나 SmartScreen/V3 허용을 의미하지 않습니다.

**R13의 소스·회귀 검증과 Windows 실기 서명/설치 검증은 별도입니다.** 실제 Windows 컴파일/서명/설치/제거는 대상 PC에서 검증해야 하며, 실행하지 않은 단계를 PASS로 간주하지 않습니다. 과거 R07~R12의 사용 안내는 역사적 자료일 뿐 현행 명령을 대체하지 않습니다.

## 핵심 기능

- 1~4분할 파일 탐색, 탭, 파일검색·분류 및 복사·이동·이름변경·휴지통 삭제·압축
- PDF, HWPX, DOCX, 이미지 미리보기 및 다중 패널
- 파일별 `#memo`의 공유 메모, 작성자 필드, 무결성 변경 경고
- 사용자 선택형 로컬 DPAPI 메모 복원 이력
- 선택형 로컬 Ollama, 프로그램 세션 복원

## 기타

- 프로젝트: `source/Sunrise.FileManager/Sunrise.FileManager.csproj`
- 설치 정의: `installer/SunriseFileManager.iss`
- 버전 단일 원천: `installer/RELEASE_VERSION.txt`
- 기존 기능·메모·Ollama·UI 소스 변경 없음
- 오프라인 정적 감사, 인수인계 이력과 보안정책은 `docs/` 참조

## R19 R14 — UI 팝업·미리보기 개선

- 폴더 목록 위의 빠른 명령 **표시 행 제거**(기존 명령은 메뉴·단축키로 제공), 열 폭/긴 이름 표시와 스마트 메모 패널 배치 개선.
- `AI 파일 정리`는 F8 또는 상단 버튼으로 여는 독립 창이며 기존 검사·승인 절차와 파일 작업 로직은 유지됨.
- 미리보기는 우측 패널의 확대·축소/너비 맞춤/독립 창 열기, 독립 창의 100%/너비·페이지 맞춤 및 Ctrl+휠 등 키보드·마우스 기본 조작을 지원하도록 소스 수정.
- 상세 변경 및 PC 검증 항목: `docs/R19_R14_UI_POPUP_PREVIEW_HANDOVER.md`.
- 현재 **소스 검증 완료, Windows 실제 WPF 컴파일·UI 실기 검증 및 공식 설치파일 생성은 별도 확인 필요**.

## R19 R15 — 공식 1.0.0 · Sunrise Control · 업데이트/공지

- 제품의 공식 버전 기준은 `1.0.0`으로 유지합니다. 이 시점의 상태는 **공식 소스 기준 확정**, Windows 실제 서명 Setup 릴리스 게이트는 **PENDING**입니다.
- 중앙 Control ID: `sunrise_file_manager`, 공개 저장소: `Peter-msk/Sunrise_Control/control.txt` schema v1.
- 실행 시작 시 중앙 `operation=allow/maintenance/block` 및 `update=allow/block/required`, `minimum_version`을 반영합니다. HTTPS 조회 실패 시 유효 TTL 캐시 후 v1 fail-open fallback을 사용합니다.
- `설정 · 기능 > 공지사항 · 버전 정보`와 `업데이트 확인`을 제공합니다.
- File Manager 저장소는 비공개이므로 공지/버전의 런타임 공개 엔드포인트는 `Sunrise_Control/products/sunrise_file_manager/latest.json`, `notice.txt`를 사용합니다. 제품 원본도 이 저장소 루트에 보존합니다.
- 새 Setup 파일이 **실제로** 서명·검증·배포되어 `download_url`, SHA256, 크기가 제공된 경우에만 사용자 승인 후 다운로드·해시 검증·실행할 수 있습니다. 검증된 설치파일이 아직 없으므로 R15에서는 다운로드 기능을 비활성화합니다.
- Control v2 서명 정책/리스 설계는 미구현이며, v1 정책은 인증된 원격 라이선스 서버가 아닙니다.
- 구현 및 검증 게이트: `docs/R19_R15_SUNRISE_CONTROL_RELEASE_HANDOVER.md`.
