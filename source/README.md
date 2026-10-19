# Sunrise File Manager — 개발용 전체 소스 구조

- `Sunrise.FileManager/`: C# WPF 애플리케이션 전체 소스 코드
  - `MainWindow.xaml(.cs)`: Sunrise 고유 1~4분할 워크스페이스, 상단 `[🤖 AI 연결]` 버튼, 우측 스마트 `#memo` 및 독립 미리보기 도킹 제어
  - `Controls/ExplorerPane.xaml(.cs)`: Windows 기본 파일 탐색기 모든 기능 (브레드크럼/주소창, 다중 탭, 잘라내기/복사/붙여넣기/복제, 새 폴더/새 파일, 이름 변경, 휴지통/영구 삭제, ZIP 압축/해제, CMD 열기, 5종 보기 모드, 숨긴 항목/확장명/확인란 토글, 유형 필터, 내·외부 드래그 앤 드롭)
  - `Controls/FolderTree.xaml(.cs)`: 빠른 이동(홈/바탕화면/다운로드/문서/사진/음악/동영상/휴지통), 즐겨찾기 고정(`+ 고정`), 내 PC 드라이브 여유 용량 바 및 폴더 트리
  - `Controls/OllamaSetupDialog.xaml(.cs)`: 최초 `[AI 연결]` 클릭 시 온보드 AI(`Ollama`) 기능 설명, 설치 여부 선택, `cmd.exe` 전용 GitHub 자동 설치 대화상자
  - `Controls/AiCommandPanel.xaml(.cs)`: 온보드 AI(`Ollama`) 및 로컬 규칙 기반 자연어 파일 검색·분류·정리 및 로컬 문서 요약 패널
  - `Services/AiOllamaService.cs`: GitHub 오픈소스 `Ollama` (`http://127.0.0.1:11434`) 로컬 연동 및 `cmd.exe` 자동 설치 서비스 (PowerShell 미사용, Gemini 제거 완료)
- `tests/`: C# 단위/통합 테스트(`LogicTests`), WPF UI 스모크 테스트(`UiSmokeTests`), Python 정적/계약 검증 스크립트
