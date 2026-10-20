# Sunrise File Manager R19 R08 — SignTool 없는 개발용 EXE 빌드 분리

## 요구사항과 수정 결정
- 사용자가 Windows PC에 `signtool.exe`가 없어 공식 설치형 빌드의 사전점검 단계에서 중단되는 문제를 보고함.
- SignTool은 WPF EXE **컴파일에는 필수가 아니며**, 기존 Authenticode 서명/Setup 배포 정책 때문에 필수로 설정되어 있었음.
- **최소 변경:** `BUILD_DEV_EXE.cmd`를 추가하고 기존 공식 서명 빌드 진입점과 SignTool 탐지기는 수정하지 않음.
- `BUILD_DEV_FULL_SOURCE.cmd`는 소스 포함 포터블 레거시이며 새로운 `BUILD_DEV_EXE.cmd`는 단순한 **개발용 미서명 EXE 게시** 전용.

## 실행 경로

| 모드 | 진입점 | 요구 도구 | 결과 | 대외 배포 |
|---|---|---|---|---|
| 개발 환경 사전점검 | `BUILD_DEV_EXE.cmd --check-only` | .NET 10 SDK | `build-logs/R19_DEV_BUILD_RESULT.txt` | 해당 없음 |
| 개발용 EXE | `BUILD_DEV_EXE.cmd` | .NET 10 SDK | `dist/SunriseFileManager_DEV_UNSIGNED/app/Sunrise.FileManager.exe` | **금지** |
| 공식 설치 EXE | `BUILD_ON_WINDOWS_COMPANY.cmd` | .NET 10 SDK + Windows SDK SignTool + Inno Setup + 인증서 생성용 Windows 도구 | `dist/Sunrise_File_Manager_1.0.0_Setup.exe` (예정) | Windows 실기 릴리즈 게이트 통과 후에만 |

- 개발용 빌드는 공유 `FindDotNet10.cmd`를 호출하므로 기존 `global.json` 버전 선택·공백 있는 경로 수정이 유지됨.
- `dotnet publish --configuration Release --framework net10.0-windows10.0.19041.0 --runtime win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:SunriseCompanyBuild=true`로 한 차례 게시함. LogicTests, 문서 미리보기, 사내 자료 Fixture, SignTool, 인증서, Inno를 실행하지 않음.
- 개발용 결과는 기존 설치형 결과와 다른 `_DEV_UNSIGNED` 폴더에만 출력. 빌드가 성공한 뒤에만 이전 **개발용** 결과 교체, 컴파일 실패 시 이전 결과 보존.
- `--check-only`는 인증서 생성·게시·설치·서명 없이 SDK와 소스 상태만 검사. 개발용 사전점검에서 SignTool 누락은 오류가 아님.
- 공식 설치형(`BUILD_RELEASE_INSTALLER.cmd`, `BUILD_RELEASE_ONEDIR.cmd`, `BUILD_ON_WINDOWS_COMPANY.cmd`)은 그대로 보존. SignTool이 없으면 여전히 **공식 빌드만** 실패하며 미서명 상태로 우회 진행하지 않음.

## Windows 실기 테스트 게이트 (미수행)
1. SignTool/Inno가 없는 Windows x64 + .NET 10 SDK에서 `BUILD_DEV_EXE.cmd --check-only` → `STATUS=READY_FOR_DEV_BUILD`.
2. 같은 PC에서 `BUILD_DEV_EXE.cmd` → `STATUS=SUCCESS`, `SIGNED=NO`, `OFFICIAL_RELEASE=NO` 및 app EXE/DLL + 런타임 파일 확인.
3. 생성된 개발용 EXE 실행, 로컬 탐색/메모/탭 테스트(승인된 테스트 파일만), 종료·재시작 확인.
4. 공식 진입점의 SignTool 누락은 여전히 `SIGNTOOL_NOT_FOUND`/사전점검 실패. 이 경우 `dist/..._Setup.exe`를 새로 생성하거나 개발용 EXE로 교체하지 않아야 함.
5. 실패한 `dotnet publish` 이후 이전 DEV_UNSIGNED 출력 보존, 설치/업그레이드·서명 인증서·사용자 설정 무변경 확인.
6. SignTool 설치가 끝난 회사 PC에서 공식 빌드/서명/설치/제거 검증을 별도로 진행. 보안 예외·인증서 신뢰루트 자동등록 없음.

**현재 판정:** SOURCE / STATIC CONTRACT PASS(수행한 항목 한정), WINDOWS DEV BUILD 및 SIGNED INSTALLER는 PENDING. 실제 개발용 EXE는 Linux 환경에서 제공/생성하지 못함.
