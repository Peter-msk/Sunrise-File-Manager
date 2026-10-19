# v1.8.2 build/portable restoration R02

## 목적
v1.7에서 확립한 전체 Portable(app+source) 배포 구조와 기본 실행 경로를 Windows 빌드 산출물로 복원한다. v1.8.2 기능 코드는 그대로 유지하고, 신규 EXE의 성공적 게시 여부를 패키징의 필수조건으로 한다.

## 단계
SDK_CHECK -> LOGIC_TESTS(최초/변경시) -> WINDOWS_NATIVE_SMOKE(최초/변경시) -> WPF_PUBLISH -> PORTABLE_PACKAGE -> COMPLETE

- SDK_CHECK: PATH, x64 Program Files, DOTNET_ROOT, 사용자 설치, 레지스트리, where.exe 후보 검사. global.json의 실제 선택 버전을 기록한다.
- 테스트 재사용: `TESTED_SOURCES.json`에 Windows NativeSmoke PASS, WindowsPublish PASS, 동일 소스 해시가 모두 있어야 한다. 그렇지 않으면 테스트 실행으로 자동 전환한다.
- WPF_PUBLISH: 매번 깨끗한 신규 폴더로 self-contained win-x64 게시. 기존 EXE 사용 금지.
- PORTABLE_PACKAGE: 게시한 EXE/DLL/종속성을 staging의 app에 복사; SHA256 비교, APP_MANIFEST 생성; source 전체 포함, 압축 검사 후 새로운 dist 릴리스 생성. 중단 시 부분 staged 폴더 제거.
- Windows UI/DPI 실기 수용 검사는 자동 PASS 처리하지 않는다.

## 제한
Windows WPF 컴파일/Portable 생성은 Windows SDK PC에서 수행되어야 하며 현재 전달한 것은 전체 소스의 빌드 준비본이다. SDK가 설치된 사용자 PC의 `SdkDiagnostics` 없이 이전 오류 원인을 최종 단정할 수 없다.
