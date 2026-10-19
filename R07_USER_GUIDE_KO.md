# Sunrise File Manager — R07 테마 및 OneDir 배포 가이드

## 배포 형식: OneDir 자체 포함형 선택

WPF와 .NET 10 Desktop Runtime, PdfPig 의존성 및 Ollama 선택 연동 구조를 기준으로 **OneDir**을 선택함.
단일 실행파일(OneFile)은 구성은 단순하지만 WPF/네이티브 런타임의 추출과 초기 기동 지연,
보안 제품의 파일 추출 검사 및 문제 분석 측면에서 불리할 수 있어 본 버전에서는 채택하지 않음.
**OneDir은 `exe` 파일 하나가 아니라, 실행파일과 의존 DLL이 들어 있는 하나의 폴더**임.
사용자에게는 폴더 전체를 전달해야 하며 EXE만 분리하여 실행하면 안 됨.

## 회사 PC 배포 (기존 문서 접근 제한 보존)

1. 전체 소스 ZIP 압축 해제.
2. `BUILD_ON_WINDOWS_COMPANY.cmd` 실행 (Windows x64, .NET 10 SDK 필요).
3. 실행이 성공하면 `dist/SunriseFileManager_OneDir_R07/Sunrise.FileManager.exe` 사용.
4. 사용자 전달용은 `dist/SunriseFileManager_OneDir_Windows_x64_R07.zip`이며 **소스는 없음**.
5. 개발용 전체 소스 ZIP은 기존 빌드 결과인 `dist/SunriseFileManager_Windows_x64_Portable_FullSource_COMPANY_R04.zip`으로 별도 유지.
6. 회사 모드에서는 문서 픽스처 및 문서 내용 테스트를 실행하지 않으며, 생성된 사용자 배포본에도 포함하지 않음.

회사 아닌 개발 환경에서는 `BUILD_RELEASE_ONEDIR.cmd`를 실행. 프로그램 실행 시 .NET SDK가 필요하지 않으며, Ollama는 선택 사항임.

## UI 변경

- Sunrise Image의 라이트/다크 팔레트 구성을 참고해 WPF 테마 관리자 `ThemeManager` 추가.
- 상단 `테마` 메뉴에서 라이트/다크 즉시 전환. 종료 시 `session.json`에 선택값 저장.
- 파일 목록·폴더 트리·메모·미리보기·메뉴 등 공통 배경/글자/선택 색상 리소스 분리.
- 상단 `파일·편집·보기·즐겨찾기·추가 기능·정보` 메뉴에 브랜드 스타일/마우스 올림 강조.
- 분할 GridSplitter 폭을 6에서 11로 확대하고, 중앙 손잡이와 커서, hover 강조를 추가.
- 독립 미리보기 열, 상세정보·메모 열의 경계도 11로 조절 영역 확대.
- 기존 단일/2/3/4 분할 및 F8 파일 정리, 파일 메모, SecureDisk 회사 빌드 정책 유지.

## 검증 주의

이 배포 ZIP은 수정된 **전체 소스**이며 Windows에서 컴파일한 EXE가 포함되지 않음.
Linux 작업 환경에서는 .NET 10 SDK 및 Windows WPF 런타임이 없어 실제 Windows 빌드·실행·DPI 검증을 하지 못함.
소스 검사 합격을 Windows 실행 합격으로 해석하지 말 것. Windows 빌드 후 실제 화면으로 추가 확인 필요.
