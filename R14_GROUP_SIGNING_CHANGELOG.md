# R14 Sunrise Group Authenticode 서명 적용

기준본: R13 OneDir 전체 소스.

변경:
- `BUILD_RELEASE_ONEDIR.cmd` 서명 인증서 탐색, SignTool 서명, 서명자 비교, 해시 재산출, 실패 시 배포 보호 게이트 추가
- `source/build-tools/Sunrise.Signing/` 신규: CurrentUser/My 기존 인증서 선택 및 선택적 1회 생성, 내장 인증서 검증
- `Sunrise.FileManager.csproj` Company, Authors, Description, Copyright 메타데이터 적용
- `BUILD_ON_WINDOWS_COMPANY.cmd`, `BUILD_ON_WINDOWS.cmd`, `RUN_FILE_MANAGER.cmd` R14 경로 갱신
- 회귀 테스트는 새 버전 경로를 검사하도록 갱신, 새 R14 signing contract 테스트 추가

변경하지 않음: Windows 탐색기 UI, 파일작업, #memo, Ollama, WPF 실행 구조, 회사 문서검사 금지, OneDir 기본 정책.

**Windows 실제 Authenticode 서명 및 실기검증: PENDING.**
