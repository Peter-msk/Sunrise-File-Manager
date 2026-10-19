# Sunrise File Manager - CMD Portable 빌드 표준화 R02

## 기준

- 입력: `Sunrise_File_Manager_OpenSource_Full_WPF_HOTFIX_FINAL_R01.zip`
- 변경 범위: 빌드 CMD, SDK 정책, 배포·검증 문서, 빌드 계약 테스트만 수정
- 기존 `FileInspector.xaml.cs`의 CS0165 / `MainWindow.xaml.cs`의 CS8602 Hotfix 보존
- Ollama, 파일 정리, `#memo`, Windows 탐색기 3열 UI 소스는 변경하지 않음

## 변경 사항

1. `global.json`을 빌드 작업 디렉터리(최상위)와 `source` 양쪽에 배치하고 .NET 10.0.100 이상 기능 밴드를 허용함. .NET SDK 10.0.402 호환 대상.
2. SDK 8/9로의 프레임워크 자동 강등 제거. 10.x SDK가 실제로 보이는 `dotnet.exe`만 선택.
3. `LogicTests`→WPF→`win-x64` 자체 포함형 publish→전체 소스 ZIP 순서로 고정.
4. `--self-contained false` 폴백 제거, 게시된 EXE + hostfxr/coreclr/PresentationFramework 확인.
5. source(`bin`, `obj` 제외), 빌드 CMD, Ollama 설치 스크립트, 문서 등을 함께 포함한 ZIP 생성 및 tar 목록 검증.
6. 빌드 성공 전에는 기존 릴리스를 바꾸지 않고 스테이징 파일에서 먼저 검증. `build-logs/BUILD_RESULT.txt`에 결과 기록.
7. Windows UI 스모크 시험은 `BUILD_AND_CAPTURE_WINDOWS.cmd`로 빌드 성공 후 별도 실행.

## 실행

- `BUILD_ON_WINDOWS.cmd` : Windows x64 새 EXE 및 전체 소스 ZIP 생성.
- `BUILD_AND_CAPTURE_WINDOWS.cmd` : 빌드 후 실제 Windows UI 스모크 실행.
- `CHECK_SDK.cmd` : SDK 설치 상태 출력(설치 변경 없음).

## 산출물

- `dist/SunriseFileManager_Portable_R02/app/Sunrise.FileManager.exe`
- `dist/SunriseFileManager_Portable_R02/START_FILE_MANAGER.cmd`
- `dist/SunriseFileManager_Windows_x64_Portable_FullSource_R02.zip`
- `build-logs/BUILD_RESULT.txt`, 단계별 로그

## 검증 한계

이 환경에는 dotnet SDK 및 Windows WPF 실행 환경이 없습니다. 인터넷을 통한 SDK 설치도 불가능합니다. 따라서 실제 Windows 컴파일·Portable 게시·실행 캡처는 아직 수행하지 못했습니다. `BUILD_TEST_ENVIRONMENT.txt`를 참고하십시오. Python 정적/계약 검사는 Windows 실행의 대체 증거가 아닙니다.
