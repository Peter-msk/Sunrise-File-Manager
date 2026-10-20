# Sunrise File Manager R19 — V3 그룹서명 보조 실행파일 경고 수정 (R02)

## 제보된 경고
AhnLab V3 Internet Security 9.0에서 클라우드 평판·행위 기반으로 `Sunrise.Signing.exe` 실행 알림이 표시됨.

## 원인 및 확실성
- **확인:** 기존 `BUILD_RELEASE_ONEDIR.cmd`는 `dotnet run --project source\build-tools\Sunrise.Signing\Sunrise.Signing.csproj ...`로 커스텀 `Sunrise.Signing.exe`를 생성·실행한다. `prepare`, 앱 EXE·DLL의 `verify-embedded`, Setup의 `verify-embedded`에 사용됨.
- **가능성:** 새로 빌드된 서명 이력/평판이 부족한 실행파일과 인증서 접근 행위가 V3의 평판·행위 탐지 대상이 되었을 수 있음.
- **확정 불가:** 스크린샷만으로 오탐·실제 악성 여부나 정확한 탐지 엔진/정책 세부사유를 판단할 수 없음. V3 로그·관리자 확인이 필요함.

## 시정 사항
1. 공식 빌드 파이프라인에서 커스텀 서명 도구의 `dotnet run` 실행을 모두 제거함.
2. 역사적 C# 보조 프로젝트는 라이브러리로 비활성화하여 EXE가 생성되지 않도록 함.
3. 기존 그룹 인증서의 소유·EKU·유효기간·RSA 키 크기를 Windows PowerShell `Cert:\CurrentUser\My` API로 확인.
4. 기존 Windows SDK `signtool.exe`로 Sunrise 자체 EXE·DLL과 Inno Setup 설치파일·Uninstaller를 서명함.
5. 빌드된 파일은 **내장 Authenticode 인증서 지문 및 CN 대조** 후 SignTool로 Windows 신뢰 상태를 별도 확인함.
6. 빌드 중 임의 새 인증서 발급을 중지함. 기등록 인증서가 없으면 보안담당자의 인증서 준비를 요청하고 빌드 실패.
7. `-ExecutionPolicy Bypass` / V3 예외 등록/폴더 배제 등 보안 정책 완화는 **전혀 수행하지 않음**.

## Windows 빌드
- Windows PowerShell 5.1, .NET 10 SDK, Windows SDK SignTool, Inno Setup 6/7, 기존 `CN=Sunrise Group` 코드서명 인증서 필요.
- R19 수정 소스를 새 폴더에 압축 해제한 후 `BUILD_ON_WINDOWS_COMPANY.cmd` 실행.
- `R14_signing_preflight.log`, `R14_verify_exe.log`, `R14_verify_dll.log`, `R15_verify_setup.log`에 인증서 선별·서명자 비교 결과가 기록됨.
- 스크립트 정책으로 실행이 제한되면 로그를 IT 담당자에게 전달하고, 임의로 보안 설정을 변경하지 말 것.
- V3 경고가 반복되면 파일 SHA-256, V3 탐지 시각·진단명·상세 로그를 조직 보안 담당자에게 제공하고 정식 오탐 검토 요청. 차단 유지 권장.

## 검증 한계
- Linux에서 가능한 소스·Python 계약 검사 및 ZIP 무결성 검사만 실행.
- Windows PowerShell 인증서 조회·SignTool 서명·Inno Setup 설치, AhnLab 실제 탐지 결과는 **미검증**.
