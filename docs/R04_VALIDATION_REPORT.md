# Sunrise File Manager v1.8.1 R04 — 개발·검증 보고서

**작성 기준**: 2026-11-13. R03 소스 ZIP(502 항목)을 추출한 후 국소적으로 수정.

## 실제 구현

- 제품 수정 파일 5개
  - `Services/FileListView.cs`: 재귀 검색 유형 필터와 목록 필터의 단일 패스 통합
  - `Features/Explorer/ExplorerPane.ViewAndFiltering.cs`: 조건·정렬·listing generation 키와 정렬된 검색 결과 캐시, 페이지 한정 표시
  - `Features/Explorer/ExplorerPane.NavigationAndWatcher.cs`: 폴더 reload 및 pane 상태 교환 시 캐시 동시 무효화
  - `Features/Explorer/ExplorerPane.PagedSelection.cs`: 검색 조건이 동일하면 전체 결과 선택 시 캐시 재사용
  - `Controls/ExplorerPane.xaml`: WPF 논리 스크롤 옵션 명시, 현재 행/열 가상화 계약 보존
- 신규 검증 자료 2개: `source/tests/test_v181_r04_recursive_ui_cache.py`, `source/tests/UiSmokeTests/R04RecursivePagingAudit.cs`.
- 기존 Windows UiSmoke 진입점 `source/tests/UiSmokeTests/Program.cs`에 선택적 `--r04-recursive-page-only` 옵션 추가. 기본 빌드나 CI에 자동 실행되지 않음.

## 실제 실행한 검증

| 항목 | 개수 | 결과 |
|---|---:|---|
| R04 + R03/R02/R01/EIASS 영향 계약 검사 | 42 | PASS / 하위검사 11 |
| 영향 UI/분할/클라우드 관련 기존 계약 중 기존 기준파일이 있는 시험 | 112 | PASS / 하위검사 22 |
| 역사 R58 검증 문서 별도 연결 후 잔여 문서계약 검사 | 10 | PASS |
| **합계(서로 다른 pytest 테스트 노드)** | **164** | **PASS / 하위검사 33** |
| WPF XAML XML parse | 1 | PASS |
| Windows .NET 10 빌드 및 R04 UiSmoke runtime | — | **미실행** (Windows/dotnet 부재) |
| Windows 100/125/150/175% 시각 검증, 서명된 Installer | — | **미실행** |

**역사 증적 처리**: 개발 소스 ZIP에는 `validation/R17`, `R57`, `R58`의 기존 Windows 게이트 문서가 포함되지 않았다. 이 때문에 처음 실행에서는 참조 파일 누락으로 3 failed/10 errors가 발생했다. 제품 코드 실패가 아니며, 원본 R06의 해당 파일을 읽기 전용 테스트 경로에 임시 연결해 재검사한 결과 위 122개 시험이 PASS. 임시 링크와 과거 검증 문서는 R04 산출물 패키지에서 제외했다. **검증 문서가 실제 Windows 검증이 진행됐다는 뜻은 아님**.

## 남은 검증·위험

- `FileListView.Apply` 변경은 **선택적 마지막 인자**만 추가하므로 기존 C# 호출의 인자 배치가 유지되지만 실제 .NET 10 컴파일 증적은 아직 없다.
- WPF 렌더링·4분할 레이아웃의 DPI별 실제 스크린샷 없음. 기존 `EnableColumnVirtualization=False`를 보존했으며 변경된 `CanContentScroll=True`의 실기 영향만 확인 필요.
- 재귀 검색/캐시 조건과 파일시스템 변경 사이에 UI generation 보호가 있으나 **SMB/네트워크 환경 실측/파일시스템 변경 경합 시험을 완료하지 않음**.
- 실제 체감 속도(p50/p95), 메모리 사용량, Windows UI 응답성은 수치 측정 전이므로 개선율 수치 표시 금지.
- 공식 `latest.json`, v1.8.0 Setup은 변경하지 않으며 Release Gate 전까진 **R04 SOURCE_CANDIDATE / WINDOWS_PENDING / RELEASE_PENDING**.
