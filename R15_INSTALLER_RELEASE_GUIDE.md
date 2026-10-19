# Sunrise File Manager — R15 설치형 전환 및 업그레이드 기준

## 기준본과 결과물

- 소스 기준: R14 전체 소스 및 Sunrise Group Authenticode(`CN=Sunrise Group`) 빌드 절차.
- 공식 사용자 배포 형식: **Setup EXE (설치형)**. R14 OneDir 산출물은 최종 설치파일 생성 전 내부 payload이며, 사용자에게 OneDir ZIP을 공식 배포하지 않음.
- 최초 설치형 제품 버전: `installer/RELEASE_VERSION.txt`의 `1.0.0`. R15는 **개발 개정번호**이며 사용자 제품 버전이 아님. 이후 업데이트는 1.0.1 등 버전 증가가 필요함.
- 설치/업데이트/제거 식별자(AppId): Inno Setup `.iss`의 고정 GUID를 유지. 절대 변경하지 않음.
- 설치방식: Inno Setup 6.x, per-user (관리자 권한 기본 요구 없음), Windows 10 x64 빌드 19041 이상.
- 설치 위치: `%LOCALAPPDATA%\Programs\Sunrise File Manager` (사용자가 설치 과정에서 선택 가능).
- 실행 진입점: 시작 메뉴 `Sunrise File Manager`, 선택한 경우 바탕화면 바로가기. 실제 실행파일과 .NET 런타임/DLL은 설치 폴더에 배치함.
- 제거: Windows 설정의 설치된 앱(앱 및 기능)에서 제거 가능.
- 서명: 앱 EXE·앱 DLL은 R14 빌더의 Sunrise Group Authenticode 재사용; Inno Setup 내부 SignTool로 **Setup 및 Uninstaller** 서명. 서명 후 SHA-256 산출.

## 회사 PC 빌드

### 준비 조건

1. Windows x64, .NET 10 SDK (기존 PC에 이미 설치된 버전 사용).
2. Windows SDK `signtool.exe`, `CurrentUser\\My` 저장소의 기존 `CN=Sunrise Group` 코드서명 인증서(개인키 포함).
3. Inno Setup 6의 `ISCC.exe` (별도 설치 필요; 사내 정책에 따른 승인 후 설치). 자동 설치하지 않음.
4. 빌드 도구의 타임스탬프 서버 접근 또는 승인된 내부 타임스탬프 서버(`SUNRISE_TIMESTAMP_URL`). 통신 실패 시 **공식 빌드 중단**.

`BUILD_ON_WINDOWS_COMPANY.cmd`를 실행하면 R14 서명된 WPF OneDir payload를 먼저 빌드하고, Inno Setup 컴파일 과정에서 Setup 및 Uninstaller에 기존 그룹 인증서를 적용한 후 검증합니다.

- 성공 산출물: `dist\Sunrise_File_Manager_1.0.0_Setup.exe`
- 빌드 결과: `build-logs\R15_INSTALLER_BUILD_RESULT.txt`
- 설치파일 서명·해시: `build-logs\R15_verify_setup.log`, `R15_SIGNED_SETUP_SHA256.txt`
- 회사 정책: LogicTests·문서 읽기/미리보기/Fixture 검사·UI 캡처 **미실행**. 프로그램 설치·빌드 중 SecureDisk에 저장된 업무 문서를 열지 않음.
- 기본 빌드에서 이전 OneFile/OneDir ZIP 배포를 선택하도록 묻지 않음. 구버전 빌드 스크립트는 코드 호환/회귀 목적으로 유지할 뿐 공식 배포 아티팩트가 아님.

### 설치/업데이트/제거

1. `Sunrise_File_Manager_1.0.0_Setup.exe` 더블클릭 → 설치 위치 확인 → 설치 진행.
2. 시작 메뉴의 `Sunrise File Manager`에서 실행. 바탕화면 바로가기는 설치 옵션.
3. 후속 버전 설치 시 **기존과 같은 AppId, 같은 per-user 설치 영역**을 유지하고 버전 값을 증가시킨 새로운 Setup을 생성. 사용자가 프로그램을 종료한 상태에서 Setup 실행.
4. Windows 설정 → 앱 → 설치된 앱 → Sunrise File Manager → 제거.
5. `%LOCALAPPDATA%\Sunrise\FileManager\session.json` 등 사용자 설정은 **업그레이드/제거 시 삭제하지 않음**. 업무 폴더의 `#memo` 메모도 건드리지 않음. 설정 데이터 삭제는 사용자가 별도로 결정.

**중요:** 구형 Portable 폴더는 자동 삭제하거나 덮어쓰지 않음. 설치형이 정상 동작한 후 사용자가 이전 Portable 폴더 보관 여부를 결정해야 함.

## 업데이트 기능의 범위

- 구현됨: 같은 AppId의 새 설치파일로 재설치·덮어쓰기(업그레이드), 기존 설치 폴더 재사용, 사용자 설정 보존, 앱 제거 등록, 릴리즈/서명/해시 생성.
- **미구현**: 배포 서버에서 `latest.json` 조회, HTTPS 업데이트 다운로드, 자동 새 버전 알림. 공식 URL·manifest 계약이 아직 지정되지 않아 임의 생성하지 않음.
- 향후 온라인 업데이트: 사용자 승인 → HTTPS로 **Setup EXE** 다운로드 → SHA-256 검증 → 앱 종료 → 설치프로그램 실행. 다운로드 및 서명 검증 결과 기록 후 완료 확인.
- 업그레이드 설치 중 프로그램이 사용 중이면 먼저 종료하도록 안내. 별도 강제 종료 도구나 사용자 문서 접근 작업은 추가하지 않음.

## 검증 제한

작성 환경에서 Windows SDK/.NET/WPF/Inno Setup 실기 빌드 실행 불가. 현재 소스의 정적·계약 검사와 ZIP 검증만 수행. Windows 실제 설치·업데이트·제거, 서명 확인, 설치된 앱 등록, 사용자 설정 유지 테스트는 PC에서 수행 전임.
