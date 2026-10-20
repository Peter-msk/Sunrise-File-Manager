# Sunrise File Manager R19 — 기존 인증서 명시 승인 R10

- 입력 기준: R09 전체 소스 ZIP. 제품 표시 버전 **1.0.0** 변경 없음.
- 이번 오류: `STAGE=SIGNING_PREFLIGHT / STATUS=UNPINNED_GROUP_CERT_MANUAL_REVIEW`.
- 실기 사용자 로그: Windows .NET 10.0.301, Inno Setup 6, SignTool **탐지 성공**. `certutil -user -store My "Sunrise Group"` 조회에서 **12개의 동명 인증서** 확인. 생성 시기와 개인키 용도가 서로 다름.
- 결론: SignTool 경로 오류가 아니라 **프로젝트 pin 부재 + 동일 CN 인증서 다수**가 원인. R09가 의도적으로 임의 키 선택을 중단한 상태.

## 적용 수정

1. `APPROVE_EXISTING_GROUP_SIGNER.cmd` 추가. 자동으로 가장 최근 인증서 또는 이름이 같은 아무 인증서나 선택하지 않음.
2. 사용자는 기존 서명 배포물의 게시자 키나 회사 IT가 승인한 **SHA-1 지문**을 직접 입력하고, 뒤 8자리와 `APPROVE`까지 재확인해야 함.
3. 입력은 40자리 16진수로 먼저 검증. 이후 기존 `FindSunriseCertificate.cmd`로 개인 저장소와 주체를 재확인하고 `certutil -v` 출력에서 **Code Signing EKU OID `1.3.6.1.5.5.7.3.3`**이 확인되어야만 고정.
4. `%LOCALAPPDATA%\SunriseGroup\FileManager\Signing\group_signer.sha1`에 **공개 인증서 지문만** 원자적 파일 교체로 기록. 개인키, PFX, 비밀번호 저장·이동·삭제 없음.
5. 기존 PIN이 있으면 덮어쓰지 않으며, 미완료 `R14_generated_group_public.cer`가 있으면 원래 인증서 복구를 먼저 시도하도록 중단.
6. 기존 R09 자동 생성/복구, 기존 공식 설치형 서명 실패 차단, 개발용 `BUILD_DEV_EXE.cmd`를 변경하지 않음. `EnsureSunriseGroupSigning.cmd`는 사용자 조치 안내만 보완.

## Windows 실행 순서

**원칙:** 이전 R09/R07 폴더와 사용자 인증서 저장소를 보존하고, 새 R10 ZIP을 **별도 폴더**에 해제.

1. 이전 실패 폴더 `build-logs/R14_generated_group_public.cer`가 존재하면 R10의 `build-logs/`에 복사하고 `BUILD_ON_WINDOWS_COMPANY.cmd` 실행. 동일한 인증서가 확인되면 자동 복구되어 별도 승인이 필요 없음.
2. 해당 공개 인증서가 없고 PIN도 없으면, 회사 IT 또는 과거 서명된 배포물과 대조하여 **승인할 정확한 SHA-1**을 결정. 인증서의 생성일이 최신이라는 이유만으로 선택하면 안 됨.
3. `APPROVE_EXISTING_GROUP_SIGNER.cmd`를 실행. 후보 목록을 확인하고 승인된 SHA-1 40자리 입력. 끝 8자 재입력 후 `APPROVE` 입력. 실패 시 어떤 PIN도 새로 기록하지 않음.
4. `CHECK_SIGNING_CERT.cmd`로 `STATUS=REUSED_PINNED` 확인.
5. `BUILD_ON_WINDOWS_COMPANY.cmd` 실행. 서명은 SignTool이 수행하므로 개인키/유효기간/타임스탬프 문제는 이 단계에서 따로 확인.
6. `build-logs/R15_INSTALLER_BUILD_RESULT.txt`가 성공이고 `dist/Sunrise_File_Manager_1.0.0_Setup.exe`에 일관된 서명과 SHA-256이 기록되는지 확인. 새 빌드만으로 공식 배포 승인하지 않음.

## 위험 및 검증 경계

- **선택한 지문이 원래 File Manager 발급자의 키와 동일한지는 자동 증명할 수 없음.** 이전 signed binary 또는 IT의 서명 인증서 자산 목록과 대조 필수. 이름이 같아도 서로 다른 인증서임.
- 사용자 로그의 일부 인증서는 `서명 테스트에 통과`, 다른 인증서는 `암호화 테스트에 통과`라고 표기되며 동명이인 대상이므로 무조건 선택 금지.
- `FindSunriseCertificate`의 CN 검사와 `certutil -v` EKU 검사로 사전 확인하지만 **개인키의 실제 파일 서명 능력**은 공식 빌드 SignTool 성공으로 판정. Windows 시험 전 성공을 단정하지 않음.
- 자체서명 인증서는 서명 정보가 있어도 신뢰된 공인 게시자가 아니며 SmartScreen/V3 경고 방지가 보장되지 않음.
- PIN 오선택 또는 개인키 분실 시 임의로 PIN 삭제/교체하거나 이전 인증서를 삭제하지 말고 IT 절차로 정정. 보안 설정·신뢰 루트 임의 변경 금지.
- 소스·계약검사만 수행할 수 있는 Linux 환경에서는 Windows 인증서 저장소·CMD·SignTool/ISCC 실기 결과는 **PENDING**.
