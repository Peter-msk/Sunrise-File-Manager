# Sunrise File Manager v1.8.2 — 독립 우측 미리보기 열

## 요청 및 확정 배치

- 파일 탐색 영역: 메인 창 좌측, 기존 단일/2/3/4 분할 유지
- 상세정보·메모: 별도의 우측 중간 열, 위쪽 상세정보 / 아래쪽 `.sunmemo` 편집기
- 미리보기: **항상 가장 오른쪽의 독립된 전체 높이 열**. 상세정보나 메모와 같은 열 또는 상하 분할 영역을 사용하지 않음
- 미리보기와 상세정보·메모의 표시/숨김은 서로 독립적인 두 토글
- 미리보기 열 닫기 버튼은 미리보기만 닫음
- 각 우측 열의 수평 크기 조절은 독립된 GridSplitter로 처리. 세션 종료 전 표시 여부는 기존 AppSession 필드 유지
- F8 AI/파일 정리 패널: 우측 열이 하나라도 열려 있으면 하단 배치, 두 우측 열이 닫혔고 충분히 넓으면 기존 우측 AI 도킹 유지

## 실제 소스 변경

| 파일 | 변경 |
|---|---|
| `Sunrise.FileManager/MainWindow.xaml` | `FileAreaShell` 내부에 탐색+상세/메모, 메인 Grid 바깥쪽에 `PreviewPane` 배치. 독립 열 너비와 분리선 구성 |
| `Sunrise.FileManager/MainWindow.xaml.cs` | 선택 파일을 두 패널에 각각 전달, 독립 미리보기 토글/종료 및 컬럼 너비 갱신, 미저장 메모 보호 유지, 세션 레이아웃 버전 19 |
| `Sunrise.FileManager/Controls/FileInspector.xaml[.cs]` | 기존 혼합 미리보기 영역 및 렌더링 로직 제거; 파일 상세정보와 메모 기능만 유지 |
| `Sunrise.FileManager/Controls/FilePreviewPanel.xaml[.cs]` | 종전 미리보기 페이지/PDF/이미지/텍스트 렌더링 로직을 독립 UserControl로 이동; 자체 닫기 버튼/선택 전환/비동기 취소 구현 |
| `source/tests/UiSmokeTests/Program.cs` | PreviewPane과 FileInspector의 실제 시각적 상하관계/독립 토글/선택 동기화 및 화면 캡처 검증 추가 |

## 독립 토글 동작 계약

1. 정보·메모 ON, 미리보기 ON: **탐색 영역 | 정보·메모 | 미리보기** 3영역.
2. 정보·메모 OFF, 미리보기 ON: **탐색 영역 | 미리보기**; 미리보기는 여전히 가장 오른쪽 독립 열.
3. 정보·메모 ON, 미리보기 OFF: **탐색 영역 | 정보·메모**; 메모 편집 상태 유지.
4. 두 패널 OFF: 탐색 영역만 남음.
5. 미리보기 OFF 시 진행 중 렌더링 취소. 파일 선택은 활성 탐색기를 따라감.
6. 상세정보·메모 OFF 시 미저장 메모는 저장/버리기/취소 확인 절차 유지.
7. 패널 간 파일 이동과 이름 변경에 따른 메모 이동·이름 변경 로직은 v1.8.1 소스를 그대로 유지함.

## 검증 현황

- PASS: 106건 집중 XAML·이벤트·독립 레이아웃 정적 검사.
- PASS: 26건 기존 UI·메모·파일정리 회귀계약 검사.
- PASS: 269건 전체 소스/XAML 연결 정적 검사.
- NOT RUN: .NET 10 Windows WPF 컴파일, C# UI 자동 검증, 실제 Windows DPI 100/125/150% 육안 검토. Linux authoring 환경에 `dotnet` 및 Windows GUI가 없음.
- Windows 최초 빌드: 루트 `BUILD_ON_WINDOWS.cmd` 실행. **변경 소스는 이전 PASS 증거 재사용 금지**. 빌드 후 `powershell -NoProfile -ExecutionPolicy Bypass -File source/VERIFY_UI_WINDOWS.ps1`을 별도 실행해 `separate-preview-rightmost.png`, `UI_RESULT.json` 확인.

## Windows 수동 수락 기준

- 상세정보·메모 열에서 미리보기 요소가 전혀 보이지 않아야 함.
- 미리보기 ON 시 최우측에 전체 높이 열이 나타나고 파일 선택에 반응해야 함.
- 미리보기를 끈 상태에서도 상세정보·메모 표시 여부가 바뀌지 않아야 함.
- 상세정보·메모를 끈 상태에서 미리보기를 켜고 끌 수 있어야 함.
- Ctrl+S, 메모 확인창, 메모 파일 저장, 파일 이동·이름 변경 후 `#memo` 동반 처리가 회귀되지 않아야 함.
- 단일/2/3/4 분할 + 미리보기 열 조합에서 탐색 영역을 계속 사용할 수 있어야 함.
- 독립 두 분리선 끌기와 최소화/최대화, 100/125/150% 확대 배율에서 경계선·미리보기 버튼이 겹치지 않아야 함.
