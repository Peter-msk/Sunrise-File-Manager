# P18 인수인계 — v1.8.0

## 계약

1. 미리보기: 메인 화면의 우측 `FileInspector` 내부에 접이식으로 배치. 파일 탐색과 동시 사용. 미리보기 OFF 상태에서도 상세정보/메모 상하 배치 유지.
2. 상세정보: 현재 활성 탐색기에서 단일 선택한 파일의 이름/유형/크기/수정일/경로만 표시, 파일 원본 수정 없음.
3. 메모: 기존 선택한 *파일* 1개에 대해 작성. `원본폴더/#memo/{원본파일명}_memo_{yy.MM.dd}_{Environment.UserName}.sunmemo`. 96+40자 컴포넌트 제한, 과장 파일명은 SHA256 접미사로 충돌 위험 저감.
4. 저장: UTF-8 JSON payload를 gzip으로 압축하고 signature `SUNRISE_MEMO_V1\n` 부착, 같은 원본 폴더에서 임시 파일 생성/디스크 flush/원자 교체. 원본 파일은 수정하지 않음. 메모 길이 200,000자 제한.
5. 조회: 선택한 원본과 같은 위치의 #memo 폴더에서 서명·원본명 검사 후 읽음. 1.1MB 압축 파일 / 900KB 해제 상한, 20,000개 열람 목록 상한. 손상 파일은 읽지 못한 개수를 상태에 표시.
6. 파일 전환: 미저장 메모는 저장·폐기·취소 선택. 취소시 가능한 경우 이전 파일 선택 복원. 저장 실패시 편집기 내용 유지.
7. 이력: 날짜/사용자별 메모 보존, 같은 날짜·사용자 재저장시 해당 메모 갱신. 이전 이력에서 현재 사용자 오늘 기록을 덮어쓰는 경우 확인.
8. 저장되는 사용자 세션: `LayoutVersion=18`, `InspectorVisible`, `DockPreviewVisible`; 기존 세션은 우측 패널 기본 켬, 미리보기 기본 끔.
9. 파일 정리 F8, 1~4분할, 탭/드라이브, Windows 연결 앱 열기, 기존 파일 이동/복사 계약 유지. AI 패널은 정보 패널과 충돌하지 않도록 아래에 배치.

## 변경 파일

- 신규 `Services/FileMemoStore.cs`, `Controls/FileInspector.xaml`, `Controls/FileInspector.xaml.cs`
- 수정 `MainWindow.xaml(.cs)`, `Controls/ExplorerPane.xaml(.cs)`, `Services/SessionStore.cs`, `Sunrise.FileManager.csproj`
- 수정 `BUILD_WINDOWS.ps1`, `tests/verify_source.py`, `tests/LogicTests` 및 `tests/UiSmokeTests`.

## 확인한 검증

- Python `source/tests/verify_source.py` → XAML 파싱·중복 이름·이벤트/리소스·원본 정적 계약 PASS (250건).
- Python `source/tests/test_ui_contract.py` → 기존 계약 + 신규 우측 패널/메모 정적 계약 18건 PASS.
- Windows .NET 10 빌드/LogicTests/FileMemoTests/WPF GUI/실 파일 탐색/클립보드/다중 DPI → NOT RUN (Linux 환경, dotnet SDK 없음).
- 기존 `source/reports` 및 `source/docs/baseline_*`의 이전 PASS 문서는 *과거 버전 근거*이며 v1.8.0의 실행 증거로 사용하지 않을 것.
- `TESTED_SOURCES.json`은 v1.8.0 수정으로 `STALE_SOURCE_CHANGED` 표기. Windows의 `BUILD_WINDOWS.ps1 -RunFullTests`가 성공한 뒤에만 v1.8.0 PASS 소스 해시가 생성되도록 함. 이후 동일 해시는 중복 LogicTests 검사 없이 재사용 가능.

## 추가 QA 체크리스트

- Windows 10/11에서 전체 빌드 및 UiSmokeTests 수행 후 GUI 정렬 확인.
- 100%/125%/150% DPI, 760x560/900x650/1440x900, 1/2/3/4분할, F8 패널 조합.
- PDF 스캔/이미지/HWPX 저장된 첫 페이지/미지원 HWP/큰 파일 미리보기 및 빠른 선택 취소.
- 한글/공백/긴 파일명, 날짜 경계, 다른 PC 사용자, 다른 원본 동일 파일명, 메모 손상/권한 거부/읽기 전용/네트워크 폴더.
- 변경 저장 실패 및 취소/닫기/분할 전환에서 메모 손실 없는지 확인.

## 의도된 비구현 범위

- 기존 파일 이름 변경·이동·복제 시 `#memo` 자동 동기화/추적, 메모 충돌 병합/동시 다중 인스턴스 충돌 해결, 클라우드 메모 동기화, 암호화는 미구현.
- `.sunmemo`은 전용 포맷이지 강제적 읽기 접근 통제·DRM·암호화가 아님.
- 구형 `PreviewWindow` 코드는 현재 경로에서 호출하지 않지만, 변경 위험 최소화 원칙에 따라 소스는 보존함.
