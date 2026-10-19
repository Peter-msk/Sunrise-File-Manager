# Sunrise File Manager — R14 그룹서명 빌드 운영 가이드

## 목표
- R13 소스 전체/OneDir/UI/Ollama 코드 유지
- Sunrise Group 개발표준의 `CN=Sunrise Group` Windows Authenticode 서명 적용
- 회사 PC에서 업무 문서 Fixture 검사 및 UI 자동 캡처 생략 유지
- 서명 성공 후에만 배포 폴더 및 ZIP 교체

## 사용자 실행 절차
1. R14 전체 소스 ZIP을 독립 폴더에 압축 해제한다.
2. 빌드 PC의 `Cert:\CurrentUser\My` (현재 사용자 - 개인)에 기존 Sunrise Group 코드 서명 인증서가 있는지 확인한다. 인증서에는 개인키가 있어야 한다.
3. Windows SDK의 `signtool.exe`가 설치되어 있어야 한다. 기존 SDK 설치경로 또는 PATH에서 자동 탐색한다.
4. `BUILD_ON_WINDOWS_COMPANY.cmd` 실행.
5. 성공한 경우 `dist\SunriseFileManager_OneDir_R14\Sunrise File Manager.cmd`로 실행.
6. 파일 속성 → 디지털 서명에서 Sunrise Group 서명 정보 확인. 서명 저장 후 인증서 신뢰 상태는 별개다.

## 코드서명 정책
- 기본값은 **기존 인증서 재사용**. 새 인증서 자동 발급은 기본 OFF.
- 인증서가 존재하지 않을 때만, 조직 승인 후 빌드 CMD를 실행하기 전에 같은 CMD 창에서 `set SUNRISE_ALLOW_CERT_CREATE=1`로 명시적으로 허용할 수 있다.
- 해당 생성 옵션은 현재 Windows 사용자의 Personal/My에 인증서를 하나 생성한다. RSA 3072, SHA-256, Code Signing EKU. 비내보내기 CNG 개인키.
- PFX/P12, 개인키/암호/자격증명은 소스, ZIP, 사용자용 `app/`에 들어가지 않는다.
- `Sunrise.FileManager.exe` 및 `Sunrise.FileManager.dll`처럼 **이 프로젝트가 생성한 실행 PE**만 서명한다.
- Microsoft/.NET/PdfPig 런타임·종속 DLL을 Sunrise Group 명의로 재서명하지 않는다.
- 타임스탬프: 기본 RFC3161 `http://timestamp.digicert.com`, 승인된 TSA 사용 시 `SUNRISE_TIMESTAMP_URL` 환경변수로 변경 가능.
- 네트워크/보안 정책으로 타임스탬프가 실패하면 서명 단계를 중단하고 기존 배포 폴더를 유지한다. 연결 제한을 우회하지 않는다.
- 서명 후 SHA-256을 기록한 후 OneDir ZIP을 만든다.
- 자체 서명 인증서에는 공인 CA 신뢰와 SmartScreen/V3 평판이 자동으로 부여되지 않는다. Windows SignTool `/pa` 결과가 신뢰되지 않으면 로그에 구분한다.
- 디지털 서명이 존재하는 것(서명자 식별)과 Windows가 해당 인증서 체인을 신뢰하는 것(공인 신뢰)은 별개의 확인 사항이다.

## 로그
`build-logs/` 하위:
- `R14_BUILD_RESULT.txt`: 최종 빌드/서명/패키징 상태
- `R14_signing_preflight.log`: 인증서 후보 판정 오류
- `R14_sign_exe.log`, `R14_sign_dll.log`: 실제 SignTool 결과
- `R14_verify_exe.log`, `R14_verify_dll.log`: 내장 서명자 인증서 비교
- `R14_wintrust_exe.log`, `R14_wintrust_dll.log`: Windows 신뢰 체인 검증
- `R14_signed_exe_sha256.txt`, `R14_signed_dll_sha256.txt`: 서명 후 바이너리 해시
- `R14_signed_package_sha256.txt`: 최종 ZIP 해시

## 빌드 오류시
- `SIGNING_PREFLIGHT`: Windows SDK SignTool 미설치, 인증서 부재, 인증서 개인키·EKU·기간·RSA 키크기 조건 불충족 또는 서명 보조도구 컴파일 실패.
- `AUTHENTICODE_SIGN`: SignTool 오류, 인증서 개인키 접근 제한, RFC3161 TSA 접속 불가.
- `SIGNATURE_CHECK`: 서명자 불일치 또는 신뢰/서명 검증 실패.
- `WPF_PUBLISH`: 기존 .NET 프로젝트 컴파일 오류.
- `PACKAGE`: OneDir ZIP 생성 오류.
- 이전 정상 배포본은 새 릴리즈가 검증되지 않는 한 교체하지 않는다.

## 검증 수준
정적 Python 계약검사만 완료. **실제 Windows SDK 기반 서명과 사용자 PC 실기 서명 인증은 아직 미실행**. 보조 도구의 새 인증서 발급 경로도 Windows에서 1회 확인해야 한다.
