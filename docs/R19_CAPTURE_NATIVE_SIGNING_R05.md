# Sunrise File Manager R19 — Sunrise Capture 방식 네이티브 그룹서명 전환 R05

## 원인
- 사용자 V3 화면은 `Sunrise.GroupSigning.ps1`에 대한 앱 격리 검사 알림을 표시함. 이 알림만으로 악성/오탐은 확정할 수 없음.
- R02에서 `Sunrise.Signing.exe` 생성·실행을 제거했으나 R04 공식 빌드는 여전히 PowerShell `.ps1`을 인증서 선별과 임베디드 서명 확인에 실행함.

## 실제 Sunrise Capture v4.2.0과 대조
- 참조: Google Drive `3. Sunrise Group/1. Sunrise Capture/Sunrise_Capture_v4.2.0_FINAL.zip`의 `03_Source/build_scripts/build_pipeline.py`.
- Capture: Windows `certutil -user -store My` 출력의 SHA1 인증서 해시를 추출하고 `signtool sign /sha1 ... /fd SHA256`를 사용하며, `.ps1` 없이 서명함.
- Capture는 Python 빌드 오케스트레이터를 사용하지만 File Manager는 Python 추가 설치가 불필요하도록 CMD 전용 파서로 대체.
- Capture의 인증서 Subject와 File Manager의 `CN=Sunrise Group`은 서로 다르므로 File Manager는 **기존 그룹 인증서를 재사용**하며 새 인증서를 생성하지 않음.

## 수정 사항
1. `build-tools/FindSunriseCertificate.cmd`: `certutil`로 CurrentUser/My 검색, `sha1` 라벨 오른쪽 값을 정확한 40자리 16진수로 검증하고 인증서 지문 선택.
2. 복수 인증서가 감지되면 자동 선택하지 않고 중단. 이 경우 승인된 `SUNRISE_CERT_SHA1` 40자리 지문 명시 필요.
3. `BUILD_RELEASE_ONEDIR.cmd`: SHA-1과 Sunrise Group 주체 이름을 SignTool에 함께 전달. 기존 PS1 사전검사 및 파일 검증을 제거하고 native SignTool 서명 + verify `/pa /v`로 변경. EXE/DLL 각각 파일 해시 기록.
4. `BUILD_RELEASE_INSTALLER.cmd`: Setup 및 제거 프로그램은 기존 Inno Setup SignTool `/S` 옵션을 유지하고 별도 PS1 임베디드 검증 없이 SignTool 검증.
5. `CHECK_INSTALLER_BUILD_ENV.cmd`: CertUtil 유무·네이티브 helper 존재 확인; PowerShell 실행 요구 삭제.
6. 구 `Sunrise.GroupSigning.ps1`은 소스 패키지에서 삭제; `Sunrise.Signing` 라이브러리 소스는 레거시 참고 자료로만 보존, 빌드에서는 호출하지 않음.

## 보안 경계
- V3 예외·차단 해제·검사 우회 없음. 보안 소프트웨어가 네이티브 도구를 감시한다면 해당 검사는 정상적으로 진행됨.
- 개인키·PFX 미포함, 인증서 자체 생성·내보내기 없음.
- SignTool이 인증서를 찾지 못하거나 서명에 실패하면 빌드는 중단됨. 사설/자체서명 인증서의 WinTrust 불신뢰는 별도로 기록하고 정상 신뢰로 표시하지 않음.
- **정적 코드 검사만 실행**되었으며 Windows SignTool·V3·Inno Setup·설치 및 제거의 실기 검증은 보류.

## Windows 재검증
1. 새 폴더에 전체 ZIP을 풀거나 `git pull origin main` 실행.
2. `CHECK_INSTALLER_BUILD_ENV.cmd` 실행 (관리자 권한 불필요).
3. `BUILD_ON_WINDOWS_COMPANY.cmd` 실행.
4. 실패 시 `build-logs/R14_certutil_store.log`, `R14_signing_preflight.log`, `R14_sign_exe.log`, `R14_verify_exe.log`, `R15_inno_compile.log` 확인.
5. V3가 경고하면 보안 설정을 바꾸지 말고 대상 파일명을 확인하고 회사 IT에 문의.
