# R19 설치형 빌드 전면 재감사 R03 — ISCC 미발견 대응

## 현장 확인

현장 출력 `VERSION OK 1.0.0` 이후 `INNO_SETUP_NOT_FOUND`. 실제 컴파일/서명은 아직 시작되지 않았음. 파일 누락, C# 빌드 불량으로 단정할 수 없음.

## 코드상 확인 및 수정

1. 기존 ISCC.exe 탐색이 PATH 및 대표 Program Files 위치로 제한됨. 수동 설치·사용자 계정 설치·다른 드라이브 경로에는 거짓 음성 가능.
2. `build-tools/FindInnoSetup.cmd`에 명시적인 `SUNRISE_ISCC` (잘못된 경로이면 fail-closed), PATH, Program Files, 사용자별 설치 폴더, 사용자/컴퓨터 등록정보 탐색 추가.
3. 실제 ISCC 부재 시 설치파일을 만들 수 없음. 사용자의 승인 및 조직 IT 정책에 따라 Inno Setup을 직접 설치해야 함. **무단 다운로드, winget 자동 설치, 보안 예외 등록, ExecutionPolicy 변경은 없음.**
4. `R15_inno_probe.log` 별도 기록 및 정확한 조치 메시지.
5. WPF 게시 이후 사용하지 않는 `DOTNET_EXE` 경로 재검사 삭제. 실제 WPF 게시에서 SDK 검증되므로 불필요한 추가 실패 요인.
6. `CHECK_INSTALLER_BUILD_ENV.cmd`는 회사문서·Fixture를 읽지 않고, 빌드·서명·설치 없이 기본 경로/도구 존재 여부만 검증.
7. SignTool도 설치형/OneDir/사전점검 간 공통 resolver로 통일하고 지정된 `SUNRISE_SIGNTOOL` 경로를 지원함. SDK 기본 경로와 PATH 검색 후에도 없으면 명확히 실패.
8. 최신 Inno 경고를 줄이기 위해 `x64` 구형 별칭을 동일한 x64 Windows 전용 식별자 `x64os`로 교체.
9. 회사용 빌드는 기존 문서 내용 테스트·UI 자동 캡처 금지, GroupSigner 및 설치형 Setup 서명 필수, 사용자 메모/설정 보존 정책 그대로 유지.

## Windows 실기 절차

**먼저** 프로젝트 루트에서 `CHECK_INSTALLER_BUILD_ENV.cmd`. `ISCC.exe`가 없으면 Inno Setup 공식 출처(https://jrsoftware.org/isdl.php)에서 조직 승인 후 설치. 지정 경로에 있을 경우 같은 CMD 창에서 `set "SUNRISE_ISCC=C:\Approved\Inno Setup 6\ISCC.exe"` 사용.

그다음 `BUILD_ON_WINDOWS_COMPANY.cmd` 실행. 누락/정책 문제는 자동 우회하지 않고 `build-logs/R15_INSTALLER_BUILD_RESULT.txt`, `R15_inno_probe.log`, `R14_signing_preflight.log`, `R14_wpf_publish.log`, `R15_inno_compile.log` 등 단계별 로그 확인.

## 빌드 파이프라인 감사 및 아직 검증되지 않은 부분

| 단계 | 감사 판단 | 실기 |
| --- | --- | --- |
| 버전/파일 정책 검사 | 기존 현장 로그에서 1.0.0 성공 | PASS (버전만) |
| Inno Compiler 사전 탐색 | 사용자/별도 폴더 누락 보완, absent 시 fail-closed | PENDING |
| SignTool/PowerShell | 환경별 설치·조직 정책에 따라 실패 가능 | PENDING |
| Windows 10 SDK (.NET 10) | OneDir 서브빌드에 SDK 검사 포함, 불필요한 중복검사 제거 | PENDING |
| Windows WPF publish | source / app build process 유지 | PENDING |
| Authenticode exe/dll + timestamp | 그룹 인증서 접근, RFC3161 네트워크 필수 | PENDING |
| Inno Setup compile + sign setup/uninstaller | /S sign tool, stable AppId, non-admin 설치 경로 유지 | PENDING |
| Output signature SHA256, backup/commit | stage after successful validation | PENDING |
| Install/update/uninstall/settings/memo | 원본 설정 보존 설계 | PENDING |

**주의**: 이전에 실행한 Python/텍스트 계약검사는 실제 Windows cmd.exe/ISCC/SignTool/보안솔루션을 대체하지 않습니다. 출시 승인 전 Windows 실기 검증이 필수입니다.
