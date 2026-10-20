# Sunrise File Manager R19 — .NET 10 SDK 경로 인용부호 오류 핫픽스 R04

## Windows 사용자 실기 로그
- Inno Setup: `[PASS] ISCC.exe` (`%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`)
- Windows SDK SignTool: `[PASS]`
- `.NET 10 SDK`: `'<드라이브>:\Program'은(는) ... 명령이 아닙니다`가 3회 출력된 뒤 `[FAIL]`
- `CHECK_INSTALLER_BUILD_ENV.cmd`에서 발생했으므로 WPF `dotnet publish` 단계까지 진행되지 않음.

## 진단 / 원인 확실성
- **확인된 코드 결함:** R03의 `CHECK_INSTALLER_BUILD_ENV.cmd`와 `BUILD_RELEASE_ONEDIR.cmd`는 `FOR /F IN ('"<dotnet 경로>" --list-sdks ...')` 구문으로 SDK 후보를 검사했음.
- CMD가 명령 치환에서 중첩된 따옴표를 처리할 때 경로가 `C:\Program`으로 잘리는 현상이 발생할 수 있으며, 사용자 로그와 일치함.
- **미확정:** 회사 PC에 실제 .NET 10 SDK가 설치되어 있는지, `global.json`이 해당 SDK를 선택할 수 있는지는 이번 로그만으로 확인할 수 없음.

## R04 수정
1. 공통 `build-tools/FindDotNet10.cmd`를 신규 추가하고 사전점검·OneDir WPF 게시 단계가 동일하게 사용하도록 변경.
2. SDK 후보 경로를 **독립 명령** `"%~1" --version > 로그` 형태로 실행하고, 출력된 **파일**에서 버전 `10.`으로 시작하는지 판독. `FOR /F` 명령 치환에 인용부호가 든 dotnet 실행 명령을 직접 넣지 않음.
3. `dotnet --version`은 저장소 루트의 `global.json` 버전 선택 정책(`10.0.100`, `latestFeature`, 정식 SDK)을 적용함. 단순 목록 검사 통과보다 정확함.
4. 승인된 비표준 설치 경로는 `SUNRISE_DOTNET`에 `dotnet.exe`의 절대경로를 지정할 수 있음. 설정 경로가 잘못됐다면 자동으로 다른 도구를 사용하지 않고 실패.
5. 탐지 및 실제 WPF 게시 스크립트에서 같은 선택 결과를 활용; 유효 SDK가 없으면 실패 유지.
6. 보안정책 우회·SDK 자동 다운로드/설치·문서 검사·인증서 생성은 없음.

## 빌드 전 확인 순서
1. 최신 ZIP은 **기존 폴더 위에 덮어쓰기보다 별도 새 폴더에 해제**.
2. `CHECK_INSTALLER_BUILD_ENV.cmd`를 실행하여 `[PASS] .NET 10 SDK detected` 확인.
3. 통과하면 `BUILD_ON_WINDOWS_COMPANY.cmd`로 WPF 게시·서명·설치파일 제작 진행.
4. SDK를 찾지 못하면 `build-logs/R15_dotnet_probe.log`, `build-logs/R15_dotnet_candidate.log`와 `INSTALLER_ENV_PREFLIGHT.txt` 확인. 실제 설치 여부는 명령 프롬프트에서 `where dotnet`, `"C:\Program Files\dotnet\dotnet.exe" --list-sdks`로 별도 확인 가능.
5. SDK가 정말 없다면 회사 IT 승인 후 .NET 10 SDK 설치. 런타임만 설치되어 있으면 `--list-sdks`에 표시되지 않음.

별도 SDK 경로 지정 예시(CMD의 실제 경로로 변경):
```cmd
set "SUNRISE_DOTNET=D:\ApprovedTools\dotnet\dotnet.exe"
CHECK_INSTALLER_BUILD_ENV.cmd
BUILD_ON_WINDOWS_COMPANY.cmd
```

## 정적 검증 범위
- 배포 계약·코드 회귀검사 및 기존 패키징 검증만 실행 가능.
- Windows CMD의 실제 공백 경로 실행, .NET 10 WPF 게시, V3, Inno Setup 및 Authenticode 서명은 회사 PC에서 실기 재검증 필요.
