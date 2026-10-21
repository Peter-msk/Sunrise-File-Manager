# Sunrise File Manager — 1.1.0 소스 기준본

- **개발 소스 버전:** 1.1.0 (UI 테마·가독성 보완, R21 Windows CS0246 빌드 핫픽스)
- **현재 공개 설치 버전:** 1.1.0 (2026-10-21 GitHub 릴리스 게시, R23 수정 소스 미포함)
- **차기 설치 파일:** `BUILD_ON_WINDOWS_COMPANY.cmd`로 Windows에서 컴파일·서명 후 검증하여 배포
- [1.1.0 UI 변경·PC 검사 안내](docs/R20_V110_THEME_ACCESSIBILITY_HANDOVER.md)

버전별 `release/NOTICE_BODY_1.1.0.txt`은 차기 릴리스에 사용합니다. 루트 `latest.json` / `notice.txt`는 기존 공개 1.0.0에 연결되므로 새 Setup 게시 전에는 수정하지 않습니다. `Sunrise_Control/control.txt`는 중앙 실행·점검·업데이트 정책만 유지합니다.

소스 정적 검사 통과, Windows WPF 실제 컴파일·설치·DPI·사용자 조작 검증은 완료되지 않았습니다.

- [R21 Windows 실기 CS0246 핫픽스 안내](docs/R21_V110_WPF_CS0246_IO_HOTFIX.md)

- [R22 빌드 오류·중복 검사 간소화](docs/R22_V110_METADATA_DLL_HOST_HOTFIX.md)
- R21 Windows 빌드 로그에서 WPF 0 errors / signed Setup success, 마지막 ReleaseMetadata.exe 실행만 ACCESS_DENIED.
- R22는 `dotnet build -p:UseAppHost=false` 뒤 `dotnet <ReleaseMetadata.dll>`을 실행하여 unsigned helper EXE 실행을 제거합니다.
- 공식 빌드 시작 시 반복적인 별도 체크리스트 출력은 생략하며 필요한 환경 검증은 실제 사용 단계에서 시행합니다.

- [R23 업데이트 파일 잠금 핫픽스](docs/R23_V110_UPDATE_FILE_LOCK_HOTFIX.md)
- 기존 1.0.0 및 이미 배포된 1.1.0 EXE의 자동 다운로드 오류는 소스 변경만으로 소급 교체되지 않음. 사용자는 브라우저에서 v1.1.0 Setup을 직접 설치해야 함.
- 이 소스 압축본의 `latest.json`은 R22 아카이브의 릴리스 시점 데이터(1.0.0)를 보존함. 실제 운영 최신 메타데이터는 공개 GitHub main의 1.1.0 `latest.json`이며, 설치 프로그램 릴리스 자산은 별개임.
