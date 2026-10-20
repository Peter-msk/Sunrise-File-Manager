# Sunrise File Manager R19 — 빌드 과정 인증서 생성 전환 R07

## 사용자 요구와 구현
- 회사 파일 탐색기를 빌드할 때 그룹 인증서가 없다는 이유로 실패하지 않고, **프로그램 빌드 스크립트가 자체서명 `CN=Sunrise Group` 인증서를 생성**한다.
- `Sunrise.Signing.exe` 또는 별도 PS1 서명 보조 프로그램을 생성·호출하지 않는다.
- `certreq.exe`/`certutil.exe`/`signtool.exe` 등 **Windows·Windows SDK 기본 도구**만 실행한다. V3 예외·우회·실행정책 변경 없음.
- 공식 배포물은 Inno Setup 설치형 EXE이며, 탐색기·메모·Ollama C# 기능에는 손대지 않는다.

## 첫 공식 빌드 처리
1. .NET SDK·Inno Setup·SignTool 사전점검 (인증서 생성 없음).
2. `BUILD_RELEASE_ONEDIR.cmd --installer-payload`에서 `EnsureSunriseGroupSigning.cmd` 호출.
3. 사용자별 PIN 파일이 없다면 `Sunrise.GroupCodeSigning.inf`를 입력해 `certreq.exe -new -user -q` 실행. Microsoft 문서상 `RequestType=Cert`는 새 자체서명 인증서를 만들고 해당 사용자 저장소에 설치한다.
4. 인증서: `CN=Sunrise Group`, RSA 3072, SHA-256, 디지털 서명 키 사용, EKU `1.3.6.1.5.5.7.3.3`, CNG Microsoft Software KSP, `Exportable=FALSE`, 자체서명 3년 유효기간.
5. 출력 **공개 인증서**에서 SHA-1 지문을 읽고 그 지문으로 `CurrentUser\My`를 다시 조회하여 CN 일치 여부 확인. 이후 `group_signer.sha1`에 원자적으로 기록한다.
6. WPF 자체 포함형 게시 → EXE·자체 DLL Authenticode 서명 → Inno Setup Setup·Uninstaller 동일 SHA-1로 서명 → SHA-256 기록.

## 다음 빌드 및 안전성
- 기존 PIN이 있으면 해당 지문 인증서만 재사용. 파일 삭제·개인키 유실·지문 불일치가 감지되면 `PINNED_CERT_MISSING_OR_INVALID` 등의 오류로 정지한다. 다른 인증서를 자동으로 생성하거나 바꾸지 않는다.
- PIN은 `%LOCALAPPDATA%\SunriseGroup\FileManager\Signing\group_signer.sha1`에 있는 **공개 SHA-1 지문 문자열**이다. 이 PIN을 다른 PC로 복사해도 개인키가 전송되지는 않는다. 사용자 프로필 보관 및 접근권한은 시스템 정책을 따른다.
- 직접 확인용 `CHECK_SIGNING_CERT.cmd`는 `--check-only`로 호출하므로 **생성하지 않는다**. 첫 빌드에서는 `READY_TO_CREATE_DURING_BUILD` 상태를 표시한다.
- 동시 최초 빌드나 Windows 사용자 계정 교체는 지원하지 않는다. 한 서명 PC와 해당 사용자 프로필을 기준으로 운영한다.
- **만료 시 무조건 새 인증서를 만드는 것도 금지**한다. 변경 시 이전 서명과 게시자 연속성을 잃을 수 있어 IT의 갱신/배포 절차가 필요하다.

## 신뢰 모델 (반드시 구분)
- `CN=Sunrise Group` 자체서명은 누구나 동일한 이름으로 만들 수 있으므로 공인 조직 인증이 아니다. 같은 이름으로 만들어도 서로 다른 인증서이며 Windows SmartScreen/V3 평판이 자동 생기지 않는다.
- 디지털 서명은 EXE/DLL/Setup **내부에 공개 인증서와 서명 정보로 삽입**된다. 소스/설치파일에 개인키를 복사하거나 임베드하지 않는다.
- 조직 내 공인/관리형 게시자 식별이 필요하면 별도의 CA 코드서명 인증서 또는 조직이 배포하는 신뢰 정책이 필요하다. 이 빌드는 신뢰 루트 저장소를 임의 변경하지 않는다.
- 기업 보안 솔루션이 `certreq.exe`, SignTool 또는 새 서명 파일을 검사할 수 있으며 경고가 사라지는 것은 보장하지 않는다. **보안검사를 끄거나 예외 처리하지 않는다.**
- 보안 정책상 사용자별 자체서명 인증서 생성이 금지된 PC에서는 실패하며 해당 정책을 존중한다.

## 주요 파일 및 로그
- `build-tools/EnsureSunriseGroupSigning.cmd` — 최초 생성/이후 재사용 자동 분기
- `build-tools/Sunrise.GroupCodeSigning.inf` — 개인키 내보내기 불가 및 코드서명 용도 설정
- `build-tools/FindSunriseCertificate.cmd` — 생성 후 고정 SHA-1/CN 확인
- `build-logs/R14_cert_provision.log` — 생성/재사용/실패 상태
- `build-logs/R14_certreq_create.log` — 실제 Windows certreq 출력
- `build-logs/R14_certreq_dump.log` — 공개 인증서 지문 분석
- `build-logs/R14_signing_preflight.log` — 선택된 인증서 확인
- `build-logs/R14_sign_exe.log`, `R14_sign_dll.log`, `R15_inno_compile.log` — 서명 결과

## Windows 실기 승인 게이트
1. `CHECK_INSTALLER_BUILD_ENV.cmd`와 `CHECK_SIGNING_CERT.cmd` 확인. PIN 없는 최초 상태가 `READY_TO_CREATE_DURING_BUILD`인지 검사.
2. `BUILD_ON_WINDOWS_COMPANY.cmd` 최초 실행 시 `CREATED_AND_PINNED` 및 EXE·DLL·Setup 서명 확인.
3. 2회째 빌드에서 `REUSED_PINNED` 및 동일 thumbprint 확인.
4. 인증서 삭제/사용자 변경 상황은 폐기된 실험 계정에서만 안전하게 검증; 실제 사용자 개인키를 시험 목적으로 제거하지 말 것.
5. 최초 설치·업그레이드·제거, Windows 서명 상태, V3 경고 여부는 회사 PC에서 실기 확인.

**현재 상태:** Linux 정적·계약검사만 수행. Windows `certreq`, SignTool, 실제 인증서 설치·서명·Inno Setup 결과는 검증되지 않음.
