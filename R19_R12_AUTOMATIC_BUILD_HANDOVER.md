# Sunrise File Manager R19 R12 - 자동 빌드/진입점 정리 인수인계

기준: 사용자 제공 R11 전체 소스. 공식 버전은 `1.0.0`, 배포 방식은 서명된 Inno Setup 설치형 EXE.

## 기존 오류
R11은 동일 `CN=Sunrise Group` 인증서가 하나라도 있으면서 프로젝트 PIN이 없으면 `UNPINNED_GROUP_CERT_MANUAL_REVIEW`로 중단하고, 사용자에게 SHA-1을 입력받았다. 이는 프로젝트 신규 인증서와 다른 Sunrise 제품 인증서를 구분하지 못한 데서 비롯되었다.

## R12 흐름
- 공식 단일 진입점 `BUILD_ON_WINDOWS_COMPANY.cmd`. 개발용은 `BUILD_DEV_EXE.cmd`.
- `group_signer.sha1` 존재: 고정 SHA-1 인증서만 재사용; 찾지 못하면 실패.
- 고정 지문 없이 사용자 프로필 `pending_group_public.cer` 존재: 그 공개 인증서로 기존 생성 인증서 식별·고정 재시도.
- R09/R11 작업 폴더에 남은 `build-logs/R14_generated_group_public.cer`: 프로필로 이관·복구. 서로 다른 pending 파일이면 자동 선택 금지.
- 고정 지문/미완료 공개 인증서/기존 설치 산출물 모두 없음: 프로젝트 전용 새 자체서명 인증서를 **1회** 자동 생성하고 공개 DER 지문을 고정. 다른 제품의 동일 CN 인증서는 선택하지 않음.
- 최종 `dist/Sunrise_File_Manager_1.0.0_Setup.exe`가 존재하는데 pin이 없으면, 게시자 변경을 막기 위해 자동 키 재생성 대신 오류 표시.
- 회사 PC 보안정책, 개인키 및 WinTrust 신뢰 상태를 변경하거나 무력화하지 않음. Python/PS1 서명 보조 EXE 없음.

## 제거한 레거시 진입점
`APPROVE_EXISTING_GROUP_SIGNER.cmd`, `CHECK_SIGNING_CERT.cmd`, `BUILD_ON_WINDOWS.cmd`, `BUILD_DEV_FULL_SOURCE.cmd`, `BUILD_RELEASE_ONEFILE.cmd`, `BUILD_AND_CAPTURE_WINDOWS.cmd`, `CAPTURE_UI_WINDOWS.cmd`, `CHECK_SDK.cmd` (8개).

내부 실행에 필수인 `BUILD_RELEASE_INSTALLER.cmd`, `BUILD_RELEASE_ONEDIR.cmd`, `CHECK_INSTALLER_BUILD_ENV.cmd`, `build-tools/`는 유지. 또한 빌드 외 편의도구 `RUN_FILE_MANAGER.cmd`, `INSTALL_OLLAMA.cmd`는 유지. 소스 앱 기능·데이터·키/인증서 저장소는 변경하지 않음.

## 검증 한계
Linux 컨테이너에서는 Windows `cmd.exe`, `certreq`, `SignTool`, `ISCC`, WPF 실기 동작을 검증할 수 없다. 현 단계는 소스/계약 검증이며 서명된 설치파일의 성공이 아니다. 인증서 관련 오류는 `build-logs/R14_cert_provision.log` 및 `R14_signing_preflight.log`를 확인한다.
