# Sunrise File Manager — R11 UI 가독성·휠 스크롤 보완

## 기준본
- `Sunrise_File_Manager_COMPANY_FULL_SOURCE_R10_UI_SESSION_FIX.zip` 전체 소스 기준
- .NET 10 WPF / 회사 전용 문서검사 생략 / 사용자 OneFile EXE 게시 유지
- Windows에서 실제 빌드 및 실행 화면 검증은 수행하지 않았음

## 변경 항목
1. `Controls/FolderTree.xaml` 및 `.xaml.cs`
   - 좌측 단일 스크롤 영역에 `NavigationScroll` 명칭 부여
   - 중첩 TreeView가 휠 입력을 소비하는 경우에도 `PreviewMouseWheel`에서 바깥 스크롤에 입력 전달
   - Ctrl+휠은 다른 제스처 처리를 위해 그대로 통과
2. `Controls/ExplorerPane.xaml`
   - 탐색기 명령 표시줄과 모든 명령 버튼에 공통 테마 전경색을 명시
   - 유형/보기 콤보박스의 전경·배경·테두리를 테마 리소스로 지정
   - 버튼 간격/높이/레이블 정렬 통일; 상시 표시 명령 수 축소
   - 복제·실행 취소·다시 실행·ZIP·CMD·탐색창 조정은 기존 상단/우클릭 메뉴 및 단축키에서 계속 제공
3. `App.xaml`
   - 메뉴 가독성 13pt 및 패딩·높이 조정
4. 배포 검사 코드 정합성
   - Windows CMD 스크립트 CRLF 정규화(내용/빌드 정책 변경 없음)
   - 구 버전 전용 검사에서 R10 결과 파일명/레이아웃 버전도 인정
5. `source/tests/test_r11_scroll_theme_toolbar.py`
   - 새 휠 처리·테마 버튼·메뉴 경로·회사 정책 유지 계약 검증

## 검증 범위
- Source Python `unittest` 59 PASS
- Top-level release Python `unittest` 27 PASS
- 정적 독립 열/XAML/이벤트 검증 156 PASS
- WPF XAML XML 구조 13개 PASS
- Windows WPF 실제 컴파일/실행/캡처: **미실행** (현재 환경에서 Windows 및 .NET SDK 없음)

## 회사 PC 검증
- `BUILD_ON_WINDOWS_COMPANY.cmd` → `dist\Sunrise File Manager.exe`
- 단일/2~4분할에서 탐색창 위에 포인터를 두고 휠로 상하 스크롤
- 라이트/다크에서 파일 메뉴·명령 버튼·유형/보기 드롭다운 글자 확인
- 숨긴 메뉴의 복제/압축/실행 취소 등이 기존 메뉴와 단축키에서 정상 동작하는지 확인
- 마지막 폴더 및 테마 설정 복원 확인

## 주의
코드 정적 검사는 실제 Windows UI에서 배율별 폰트 렌더링과 입력 이벤트 성공을 확정하지 않습니다.
