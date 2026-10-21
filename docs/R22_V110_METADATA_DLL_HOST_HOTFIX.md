# R22 — Windows 실제 로그 기반 빌드 개선 (1.1.0 유지)

## 실기 로그 증거

사용자가 제출한 `새 폴더.zip` (33개 로그)의 R14 WPF publish 결과는 0 warnings/0 errors, R14 payload success, R15 ISCC signed setup success. 최종 R16 release metadata만 `ReleaseMetadata.exe` 프로세스를 시작할 때 `Access is denied`로 실패했습니다. 인증서·설치파일 문제가 아닙니다.

## 수정

- `BUILD_RELEASE_INSTALLER.cmd`: 별도 .NET 콘솔 apphost EXE를 시작하던 `dotnet run --project`를 제거했습니다. `dotnet build -p:UseAppHost=false`로 DLL을 생성한 후, 승인된 `dotnet.exe` 호스트를 통해 DLL을 로드합니다. 원인별 빌드/실행 로그를 분리하며 실패 시 기존 Setup 롤백은 유지합니다.
- `BUILD_ON_WINDOWS_COMPANY.cmd`: 13항목짜리 사전 체크리스트를 별도 실행하는 중복 단계를 삭제했습니다. 설치 스크립트와 payload 단계에서 실제로 필요할 때 SDK, ISCC, SignTool, 서명 인증서를 찾고 안전하게 실패합니다.
- `BUILD_RELEASE_ONEDIR.cmd`: `FindDotNet10`이 확인한 SDK를 다시 `--list-sdks`로 확인하던 중복 호출 제거.
- 반복 파일 존재성 검사(이미 검증된 child payload 재검사)만 삭제했습니다. **인증서 SHA1 고정, SignTool 서명·검증, 설치파일 해시, 회사 정책, 버전, 릴리스 산출물 검증은 삭제하지 않았습니다.**

## 결과·한계

- 기존 v1.0.0 GitHub 공개 latest.json/notice.txt/Setup는 변경하지 않았으며 1.1.0 설치파일을 게시하지 않았습니다.
- 빌드 시 로그는 `build-logs/R16_metadata_build.log` (DLL 컴파일)와 `R16_release_metadata.log` (DLL 실행)를 참조합니다.
- 이 환경에서는 Windows `dotnet`/SignTool/Inno가 없으므로 수정 R22를 PC에서 다시 빌드해 확인해야 합니다. 접근 거부 원인이 호스트 DLL 로드까지 제한하면 IT 실행정책 조사 필요; 보안 예외나 우회는 수행하지 않습니다.
