# Sunrise File Manager R19 — 서명 인증서 사전검사 진단 R06

## 실기 로그로 확인한 사실
- Windows의 .NET SDK는 `10.0.301`, ISCC.exe와 Windows SDK SignTool은 탐지됨.
- 설치형 외부 단계는 `PAYLOAD_BUILD`이지만, 하위 `R15_payload_build.log`는 `SIGNING_PREFLIGHT` 실패를 기록함.
- 사용자 제공 로그에는 `R14_signing_preflight.log`와 `R14_certutil_store.log`가 **없으므로 실제 실패 세부 사유는 미확정**임.
- 인증서가 없음과 파서가 실패함을 동일시하지 않음.

## R06 개선
1. `CHECK_SIGNING_CERT.cmd` : 게시나 서명 없이 Windows CurrentUser/My 인증서 선택만 진단할 수 있음.
2. `FindSunriseCertificate.cmd` : 프로필 GROUP(기본 `CN=Sunrise Group`) 또는 CAPTURE(명시 `CN=Sunrise Capture Self-Signed Code Signing`)만 허용함. 어떠한 자동 대체도 없음.
3. 기존 인증서 복수 검색 시 선택 중단, 승인된 SHA1 (`SUNRISE_CERT_SHA1`)만 별도 명시 허용.
4. `R14_signing_preflight.log`에 분기별 명확한 STATUS를 기록하고, 하위·상위 빌드가 해당 로그를 화면으로 전달.
5. EXE·DLL은 SignTool에 선택된 SHA1과 주체 이름을 함께 주어 서명하고, Setup·Uninstaller는 동일 SHA1로 서명함.
6. 결과 로그에 실제 선택된 게시자 CN을 표시하되, CAPTURE로 서명했다면 Sunrise Group 서명으로 잘못 표시하지 않음.
7. 자체 서명 보조 EXE, PowerShell PS1, 인증서 신규 생성, V3 예외 및 Windows 보안 정책 우회가 없음.

## 실행 순서 (새 폴더에 R06 풀기)
```
CHECK_INSTALLER_BUILD_ENV.cmd
CHECK_SIGNING_CERT.cmd
BUILD_ON_WINDOWS_COMPANY.cmd
```

기본 GROUP 프로필이 실패할 때 `build-logs/R14_signing_preflight.log`를 보고 원인 확인:
- `CERT_NOT_FOUND`: 그룹 인증서 없음 또는 조회 실패; `R14_certutil_store.log`를 비교
- `SHA1_NOT_PARSED`: 인증서 검색은 성공했으나 유효한 SHA1 지문을 파싱하지 못함
- `MULTIPLE_CERTIFICATES`: 동일 주체 인증서가 2개 이상; 승인 지문 직접 지정 필요
- `SUBJECT_MISMATCH`: 선택한 지문에 해당하는 주체가 요구한 이름과 다름
- `STATUS=SELECTED`: 인증서 조회만 통과. 실제 개인키·코드서명 EKU 및 타임스탬프 사용 가능 여부는 이후 SignTool 실행으로 확인함

Capture 인증서를 File Manager에서도 사용하도록 **조직이 승인했다면**, 같은 CMD 창에서 아래처럼 명시적으로 선택합니다.
```
set "SUNRISE_CERT_PROFILE=CAPTURE"
CHECK_SIGNING_CERT.cmd
BUILD_ON_WINDOWS_COMPANY.cmd
```
이는 `CN=Sunrise Group` 서명이 아니라 **`CN=Sunrise Capture Self-Signed Code Signing` 게시자로 서명하는 것**입니다. 서명 주체 변경을 승인받지 않았다면 위 환경변수를 사용하지 마세요. `SUNRISE_CERT_PROFILE=GROUP` 또는 새 CMD 창으로 기본 복귀할 수 있습니다.

실패 로그에 지문/공개 인증서 메타데이터가 포함될 수 있으나 **개인키/암호는 절대로 전송하지 마세요**. 사용자 PC 로그의 선택된 지문은 필요하면 일부 마스킹할 수 있습니다.

## 남은 검증
Windows에서 실제 인증서 선택, WPF `dotnet publish`, EXE·DLL/Setup/Uninstaller 코드서명, V3, 설치/제거까지의 실기 검증 필요. 이번 패키지는 Windows 설치 EXE가 아닌 전체 소스 수정본임.
