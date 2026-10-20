# Sunrise File Manager R19 — 최초 생성 인증서 지문 파싱·재사용 보완 R09

- 기준 소스: `Sunrise_File_Manager_1.0.0_R19_DEV_UNSIGNED_BUILD_R08_FULL_SOURCE.zip`
- 보완 범위: `build-tools/EnsureSunriseGroupSigning.cmd` 인증서 생성·지문 확정·복구 분기
- 공식 배포: **Inno Setup + Sunrise Group 자체서명 Authenticode** 그대로 유지
- 검증 범위: Linux 정적·계약 회귀 / **Windows 실제 빌드 및 설치 미검증**

## 1. 사용자 Windows 로그와 근본 원인

사용자 로그는 `Inno Setup ISCC.exe`와 `Windows SDK signtool.exe`를 정상 검출했다. 설치형 빌드는 `PAYLOAD_BUILD`를 실행했으나 하위 `SIGNING_PREFLIGHT → CERTIFICATE_PROVISION`에서 멈췄다.

```
STATUS=CREATING_SELF_SIGNED_CERTIFICATE
STATUS=CREATED_SHA1_NOT_PARSED
HASH_COUNT=6
```

R07은 `certutil -dump <공개 인증서>`의 모든 `sha1` 행 중 40자리 16진수처럼 보이는 후보를 셌다. `-dump`는 인증서 지문뿐 아니라 확장 필드·식별자 등 다양한 해시를 출력할 수 있으므로 **후보 6개를 선택 실패**로 판정했다. `HASH_COUNT=6`은 인증서가 6개 생성됐다는 뜻이 아니다. 이 실패는 WPF 게시 이전이다.

## 2. R09 최소 보완

1. 공개 인증서 파일의 내용과 `CN=Sunrise Group` / 코드서명 EKU `1.3.6.1.5.5.7.3.3` 표시를 `certutil -dump`로 사전 검사한다.
2. 공개 `.cer` 파일이 Base64라면 Windows 기본 `certutil -decode`로 DER로 변환하고, 이진 DER라면 원본을 사용한다.
3. `certutil -hashfile <DER> SHA1`의 **완전한 40자리 해시 한 줄**만 지문 후보로 인정한다. `-dump`의 여러 `sha1` 줄을 더 이상 지문 추출에 쓰지 않는다.
4. 추출한 SHA-1이 Windows `CurrentUser\My`에 등록된 실제 인증서의 지문과 일치하고 요구된 CN에 해당하는지를 기존 `FindSunriseCertificate.cmd`로 확인한다. 검증 완료 이후에만 공개 지문 `group_signer.sha1`을 원자 기록한다.
5. 생성 후 지문 파싱 오류가 났다면 `build-logs/R14_generated_group_public.cer`를 삭제하지 않고 **다음 공식 빌드에서 우선 복구**한다. 복구 결과는 `STATUS=RECOVERED_AND_PINNED`, 정상 최초 생성은 `CREATED_AND_PINNED`, 기존 PIN 재사용은 `REUSED_PINNED`로 구분한다.
6. PIN과 복구용 `.cer`가 모두 없는데 동일 CN의 인증서가 이미 있으면 `UNPINNED_GROUP_CERT_MANUAL_REVIEW`로 중단한다. 인증서 저장소 조회가 불명확하면 `CERT_STORE_PROBE_INDETERMINATE`로 중단한다. 임의 새 인증서 발급으로 우회하지 않는다.

## 3. 사용자 PC 재실행 — 기존 실패 폴더의 공개 인증서 보존

**기존 R08 빌드 폴더/인증서/개인키를 삭제하거나 재발급하지 말 것.**

1. 기존 R08 폴더의 `build-logs\R14_generated_group_public.cer`가 존재하는지 확인한다. 이는 **공개 인증서 파일**이며, PFX/개인키가 아니다.
2. 새 R09 ZIP은 **별도 폴더에 압축 해제**한다.
3. 이전 폴더의 `R14_generated_group_public.cer` 파일 **하나만** 새 R09 폴더의 `build-logs\`에 복사한다. `build-logs` 폴더가 없으면 새로 만든다. 다른 인증서·키·설정파일을 복사하지 않는다.
4. 새 폴더의 `CHECK_SIGNING_CERT.cmd`를 실행한다. `R14_cert_provision.log`에 `READY_TO_RECOVER_PENDING_CERTIFICATE`가 표시되면 복구 준비 상태다. 이는 **인증서를 변경하거나 생성하지 않는 읽기 전용 점검**이다.
5. 새 폴더의 `BUILD_ON_WINDOWS_COMPANY.cmd`를 실행한다. R09는 같은 공개 인증서에서 지문을 산출한 후 인증서 저장소를 대조하여 PIN을 기록하며, 이후 게시·서명·Setup 제작으로 넘어간다.
6. `build-logs\R14_cert_provision.log`의 `RECOVERED_AND_PINNED`, `R14_signing_preflight.log`의 `STATUS=SELECTED`, `R15_INSTALLER_BUILD_RESULT.txt`의 최종 결과를 확인한다. 서명·설치가 완전히 성공하기 전에는 공식 배포하지 않는다.

### 공개 인증서가 존재하지 않는 경우

`R14_generated_group_public.cer`가 없는데 R07 실행으로 인증서가 이미 만들어졌다면 새로운 빌드가 `UNPINNED_GROUP_CERT_MANUAL_REVIEW`로 중단될 수 있다. 이 경우 **이전 설치 폴더·인증서 저장소·PIN 파일을 삭제하지 않는다**. `R14_certreq_create.log`, `R14_certreq_dump.log`, `R14_unpinned_group_probe.log`의 비밀정보를 제외한 진단 내용으로 원래 인증서를 식별하여 IT가 복구 경로를 결정해야 한다.

### SignTool 자체가 없는 경우

공식 설치형 배포에는 SignTool이 필요하다. R08에서 추가한 `BUILD_DEV_EXE.cmd`는 여전히 SignTool 없이 미서명 개발용 EXE만 생성한다. R09는 개발용 빌드를 공식 배포로 바꾸지 않는다.

## 4. 검증 및 보안 제한

- Python 정적·계약 회귀: **121 tests passed, 26 subtests passed**. CMD 텍스트 CRLF/ASCII 확인.
- 이 결과는 Windows CMD 실행, 인증서 DER/Base64 판별, `certutil` 로캘별 출력, SignTool 서명, 실제 Inno Setup/설치 성공의 증거가 **아님**.
- PIN에는 인증서의 공개 SHA-1 지문만 저장한다. SHA-1은 인증서 식별용이며 파일 서명은 SHA-256으로 수행한다.
- 자체서명은 공인 조직 신원을 증명하거나 V3/SmartScreen 신뢰를 자동 부여하지 않는다. 보안 정책, 인증서 저장소의 신뢰루트, 서명 검사를 우회하지 않는다.
- 프로젝트 소스의 메모·파일 탐색·Ollama 기능, 설치 AppId, 업데이트·사용자 데이터 계약은 변경하지 않았다.
