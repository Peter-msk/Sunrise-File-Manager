# Sunrise File Manager v1.8.2 R03 — SDK 탐색 보완 및 전달 검증

- 기준: `Sunrise_File_Manager_v1.8.2_R02_FEATURE_THEME_SOURCE.zip` (532개 항목, SHA-256 `c1ffe42c43880bd3c1bf3e4da443465941477bfe69ecb11cb128fd94306c44b7`).
- 제품 버전 유지: 1.8.2. 동일 버전 빌드 보완 전달본 R03.
- 사용자 빌드 로그: Inno Setup/SignTool 발견, 버전 1.8.2 PASS; PAYLOAD_BUILD/PREFLIGHT 실패. 표준 dotnet.exe 호스트 세 번 반복 탐색, 호스트에 노출된 SDK 없음. 사용자는 SDK 설치 완료 상태라고 보고했으므로 실제 설치 위치와 원인은 아직 확정할 수 없다.

## 변경
- `FindDotNet10.cmd`: 명시적 SUNRISE_DOTNET 경로 우선(실행파일 또는 설치 폴더), DOTNET_ROOT_X64/DOTNET_ROOT/DOTNET_INSTALL_DIR, x64 등록 경로(레지스트리 양쪽 보기), 기본 경로와 사용자 경로, PATH를 검사한다. 절대 경로 기준 중복 탐색을 방지하고 후보별 --version 종료코드와 실패한 호스트의 --list-sdks 결과를 누적한다.
- 실제 선택 결과가 정상 종료 및 안정 10.0 SDK 버전일 때만 승인한다. global.json 정책은 그대로 유지한다. SDK 자동 설치, 시스템 설정 변경, SDK 조건 완화, 서명 생략 없음.
- 루트에 SDK_DIAGNOSTICS.cmd(실제 공유 탐색기 사용) 및 SDK_DIAGNOSTICS.ps1(독립 경로 진단) 포함.
- 제품 C#/XAML, 소스·설치 버전, 공식 릴리즈 빌드/서명 경로는 그대로 유지한다.

## 사용
1. 새 ZIP을 별도 폴더에 완전히 압축 해제하고 `SDK_DIAGNOSTICS.cmd`를 실행한다.
2. SDK 선택 성공 시 `BUILD.cmd --release --no-pause` 또는 BUILD.cmd 더블클릭. 이미 확인한 무관한 검사나 설치를 별도로 반복할 필요 없다.
3. 실패 시 `build-logs/R15_dotnet_probe.log` 및 `build-logs/R15_dotnet_candidate.log`를 전달한다. 비표준 위치는 실제 dotnet.exe 경로를 SUNRISE_DOTNET으로 명시한다. 임의의 SDK 다운그레이드 또는 재설치 불필요.

## 검증 결과
- 신규 SDK 탐색 소스 계약 6건 및 현행 버전 계약 6건: 총 12 PASS. 이전 R00 이력 표기 계약의 최초 실패 1건은 역사적 기준본 표기를 명확히 보완하여 재검사하였다.
- 기준 ZIP의 항목을 누락 없이 보존하고 제품 코드와 설치/서명 설정의 바이트 동일성을 확인한다.
- 현재 환경은 Windows cmd.exe/.NET SDK/WPF가 없으므로 실제 SDK 탐색 성공, Windows 빌드, 설치파일 생성, 서명 및 UI 실기는 미검증.
- 이번 전달은 로컬 소스 ZIP이며 GitHub PR/정식 업데이트 메타데이터는 변경하지 않는다.
