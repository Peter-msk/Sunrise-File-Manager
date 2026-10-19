# Sunrise File Manager 1.0.0 R17 — 작업인수인계서

- 기준일: 2026-10-20
- 기준 코드: `Sunrise_File_Manager_1.0.0_R17.zip`
- 제품: Sunrise File Manager / Sunrise Group / Windows x64 / .NET 10 WPF
- 공식 배포: **설치형(Inno Setup으로 생성하는 Setup EXE)**; OneDir은 설치파일용 내부 게시 결과. OneFile/포터블은 레거시
- 현재 인수 자격: **SOURCE·STATIC PASS / WINDOWS RELEASE PENDING**

## 1. 업무 현황 및 이번 이관 범위

Drive 폴더 `4. Sunrise File Manager`의 R12 감사 ZIP과 로컬 R16 ZIP을 비교하였다. Drive 177개, R16 205개, 바이트 동일 148개, 변경 29개, R16 전용 28개, Drive 전용 0개로 확인되었다. R16이 설치형 및 Sunrise Group 서명 빌드 등 현행 배포 계약을 갖고 있으므로 R16을 주 기준본으로 선택하였다. R12에서 빠진 기능 중 재활용 가능한 breadcrumb 가로 휠 스크롤 및 반응형 화면 폭 처리만 선별 복원하였다. 비교 결과와 SHA256 증거는 `docs/R17_DRIVE_COMPARE.md` 및 `.json`을 참조한다.

## 2. 유지해야 할 핵심 기능

Windows 탐색기형 멀티 탭, 1·2·3·4분할, 분할창별 독립 폴더 트리 켜기/끄기, 파일 복사·이동·삭제·압축, 파일별 `#memo`, 독립 우측 미리보기, 파일 목록 정렬·필터·열 너비, 라이트/다크 테마, 마지막 폴더·탭·테마·패널·창 위치/크기 로컬 세션 저장, Ollama 로컬 연동(선택 기능)을 보존한다. 회사 SecureDisk 문서 접근·권한을 우회하지 않으며 문서 내용이나 Fixture를 회사 빌드에서 시험하지 않는다.

## 3. 디렉터리 책임 구분

- `source/Sunrise.FileManager/`: 제품 WPF 진입점, Controls, Features, Models, Services
- `source/build-tools/Sunrise.Signing/`: 기존 Sunrise Group 인증서 선택·Authenticode 서명 및 검증 코드
- `installer/SunriseFileManager.iss`: 설치형 Inno Setup 정의 (고정 AppId, 사용자별 설치, 바탕화면 선택, 실행 중 앱 검사)
- `BUILD_ON_WINDOWS_COMPANY.cmd`: 공식 회사 빌드 진입점
- `BUILD_RELEASE_INSTALLER.cmd`: WPF 게시 → 서명된 OneDir payload → ISCC Setup/Uninstaller 서명 → SHA256
- `BUILD_RELEASE_ONEDIR.cmd`: 설치 내부 구성 및 과거 OneDir 관련 빌드; 공식 최종 배포물 아님
- `tests/`, `source/tests/`: 정적·회귀검증. 회사 빌드에서 LogicTests·문서 테스트 미실행
- `docs/`: R17 Drive 대조·인수인계·개발계획, 이전 각 R단계 이력도 보존

## 4. 빌드 운영 절차

1. 허용된 Windows x64 개발 PC에 .NET 10 SDK, Windows SDK SignTool, Inno Setup ISCC, Sunrise Group 코드서명 인증서(개인키 포함)를 준비한다. 인증서·PFX·비밀번호는 소스 ZIP에 넣지 않는다.
2. 소스를 압축 해제하여 `BUILD_ON_WINDOWS_COMPANY.cmd`를 실행한다. 그 외 레거시 스크립트를 공식 배포 진입점으로 사용하지 않는다.
3. `build-logs/`의 단계별 로그 및 최종 `STATUS=SUCCESS`를 확인한다. 최종 대상은 `dist/Sunrise_File_Manager_1.0.0_Setup.exe`이다.
4. EXE와 Setup/Uninstaller의 그룹서명 상태, Setup 설치/수정/제거, 프로그램 시작 및 중복 실행 방어를 Windows 실기에서 확인한다.
5. 운영 PC에서 설치 전후 사용자 설정 `%LOCALAPPDATA%\Sunrise\FileManager\session.json`, `#memo`, 마지막 폴더·분할·테마 복원이 유지되는지 확인한다. 설치·제거 시 임의 삭제하지 않는다.
6. 설치형 업그레이드 전 실행 중 파일 작업이 없는지 사용자에게 안내하고, 현재 앱이 실행 중이면 설치를 중단한다.

## 5. 검증·보안 상태

- Python 회귀·계약검사 및 XML·XAML 정적 검사를 별도로 수행한다. 수행한 검사 결과를 `docs/R17_VALIDATION.json`에서 확인한다.
- **미검증**: Windows WPF 실제 빌드, SignTool 인증서 선택 및 디지털 서명, Inno Setup 설치파일 생성, 사용자별 설치·업데이트·제거, 100/125/150% DPI 및 실사용 파일 관리.
- 자동 업데이트 URL/manifest/공지 서버는 확정되지 않았으므로 앱이 임의로 내려받거나 실행하지 않게 한다. 사용자 승인형 설치파일 업그레이드가 현재 승인된 방식이다.
- 회사 정책: 소스 코드를 검사하되 보안 문서의 본문을 열람하거나 스캔하지 않는다. 테스트 PDF/HWPX Fixture를 회사 빌드에 포함하지 않는다.

## 6. 알려진 기술부채·진행 우선순위

1. P0: **실제 Windows Build–Sign–Setup–Install–Launch** 폐쇄루프. 실패 로그에서 원인 확인 후 최소 패치.
2. P0: 설치·재설치·업그레이드·제거에서 설정과 외부 메모 파일 무결성 확인.
3. P1: 다크 메뉴, 4분할·스크롤·검색 레이아웃의 DPI 실기 검사.
4. P1: 설치형 빌드 전에 생성되는 중복 R14 OneDir ZIP 제거 여부 검토. 현재 정상 경로의 실기 승인 이전까지 구조 변경 유보.
5. P2: 공식 업데이트 서버, 사용자 승인형 다운로드·SHA256·Setup 실행 설계. 신뢰할 수 있는 주소와 배포 스키마 확정 후 개발.
6. P2: `MainWindow`/`ExplorerPane` 책임 분산과 `Services` 기능별 하위 폴더 그룹화는 별도 변경으로 실시.

## 7. 인수 시 금지 사항

R12로 전체 롤백, OneFile을 공식 배포로 재전환, 인증서를 빌드마다 재발급, PFX/키를 ZIP에 포함, 설치 제거 시 `%LOCALAPPDATA%` 사용자 자료 삭제, 회사 문서에 대한 권한 우회 또는 무단 테스트, HTML 프로토타입 통과를 Windows 실기 PASS로 표기하는 행위는 금지한다.

## 8. Google Drive 관리

기존 Drive R12 ZIP은 역사 기준본으로 유지한다. R17 ZIP과 본 인수인계서 및 개발계획서를 해당 프로젝트 폴더에 **별도 새 파일로 등록**한다. 비교 SHA를 보존하며 문서 표기에 `Windows 릴리즈 대기` 상태를 유지한다.
