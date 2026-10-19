# Sunrise File Manager R09 — 사용자용 단일 EXE 배포

## 변경 목적

R07/R08 OneDir 배포에서는 Windows x64 .NET 10 WPF의 런타임 및 프레임워크 DLL을 개별 파일로 포함해 사용자가 많은 파일 중에서 EXE를 찾아야 했음. R09의 회사 기본 빌드는 **Self-contained Single-file Windows x64**로 변경함. 기존 OneDir 빌드 스크립트는 필요 시 대체 검증용으로 유지함.

## 회사 빌드 절차

1. `BUILD_ON_WINDOWS_COMPANY.cmd`를 실행
2. .NET 10 SDK 자동 탐색 후 WPF 단일 EXE를 컴파일/게시함. LogicTests, 문서 Fixture 읽기, 미리보기/UI 자동 캡처 실행은 하지 않음
3. 결과가 성공하면 `dist/Sunrise File Manager.exe`가 생성되고 Windows 탐색기가 해당 EXE를 선택한 상태로 열림
4. 이후에는 최상위의 `RUN_FILE_MANAGER.cmd`를 더블클릭해도 실행 가능

## 배포 결과

```text
dist/
  Sunrise File Manager.exe                 ← 배포 및 실행에 필요한 프로그램 파일 하나
  Sunrise_File_Manager_OneFile_Windows_x64_R09.zip
```

배포 ZIP에는 위 EXE 하나와 별도 `_licenses/` 폴더만 포함됨. `.cs`, `.xaml`, `.pdb`, `.dll`, `.json`, `source/`, `Fixtures/`은 사용자 배포 ZIP에 포함하지 않음. 전체 소스는 개발용 R09 소스 ZIP으로 별도 보관함.

- `BUILD_RELEASE_ONEFILE.cmd` : 단일 EXE 제작/배포 검증 스크립트
- `BUILD_RELEASE_ONEDIR.cmd` : 기존 OneDir 대체 방식(회사 보안정책에 따른 허용 여부 별도 확인)
- `BUILD_ON_WINDOWS.cmd` : 기존 개발·전체 소스 패키지용, 회사 모드 감지 시 문서 테스트 생략

## 주의 사항

- OneFile은 .NET 10의 네이티브 구성요소를 실행 시 임시 디렉터리로 추출할 수 있음. 회사 보안 소프트웨어 정책이 이를 차단할 경우 **우회하지 않고 중단**하며 `BUILD_RELEASE_ONEDIR.cmd`를 허용되는 범위에서 검토해야 함.
- 공개된 실행 파일의 역컴파일 가능성까지 제거하는 보안 기술은 아님.
- Ollama는 선택 기능이며 모델/서버를 포함하지 않음.
- 프로그램 실행 전부터 회사 SecureDisk가 차단한 문서에 접근하는 기능을 추가하지 않음.
- 회사 빌드에서 LogicTests를 실행하지 않으므로 컴파일 통과와 실사용 기능 검증을 구분함.
- Windows WPF 빌드와 실행 검증은 Windows에서 해야 함. 현재 개발 환경의 정적검사만으로 성공을 보장하지 않음.
