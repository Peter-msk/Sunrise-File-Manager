# Sunrise File Manager v1.8.2 / UI Audit R03

## 실제 실행 캡처 관련 사실관계
본 수정은 Linux 소스 편집 환경에서 진행되었음. Windows용 .NET 10 SDK와 WPF 런타임이 없어 Windows GUI를 실행하거나 실제 윈도우 캡처를 생성하지 못했음. `Windows 실행 PASS`, `빌드 완료`, `실행창 캡처 완료`로 표기하면 안 됨.

## 코드에서 확인·수정한 레이아웃 문제
1. `WorkspaceShell`의 네 번째 `AiColumn`과 두 번째 `AiRow` 구조로 인해 파일 정리(F8)가 메인 3열 구성에 간섭할 수 있었음. 이제 파일 정리 영역은 탐색기 바로 아래에서만 펼쳐짐. 미리보기는 최우측 단독 열 전체 높이를 유지함.
2. 상세정보·메모 열은 파일 정리(F8)와 공간을 공유하지 않고 전체 높이를 유지함.
3. `GridSplitter` 조절 뒤 `ColumnDefinition.Width.Value`를 픽셀로 가정하던 계산을 실제 측정 폭(`ActualWidth`)과 `WorkspaceViewport.ActualWidth` 기반으로 수정함.
4. 크기 조절 시 레이아웃 값이 1px 이하 변하면 다시 쓰지 않도록 하여 반복 레이아웃 갱신을 줄임.
5. 기존 단일/2/3/4분할, 파일 작업, 메모 저장/이동, 기존 보기 방식, Windows 탐색기형 버튼 및 정리 기능은 유지함.

## 사용자 PC에서 실제 WPF 캡처 얻는 방법
1. `BUILD_ON_WINDOWS.cmd` 성공 확인.
2. 최상위 `CAPTURE_UI_WINDOWS.cmd` 실행.
3. Windows UI가 실제 WPF로 렌더링된 이미지·검증 결과가 `source/ui-check-results/날짜-시간/`에 생성되고 `source/WINDOWS_UI_SCREENSHOTS.zip`으로 묶임.
4. 캡처 ZIP을 제공하면 정렬·DPI·버튼 겹침 등을 실제 픽셀 기준으로 검토할 수 있음.

## 신설 실제 Windows UI 검증
- F8 ON + preview ON + info/memo ON: 미리보기 및 상세정보 열이 전체 높이를 유지하고, 파일 정리 패널은 좌측 파일 탐색기 아래에 나타나는지 검사 후 `preview-info-memo-f8.png`로 캡처함.
- 최소 창 너비: 미리보기/정보 열이 분리되어 있으며 폭의 하한을 유지하는지 검사 후 `preview-min-window-width.png`로 캡처함.
- 기존 `separate-preview-rightmost.png`, 단일/2/4분할별 캡처도 계속 생성함.

## 검증 한계
정적 XAML/소스 계약 테스트만 Linux에서 실행 가능. Windows 컴파일, 실제 WPF 실행, 모니터별 DPI 100/125/150% 시각 검사는 Windows PC에서 수행 필요.

## 원클릭 Windows 빌드+캡처
`BUILD_AND_CAPTURE_WINDOWS.cmd`는 새 Windows 빌드 성공 후 UI 테스트 프로그램을 실행하여 실제 WPF 렌더링 결과를 압축합니다. 테스트 실패 시 성공으로 표기하지 않습니다. Windows에서만 실행 가능합니다.
