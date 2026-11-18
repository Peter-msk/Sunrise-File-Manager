# Sunrise File Manager v1.8.1 R04 — 대용량 탐색·검색 및 UI/UX 안정화 계획

**기준본**: `Sunrise_File_Manager_v1.8.1_R03_SOURCE.zip`; GitHub 개발 브랜치 커밋 `8bc8651890da1b556ee1573669e1cac9dc3bd091`. 2026-11-13.

**범위**: 읽기 전용 목록 표시·검색 캐시·그룹/정렬·현재 페이지 선택·1~4분할 WPF 목록 UI. 외부 폴더 파일과 사용자의 클라우드 데이터를 변경하지 않는다. `TransferService`/`FileActionsService`/`ExplorerUndoService`/EIASS 생성기/테마 토큰/공식 업데이트 계약을 변경하지 않는다.

## 발견·재현 대상

| ID | 기존 구현 | 개선 | 검증 |
|---|---|---|---|
| R04-P1-01 | `ApplyFilterAndSortAsync`가 재귀 검색 결과 `MaxResults` 전체를 매 페이지마다 `FileListView.Apply`로 재필터·재정렬 | 재귀 검색·정렬·유형·카테고리·숨김 조건·listingGeneration이 같은 동안 정렬된 전체 뷰를 1회만 보존, 각 페이지는 최대 2,000개로 `Skip/Take` | 소스 계약 + Windows opt-in UI pagination |
| R04-P1-02 | 현재 pane에서 `snapshot.Where(...).ToArray()` 만든 직후 `FileListView.Apply`가 또 `ToList()` | `FileListView.Apply`의 선택적 `ExplorerSearchFilter` 인자로 결합하여 단일 패스 필터, 추가 임시 배열 제거 | source test 및 공개 API 기존 호출 유지 |
| R04-P1-03 | 재귀 검색 `전체 결과 선택`이 동일한 결과를 재정렬 | 정확히 동일한 cache key에 한해 정렬·필터 캐시 결과 경로만 추출, 다른 조건은 기존 안전한 전체 경로 계산 수행 | 소스/Windows targeted smoke |
| R04-P0-04 | 폴더·새로고침·서로 다른 pane 작업상태 교환에서 캐시 오염 가능 | 원본 검색 캐시와 재귀 정렬 뷰 캐시를 명시적으로 동시 무효화 | source model + Windows refresh |
| R04-P1-05 | WPF 데이터 행 가상화는 현재 `EnableRowVirtualization=True`/열 가상화=False이지만 `CanContentScroll` 명시 없음 | 기존 열 가상화 OFF 승인 계약은 보존하고 파일 목록의 WPF 논리 스크롤(virtualized items) 의도를 명시 | XAML XML/계약 및 `FileScrollRegression` Windows pending |

## 성능 기대 및 한계

- 재귀 검색 N건, P페이지(2,000행 기준)에서 기존 UI 페이지 처리비용 **대략 P회 × O(N log N)** 정렬 + 임시 배열; 변경 후 같은 조건의 페이지 전환은 정렬 1회 + 각 페이지 최대 2,000 참조 투입. 검색 자체의 디스크·NAS 조회 비용, `MaxResults` 제한 및 메타데이터 행 생성 정책은 변경하지 않는다.
- 캐시는 **한 pane의 활성 재귀 검색 한 종류만** 보관한다. 일반 로컬 폴더 목록은 기존 `DirectoryScanner.ReadPageAsync` 2,000행 페이지 정책을 사용하고, 결과 캐시는 refresh 시 해제한다.
- 실제 p50/p95, Windows UI 프레임 지연, 최대 메모리/CPU 개선율은 **미측정**. 속도 향상을 수치로 보증하지 않는다.

## Windows R04 실행 항목 (미실행)

1. Windows 10/11 x64, .NET 10 SDK에서 `dotnet run --project source/tests/UiSmokeTests -- --r04-recursive-page-only <증적폴더>` (증적폴더는 작업 시작 전 별도 생성). GUID 기반 격리 Temp 폴더에서 2,205개 빈 파일만 생성한다.
2. 재귀 검색 첫 페이지 2,000개/다음 페이지 205개/이전 페이지 복귀, 동일 캐시 참조 유지, 정렬 변경·검색어 변경·새로고침 시 캐시 무효화 확인.
3. 기존 `UiSmokeTests --file-scroll-only`, `--split-interaction-only`: 파일 컬럼 수평스크롤, 분할창 전체 프리셋, 키보드/마우스, 정렬·스크롤 변위. **변경 코드와 관련된 항목만** 실행하고 과거 PASS 전부 반복하지 않는다.
4. 실제 Windows **100%, 125%, 150%, 175%** 배율 × 단일/좌우2/2×2 4분할 × 최소/기본 창. 본문·파일명·검색창·상태줄·다음/이전 페이지 버튼 잘림, 클릭 영역, 툴팁/툴바 확인. 증적 PNG 및 DPI/해상도 기록. 이 환경에서는 불가능하여 `PENDING`.
5. 2,205건과 10,000/100,000 건 R52 성능 픽스처 실행은 별도 승인된 테스트 창에 한정. 기존 `V175R52PerformanceFixture`는 실제 Windows에서 가상화·메모리·p95 측정용으로 재사용할 수 있다. 반복 실행/무의미한 benchmark 금지.

## 출시 경계

- `main`, `latest.json`, `notice.txt`, v1.8.0 official Release, 기존 사용자 설정/파일 변경 금지.
- Windows WPF 컴파일/실제 화면 시각 판정/서명된 Setup 검증 없이 `v1.8.1` 정식 출시 판정 금지.
- 다음 우선순위는 **R05 Release Gate**(실제 Windows 실행 및 서명/설치/업데이트)이며 R04의 캐시 로직이 바뀌지 않은 파일 전송 코드는 중복 빌드검사 생략.
