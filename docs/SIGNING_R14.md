# Sunrise File Manager — R14 그룹서명 빌드 운영 가이드

## 목표
- R13 소스 전체/OneDir/UI/Ollama 코드 유지
- Sunrise Group 개발표준의 `CN=Sunrise Group` Windows Authenticode 서명 적용
- 회사 PC에서 업무 문서 Fixture 검사 및 UI 자동 캡처 생략 유지
- 서명 성공 후에만 배포 폴더 및 ZIP 교체

## 사용자 실행 절차
1. R14 전체 소스 ZIP을 독립 폴더에 압축 해제한다.
2. R19 R07 공식 빌드는 기존 외부 인증서를 요구하지 않는다. 최초 빌드에서 Windows certreq로 자체서명 `CN=Sunrise Group` 인증서를 생성하고 CurrentUser/My에 저장한다.
3. Windows SDK의 `signtool.exe`가 설치되어 있어야 한다. 기존 SDK 설치경로 또는 PATH에서 자동 탐색한다.
4. `BUILD_ON_WINDOWS_COMPANY.cmd` 실행.
5. 성공한 경우 `dist\SunriseFileManager_OneDir_R14\Sunrise File Manager.cmd`로 실행.
6. 파일 속성 → 디지털 서명에서 Sunrise Group 서명 정보 확인. 서명 저장 후 인증서 신뢰 상태는 별개다.

## 코드서명 정책
- **R19 R07 기준:** 첫 공식 빌드에서 `certreq.exe -new -user -q`와 `RequestType=Cert` INF로 자체서명 인증서를 생성하며, 다음 빌드부터 생성된 동일 인증서를 고정 지문으로 재사용한다.
- 외부 인증서 검색/자동 대체는 공식 빌드 경로에서 사용하지 않는다. 생성 후 고정된 인증서를 임의로 삭제하거나 다른 것으로 자동 교체하지 않는다.
- 최초 빌드 시 생성된 인증서 개인키가 사라진 경우에는 자동 재생성을 하지 않고 원복 또는 서명자 변경 절차를 요청한다.
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
- `SIGNING_PREFLIGHT`: Windows SDK SignTool 미설치, 인증서 부재, 인증서 개인키·EKU·기간·RSA 키크기 조건 불충족 또는 인증서 조회/서명에 대한 조직 보안정책 제한.
- `AUTHENTICODE_SIGN`: SignTool 오류, 인증서 개인키 접근 제한, RFC3161 TSA 접속 불가.
- `SIGNATURE_CHECK`: 서명자 불일치 또는 신뢰/서명 검증 실패.
- `WPF_PUBLISH`: 기존 .NET 프로젝트 컴파일 오류.
- `PACKAGE`: OneDir ZIP 생성 오류.
- 이전 정상 배포본은 새 릴리즈가 검증되지 않는 한 교체하지 않는다.

## 검증 수준
정적 Python 계약검사만 완료. **실제 Windows SDK 기반 서명과 사용자 PC 실기 서명 인증은 아직 미실행**. 새로 컴파일한 서명 보조 EXE는 더 이상 실행하지 않는다. certutil과 SignTool의 실제 서명·검증은 Windows에서 확인해야 한다.

## R19 V3 Internet Security 경고 대응
- 기존 R14~R19 R01은 `dotnet run --project source/build-tools/Sunrise.Signing/...`를 실행하여 빌드 PC에서 `Sunrise.Signing.exe`를 새로 생성·실행하였다. V3 화면에서 이 이름을 직접 차단 대상으로 표시했다.
- R19 R05 공식 빌드는 **보조 EXE 및 PowerShell PS1 모두 실행하지 않음**. `FindSunriseCertificate.cmd`가 Windows `certutil.exe`로 SHA-1 인증서 지문을 조회하고 Windows SDK SignTool이 앱·Setup 서명 및 Windows 신뢰 검증을 수행함. 인증서가 없거나 복수이면 실패하며, 그룹 인증서 자동생성은 하지 않음.
- R19 R05부터 그룹서명용 PowerShell 스크립트는 실행하지 않는다. R06부터 인증서 검사 전용 CHECK_SIGNING_CERT.cmd를 제공하며, 서명 인증서 PROFILE은 GROUP을 기본으로 한다. 회사 보안정책 변경·V3 예외 등록은 개발 도구가 자동으로 수행하지 않는다.
- R14 당시의 C# 보조 코드가 있더라도 역사적 참고용 Library 프로젝트로만 남겨 `Sunrise.Signing.exe`가 생성되지 않도록 했다.
- 자체서명 인증서의 평판 문제는 별개이며 V3 경고 재발이 없다는 보장은 없다. 보안 담당자의 승인을 받은 후만 배포한다.

## R19 R06 인증서 진단
`CHECK_SIGNING_CERT.cmd`와 `docs/R19_SIGNING_PREFLIGHT_DIAGNOSTICS_R06.md`를 참조한다. Capture 인증서 사용은 명시적 프로필 및 조직 승인 조건이며, 게시자 이름이 Capture로 바뀐다.

## R19 R07 최신 빌드-내 인증서 생성 기준

`docs/R19_INBUILD_SELF_SIGNING_R07.md`를 우선 참조한다. 이전 R14~R06 인증서 수동 검색·Capture 프로필 설명은 기록용이며 현재 공식 정책과 다른 경우 R07이 우선한다.
