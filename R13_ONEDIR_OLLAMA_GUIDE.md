# Sunrise File Manager R13 — Ollama + OneDir 기준 복원

## 결정

- AI 제품·화면·설명 이름은 **Ollama**로 통일함(공식 GitHub 주소 `ollama/ollama`는 그대로 유지)
- 사용자 배포는 **OneDir(자체 포함 win-x64)** 을 기본으로 함
- 개발용 전체 소스 ZIP과 사용자용 실행 배포 ZIP을 분리함
- OneFile(R09)은 비교·유지보수 목적의 비기본 스크립트로만 남김
- 모듈화의 단위는 C# 원본 소스·서비스·프로젝트 구조이며 배포 방식과 독립임. OneDir도 C# 개별 소스가 각각 DLL로 생성되는 것을 의미하지는 않음

## 실행 방법

Windows 회사 PC에서 `BUILD_ON_WINDOWS_COMPANY.cmd` 실행. SDK .NET 10 자동 탐색 후 WPF publish를 수행하며, 사용자는 아래 폴더의 실행 진입점을 더블클릭하면 됨.

```text
dist/
  SunriseFileManager_OneDir_R13/
    Sunrise File Manager.cmd       <- 사용자 실행 진입점
    READ_ME_FIRST.txt
    LICENSE
    licenses/
    app/
      Sunrise.FileManager.exe      <- 실제 실행 프로그램
      Sunrise.FileManager.dll
      hostfxr.dll / coreclr.dll / PresentationFramework.dll / ...
  SunriseFileManager_OneDir_Windows_x64_R13.zip
```

- 실제 런타임 DLL은 `app/` 내부에 필요함. `app`을 삭제하거나 그 안의 파일을 개별적으로 옮기면 실행이 안 될 수 있음
- 최상위 실행용 CMD는 원본 프로젝트 빌드 성공을 요구하지 않고 앱만 실행함
- 신규 게시 단계는 `dotnet publish --self-contained true -p:PublishSingleFile=false`이며 별도 OneFile 폴백 없음
- 회사용 LogicTests, 문서 Fixture 복사·읽기, UI 자동 캡처는 실행하지 않음
- 라이선스 고지 외 다른 개발 소스는 사용자 배포 ZIP에 넣지 않음
- Windows 실기 WPF 빌드·실행 검사는 현 환경에서 미수행. 정적 계약 검사는 실제 빌드 성공을 보장하지 않음

## 변경된 진입점

- `BUILD_ON_WINDOWS_COMPANY.cmd`: 기본 회사 OneDir 빌드
- `BUILD_ON_WINDOWS.cmd`: 현재 OneDir 기본 빌드
- `BUILD_RELEASE_ONEDIR.cmd`: 빌드·검증·패키징 원본 스크립트
- `RUN_FILE_MANAGER.cmd`: 개발 폴더에서 성공한 OneDir 실행
- `BUILD_DEV_FULL_SOURCE.cmd`: 구형 개발자 전체 소스 패키징을 분리 보관
- `BUILD_RELEASE_ONEFILE.cmd`: 과거 OneFile 선택형 스크립트 유지(비기본)

## 중요

OneFile로 게시한다고 **코드 모듈화가 사라지는 것은 아님**. 모듈·계층 구조는 빌드 전 코드 구조이고, OneFile과 OneDir은 주로 프로그램 바이너리와 .NET 런타임을 배포하는 방식의 차이임. 확장 모듈을 별도 DLL로 동적 교체하려면 별도 C# 프로젝트·어셈블리·플러그인 계약이 필요함.
