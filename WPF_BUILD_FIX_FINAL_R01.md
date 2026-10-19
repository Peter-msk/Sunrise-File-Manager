# Sunrise File Manager — OpenSource Full / WPF Build Fix Final R01

## 기준본

- **기준 소스**: 사용자가 전달한 `Sunrise_File_Manager_OpenSource_Full.zip` (Ollama AI / CMD 빌드)
- **반영 패치**: 이전 `Sunrise_File_Manager_WPF_Build_Hotfix_R01.zip`의 수정 2건
- **배포 방식**: 추가 패치 적용이 필요 없는 **전체 프로젝트 소스** ZIP
- **기존 Ollama, CMD, 탐색기/미리보기/메모 기능**: 유지. 소스 코드 변경은 아래 두 파일만 해당.

## 적용 내역

1. `source/Sunrise.FileManager/Controls/FileInspector.xaml.cs`: CS0165. `LoadMemoHistory`에서 nullable `FileEntry? entry = _selected;`를 명시해 definite assignment 문제 해결.
2. `source/Sunrise.FileManager/MainWindow.xaml.cs`: CS8602. `ArrangePanels` 전반부 선택 버튼 상태 갱신과 중복된 버튼 배경 설정 4줄 제거. 분할 버튼 표시 로직 유지.

## 빌드 방법

1. ZIP 압축을 **새 폴더**에 완전히 해제합니다.
2. 최상위의 `BUILD_ON_WINDOWS.cmd` 실행. 로컬 Windows .NET SDK 10.0.402 환경에서 이전에 단위 테스트가 통과한 이력이 있습니다. 현재 통합본의 Windows 빌드 성공은 **아직 확인하지 못했습니다**.
3. `build-logs\02_wpf_build.log`를 확인합니다. 빌드 성공 시 `dist\SunriseFileManager_Portable\app\Sunrise.FileManager.exe`에 실행 파일이 게시되도록 기존 CMD 스크립트가 설정되어 있습니다.
4. 다른 오류가 있으면 `build-logs` 아래 해당 로그를 전달합니다.

## 검증 구분

- 적용 전후 두 C# 파일 SHA-256 일치: PASS
- 사용자 제공 전체 소스 파일 보존 검증: PASS (기존 파일 삭제/누락 없음)
- Python 정적/계약 테스트: 결과는 `source/reports/WPF_BUILD_FIX_FINAL_R01.json` 참고
- 실제 Windows WPF 컴파일·실행 확인: NOT RUN (현재 도구 환경에 Windows/.NET SDK 없음)

**주의:** 이전 CMD 빌드 로그의 `LogicTests PASS`는 수정 전 기준본에서 관찰된 이력이며, 통합 후 Windows 빌드 완료를 의미하지 않습니다.
