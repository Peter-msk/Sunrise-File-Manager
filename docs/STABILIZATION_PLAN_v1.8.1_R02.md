# Sunrise File Manager v1.8.1 — 코드·UI·UX·기능 안정화 실행계획 (R02)

- 기준본: v1.8.1 R01 후보 ZIP (`Sunrise_File_Manager_v1.8.1_R01_SOURCE.zip`), GitHub `feature/eiass-v1.8.1-activation` 커밋 `9377700bbbb52224b3588ffb8336b6d08daead94`.
- 조사 근거: `docs/EXTERNAL_GITHUB_BENCHMARK_R01.md`에 기재된 Microsoft PowerToys / Files / dotnet CommunityToolkit / WPF UI / MahApps.Metro 등 공개 저장소의 패턴. 외부 소스 코드·디자인 에셋은 복사하지 않는다.
- 목표: 기존 v1.8.0 파일 기능·UI 현행계약·업데이트 포맷·사용자 원본을 보존하면서, v1.8.1 EIASS 작업에서 발견된 병목·UI 응답성·중간 취소 위험을 먼저 제거한다.
- 배포 자격: 작업본의 **source/pre-build 검증**과 **실제 Windows 서명 설치형 정식 릴리즈**를 엄격히 구분한다. v1.8.0 `latest.json`/`notice.txt`/기존 설치파일 유지.

## 위험도 및 작업 우선순위

| ID | 우선순위 | 대상 / 문제 | 조치 및 재사용 범위 | 합격기준 | 현재 단계 |
|---|---|---|---|---|---|
| R02-01 | P0 | EIASS 문서명 미리보기: 행마다 전체 이름·코드를 다시 세어 1,000건에서 O(n²) | `EiassRenameDialog.Rebuild`의 중복 코드/대상 충돌을 로컬 Dictionary 집계로 변환, 나머지 규칙과 원본 유지 | 기존 중복번호·동일 대상·기존파일 충돌·원본 누락 판정 유지 | **R02 구현** |
| R02-02 | P0 | 목차 정렬: 각 항목의 전체 목차 재검색 O(n²) | `EiassFileNameService.MatchContents`를 고유키+최초 위치 사전으로 변경. 정규화(FKC), 중복/공백 미매칭 유지 | 기존 예제+1,000/2,000건+중복/비매칭 동작 동일 | **R02 구현** |
| R02-03 | P0 | EIASS 1,000개 파일명 변경이 UI 스레드를 장시간 점유 | 기존 `RunLocalFileJobAsync`와 보호 상태 이벤트를 그대로 재사용. 새 공통 실행기·외부 프레임워크 없음 | 작업 중 메인 창 응답, 중복 실행 금지, 종료·업데이트 차단, 완료 후 Ctrl+Z 활성 | **R02 구현** |
| R02-04 | P0 | 중간 종료/취소 시 이름 변경 중간상태 | 2단계 스테이징을 유지하고 파일 단위 전후에만 취소 체크. 완료 전 취소되면 기존 복구 알고리즘 실행. 오류·부분 복구 실패는 엄격히 분리 | stage 1건 후 취소, publish 1건 후 취소, #memo 동반 복구, 임시파일 누락 검사 | **R02 구현 / Windows 실행 대기** |
| R02-05 | P1 | 긴 PDF 목록·경로 표의 렌더링 | 개별 UI에 DataGrid 행/열 가상화, 긴 이름 말줄임·툴팁 | 100/125/150/175% DPI에서 팝업·셀·버튼 클리핑 없음 | **R02 구현 / Windows 시각검증 대기** |
| R03-01 | P0 | 일반 파일 복사/이동/이름변경 충돌 및 실패 | 실제 `FileActionsService`, `TransferService`, Memo/Undo 오너의 충돌·실패 경로 개별 감사. 기존 PASS 회귀 재사용 | 대상 덮어쓰기 없음, 복구 경로 노출, 사용자 데이터 보존, 기존 Undo 안정 | **후속 단계** |
| R03-02 | P1 | 목록 2,000개 이상 로딩과 검색·정렬 | `DirectoryScanner`/ExplorerPane 가상화·취소·DPI 영향분석 후 LOCAL OWNER만 수정 | 1/2/4 분할에서 프리즈·선택 소실·메모리 비정상 증가 없음 | **후속 단계** |
| R04-01 | P1 | 전체 UI/UX 현행계약 정합성 | 실기 Windows 100·125·150·175%에서 Explorer/EIASS, 키보드, 대화창, 최소 창 확인 | 승인된 CURRENT_UI_CONTRACT 및 실제 사용자 행동이 동일 | **Windows 대상 환경 대기** |
| REL-01 | P0 | 실제 공식 Windows 출시 검증 | .NET10 clean build, 설치, 코드서명/해시, NTFS/ReFS 실제 폴더 생성·취소·ACL·잠금, 설치/업데이트/복구 | 미검증 Gate 0건 + 서명 Setup + 실제 실행 증적 | **대기 / 미출시** |

## 원본·호환성 안전 경계

1. 기존 파일에 대한 자동 삭제·내용 변경·덮어쓰기 금지. 파일명 변경은 사용자 명시 확인 후에만 진행한다.
2. 취소는 파일 하나의 메모 동반 이동이 끝난 뒤에만 처리한다. 취소 중 원본 복구 실패 시 성공으로 표시하지 않고 남은 경로를 명시한다.
3. 다른 메뉴/공유 테마/업데이트 프로토콜 수정 금지. 메뉴·테마는 현행 UI 계약 유지, 신규 패키지 의존성 0.
4. 사전 PASS 체크포인트는 입력이 같은 범위에서 재사용하고 변경 영향 테스트만 재실행한다. Windows 빌드에서 동일 정적검사 재반복 금지.
5. 개발 검증은 테스트 대상 및 실제로 실행한 항목만 PASS 기재. .NET/WPF 동적은 Linux/정적 테스트로 대체 불가.

## 다음 Windows 빌드의 조건부 필수 시나리오

- EIASS 1/100/1,000 PDF 변경 전 미리보기, 충돌·동일명·대소문자·한글/특수문자·목차 중복·비매칭.
- #memo 있는 두 파일 이름 교환, 잠긴 파일 중간 실패, 스테이징·게시 단계 중 취소 후 원본/메모/Undo 검증.
- 작업 중 창 종료/자동업데이트 요청 → 기존 MainWindow의 취소 요청 및 종료 방어 확인.
- 최소 크기 및 Windows DPI 100/125/150/175%: 표 가상화, 입력·가로스크롤·실행 버튼, 툴팁.
- 1.8.0 → 1.8.1 업그레이드/설정 보존/복구/공식 서명 확인 후에만 stable 게시.

## 작업 현황 판정

- R02 구현 범위: EIASS PDF 명칭 미리보기/목차 매칭·기존 비동기 작업기 연결·취소 복구 경로·로컬 UI 가상화.
- R02 Windows 권한: 이 작업 세션에 Windows .NET10/WPF 및 Authenticode signing 환경이 없으므로 **`WINDOWS_DYNAMIC_PENDING`**, `BUILD_GATE_PENDING`, `RELEASE_GATE_PENDING`.
