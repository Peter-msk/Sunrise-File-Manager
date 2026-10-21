# Sunrise File Manager 1.0.0 / R19 — 한국어 설치 마법사 및 사용자용 공지 정비

## 변경 범위
- Inno Setup의 단일 언어를 English(Default.isl)에서 Korean(Languages\Korean.isl)으로 변경. 새 빌드 시 설치와 제거 UI가 한국어를 사용하도록 구성. Windows Inno Setup 6에 Korean.isl이 없으면 컴파일 실패할 수 있으므로 설치 PC에서 확인 필요.
- `notice.txt`와 원본 `release/NOTICE_BODY.txt`: 제품 출시/기능/공식 다운로드 정보만 표시. 개발·소스·빌드·서명·정책 내부 설명 제외.
- `latest.json`: 기존 게시 릴리스의 버전/다운로드 URL/실제 파일 크기/SHA-256 그대로 유지. `release_notes`는 이용자 대상 간단한 설명만 유지.
- `.NET` 릴리스 메타데이터 생성기의 향후 `release_notes`도 이용자 전용 문구로 변경. 빌드/배포 진단 상태는 `release_info.json`, `UPLOAD_TO_GITHUB.txt` 등 내부 산출물에만 표시.
- 공식 v1.0.0 설치파일은 재빌드/교체하지 않음. 수정된 한국어 설치 화면은 **다음 Windows 빌드 설치파일부터 적용**.

## 용량 비교
- 사용자 화면의 설치 요구 공간 약 174.1MB, 실제 v1.0.0 설치파일은 51,251,864바이트 (약 48.88MiB).
- 프로젝트의 `BUILD_RELEASE_ONEDIR.cmd`는 `--self-contained true`, `-p:PublishTrimmed=false`, `-p:PublishSingleFile=false`로 WPF와 .NET 10 런타임을 함께 배포합니다.
- Q-Dir은 최소 크기의 네이티브 Windows 실행파일로 약 841KB(홈페이지 일반 다운로드 표시) ~ 1.1MB(x64 다운로드)를 제공합니다. 기능/런타임/포함 DLL 구성이 달라 직접 동급 용량 비교는 어렵습니다.
- WPF/.NET 트리밍을 바로 활성화하면 리플렉션/디자인 리소스 문제가 생길 수 있으며 안정성 검증 없이 적용하지 않음.
- .NET 데스크톱 런타임을 PC에 따로 설치하는 framework-dependent 설치형을 **별도 옵션**으로 추후 실험 가능하지만, 현재 공식 설치형의 독립 실행 가능성은 유지합니다.

## 빌드·검증 게이트
- Inno `Languages\Korean.isl`의 실제 존재 여부 및 UI 한글 출력은 Windows Inno Setup 컴파일/설치 검사 필요.
- 현재 제공된 v1.0.0 릴리스 EXE는 English 설치화면을 갖고 있으므로 태그를 그대로 두면 변경되지 않음.
- 재출시할 때는 버전 충돌을 막기 위해 다음 공식 버전/태그로 빌드·배포 후 `latest.json` 갱신 권장.
