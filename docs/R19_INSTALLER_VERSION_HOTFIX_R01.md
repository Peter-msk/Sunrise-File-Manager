# Sunrise File Manager R19 — 설치형 빌드 버전검사 핫픽스 R01

## 현장 증상

```
[ERROR] Installer build stopped at VERSION_OR_INSTALLER_CONFIG.
[BUILD FAILED] See build-logs\R15_INSTALLER_BUILD_RESULT.txt.
```

원본 `BUILD_RELEASE_INSTALLER.cmd`는 다음 세 오류를 같은 단계로 기록했음.

1. `installer\RELEASE_VERSION.txt` 파일 없음
2. `installer\SunriseFileManager.iss` 파일 없음
3. `findstr /R /X`로 세 자리 숫자 버전 추출 실패

제공된 화면 메시지에는 세 오류 중 어느 조건인지 나타나지 않음. R19 소스에는 두 파일이 포함되어 있고 버전 값은 `1.0.0`임. 사용자의 실제 PC에서 발생한 구체적 원인은 아직 미확정이며 성공 빌드는 확인하지 않았음.

## 시정

- Windows CMD 내부의 `FOR /F`로 버전을 읽고, `:validate_release_version`·`:check_decimal_segment`로 세 부분의 숫자 및 점 구조 검사.
- 입력 파일이 없거나 값이 형식에 맞지 않으면 **빌드를 중단**하고 사유별 `DETAIL=`과 `build-logs\R15_version_preflight.log` 기록.
- 원본 파일이 정상일 때만 버전/빌드 단계 진행. 무조건 `1.0.0`으로 통과시키는 임시 우회는 적용하지 않음.
- 회사 전용 정책(문서 Fixture·내용 테스트 금지), Authenticode 인증서·Inno Setup·사용자 데이터 정책 불변.

## 확인 방법

프로젝트 루트에서 `BUILD_ON_WINDOWS_COMPANY.cmd` 실행. 실패 시 다음을 확인:

```
type build-logs\R15_INSTALLER_BUILD_RESULT.txt
type build-logs\R15_version_preflight.log
```

`CHECK=PASS`가 찍히고 다음 단계에서 실패한다면 `R15_payload_build.log`, `R14_signing_preflight.log`, `R14_wpf_publish.log`, `R15_inno_compile.log` 등 해당 단계 로그 확인.

## 검증 구분

- Python 계약/구조 회귀 테스트: Linux에서 실행 가능.
- Windows CMD, .NET 10 WPF publish, Signtool, ISCC 실제 동작: Windows 빌드 PC에서 확인 필요.
