# Sunrise File Manager R10 — UI / Session 보완 기록

## 수정 기준
- `Sunrise_File_Manager_COMPANY_FULL_SOURCE_R09_ONEFILE_RELEASE.zip` 전체 소스
- Windows 10/11 x64, net10.0 WPF, Ollama 선택 기능 유지
- 회사 전용 OneFile 빌드, 문서 Fixture / LogicTests 접근 생략 유지

## 반영 내용
1. 좌측 폴더 탐색 트리: 빠른 이동/고정 폴더와 드라이브 목록을 하나의 스크롤 영역으로 통합. 기존 이중 스크롤 UI 제거.
2. 메뉴: 각 분할창 상단 파일/편집/보기/즐겨찾기/추가 기능/정보 메뉴의 선택 상태·호버 색상·글자색·드롭다운 배경/테두리를 라이트·다크용 공통 리소스로 통일.
3. 라이트/다크 테마 현재 선택 상태를 체크 표시로 표시.
4. PC 로컬 설정 복원: 탭별 마지막 폴더, 활성 탭, 활성 분할창, 분할 배치, 내비게이션 트리 표시, 미리보기·메모 표시, 테마, 고정 폴더, 창 위치·크기 및 최대화, 정보/미리보기 폭, 파일 목록 열 폭, 검색·분류·정렬 조건.
5. 정상 종료 시 저장하며 폴더 이동/테마 변경/분할 변경 후 약 1초 지연 저장. 사용자 파일 내용은 읽거나 설정에 저장하지 않음.
6. 사용자 설정 파일: `%LOCALAPPDATA%\Sunrise\FileManager\session.json` (이전 설정 스키마와 호환되는 신규 필드 추가).

## 빌드
- `BUILD_ON_WINDOWS_COMPANY.cmd` : 소스 없는 자체 포함형 OneFile 실행파일 생성
- 출력: `dist\Sunrise File Manager.exe`
- 로그: `build-logs\R10_RELEASE_RESULT.txt`, `build-logs\R10_01_singlefile_publish.log`
- 회사용 소스 패키지에는 PDF/HWPX/DOCX Fixture가 없어야 하며 문서 테스트는 실행하지 않음.

## 검증 한계
- Python 회귀 및 XAML XML/이벤트 정적 검사 수행
- Linux 환경에서 Windows WPF 실제 컴파일 및 UI 렌더링은 불가. Windows 실기 빌드 후 메뉴 열기, 첫 실행, 창 닫기·재실행 및 폴더 복원, 2~4분할별 설정 복원, 다크 테마 선택 상태를 반드시 확인할 것.
