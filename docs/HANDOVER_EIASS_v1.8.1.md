# Sunrise File Manager v1.8.1 — EIASS 운영 생성 작업 인수 / 검증 상태

- 기준: 2026-11-13. 정식 운영 기준본: v1.8.0 R06 (Drive SHA-256 77d357ae4a6b0b07fe0e7dcc7f504a8c363120a40268836a113e7a237c3cd707)
- 작업 시작 소스: R06에 R07 CASE 경로 보완을 적용한 기준점
- v1.8.1 코드는 신규 후보이며 **Windows Release Setup / 코드서명 / 설치 / 실기 미검증**. 공식 배포 `latest.json` 및 기존 v1.8.0 GitHub 릴리즈 변경 금지.
- 회사 전용 폴더 규칙: 없음. 프로그램에 정의된 '기본안' 경로를 기준으로 작업. 회사·발주처 미승인 초안 폴더는 운영 생성 대상에서 제외.

## 코드 변경

1. `EiaWorkspaceNativeDirectoryWriter`: Windows 로컬 NTFS/ReFS에서 핸들로 기준 폴더 고정, NT 상대경로 생성. 파일/연결지점 거부. NAS/SMB 실패 차단.
2. `EiaWorkspaceProductionWriter`: 사업별 승인 문구, 신규 경로 재점검, 읽기 전용 원본 계획과 새 관찰 지문 비교, 부모 확인, 신규 폴더만 `FILE_CREATE`, 기존 폴더 `FILE_OPEN`, 결과 구분 및 취소.
3. `EiaWorkspaceExecutionReviewWindow`: 재점검 후 생성·중단, 사용자 명시 최종 확인, 진행상태/부분결과, 재조회 필요 안내.
4. 릴리즈 버전 `1.8.1` 소스 메타데이터 및 빌드용 버전파일 일치. `release/NOTICE_BODY_1.8.1.txt` 후보 생성.
5. 기존 PDF 일괄 파일명 변경 코드는 그대로 유지. 기존 파일을 삭제하지 않지만 파일 경로 변경은 사용자 작업이므로 최종 실행 검증 대상.

## 완료 검증

- Python 계약검사: `python -m pytest -q source/tests/test_v181_eiass_production_contract.py source/tests/test_v180_r05_eiass_menu.py` (12 PASS)
- XML 파싱: MainWindow, EIASS 계획·검토 XAML, app.manifest, csproj (PASS)
- 과거 v1.8.0 버전 불변 및 '운영 생성 금지' 가정을 검사하는 테스트는 v1.8.1의 현행 계약과 불일치하므로 역사 기록으로 격리. 그 결과를 현행 실패/성공으로 집계하지 않음.

## 반드시 완료할 검증 (현재 미수행)

- Windows x64 .NET 10 실제 C# 컴파일 및 빌드: BUILD_GATE_PENDING
- `EiaWorkspaceProductionSmoke`: 독립 Windows NTFS 테스트; 신규 생성/재실행/잘못된 승인/원본 불변 확인: WINDOWS_DYNAMIC_PENDING
- NTFS ACL 제한, 링크/경합, 생성 중단 후 결과조회, Windows UI 다중 DPI: WINDOWS_DYNAMIC_PENDING
- NAS/SMB: 현행 코드가 의도적으로 생성 차단. 지원 기능으로 공표 금지.
- Inno Setup `BUILD.cmd --release --no-pause` → 설치 EXE/주 EXE 코드서명 확인 → 생성/rename 실사용 → 설치·업데이트·재설치/복구 → 최종 SHA256: RELEASE_GATE_PENDING
- 최종 Setup이 확인되지 않았으므로 GitHub 릴리즈 v1.8.1 업로드 및 `latest.json` 갱신은 수행하지 않음.

## 빌드 경계

`BUILD.cmd --release --no-pause`는 Windows 빌드 PC에서만 실행. 이전 PASS로 확인한 무관한 검사 반복 금지. `installer/RELEASE_VERSION.txt=1.8.1` 및 `release/NOTICE_BODY_1.8.1.txt`가 필요.

공식 기존 v1.8.0 Setup: `Sunrise_File_Manager_1.8.0_Setup.exe` / SHA-256 `30767327b56ced5267c8630a9b49b09ae86e472073377e5806a06927a4ec9d9a`. 출시 전까지 그대로 유지.

## R02 (2026-11-13) — 공개 오픈소스 벤치마킹 이후 대상 변경 안정화

- **기준**: R01 `9377700bbbb52224b3588ffb8336b6d08daead94`. 전역 테마, installer/updater, 다른 메뉴 및 원본 v1.8.0 릴리즈는 수정하지 않음.
- **미리보기**: `EiassRenameDialog`의 코드·파일명 중복 검사에서 반복 목록 전체검색 제거, 대소문자 비구별 경로 집계 유지. 큰 목록의 DataGrid 가상화 및 새 파일명 말줄임/전체값 Tooltip 제공.
- **목차 일치**: `EiassFileNameService.MatchContents`를 유일키/목차위치 딕셔너리로 계산하여 중복/빈값 거부 의미 보존.
- **실제 파일명 변경**: 기존 `RunLocalFileJobAsync`로 백그라운드 실행, MainWindow 기존 종료/업데이트 방어 재사용. 2단계 스테이징의 파일 단위 경계에서만 취소 인식. 중단 시 복구 실패가 없으면 `OperationCanceledException`, 복구 실패면 `IOException`으로 보존 경로 제공. 파일 콘텐츠 변경/삭제 없음. 늦게 도착한 진행 이벤트는 완료 메시지 덮어쓰지 않음.
- **변경영향 테스트**: Python 소스 계약·목차 로직 모델 및 기존 EIASS 테스트 29 PASS. 관련 과거 UI/파일 조작 계약 49 PASS, 22 subtests PASS. R01 소스 패키지에서 빠진 역사 Windows 검사 문서 2개는 원래 R06 기준본의 읽기 전용 문서를 임시 링크한 뒤 통과, 새 소스 패키지에 임의로 역사 검증 자료를 삽입하지 않음.
- **신규 Windows 실기 시나리오**: UiSmoke의 파일명 변경 후 비동기 완료 대기, 1,000 제목·2,000 목차, stage/publish 중 취소시 원본/`#memo` 복구 및 임시파일 부재. 구현됐으나 **미실행**.
- **미검증 자격**: .NET 10/WPF Windows 빌드, 실제 NTFS 파일 잠금/ACL/취소, Windows 100–175% DPI, Authenticode signed installer, 공식 v1.8.1 출시는 모두 `PENDING`.
- **다음 작업**: `docs/STABILIZATION_PLAN_v1.8.1_R02.md`의 R03/R04/REL Gate 및 빌드 후 사용자 실기 결과 기준.

## R03 (2026-11-13) — 일반 파일 이동/복구 안전성 및 사전 검증

- R02 코드를 기준으로 `TransferService`의 verified cross-volume **폴더** 이동 후 `Directory.Delete(plan.Source, recursive:true)` 위험을 제거했다. 이제 최초 열거한 파일을 개별 재확인해 제거하고, 하위/상위 폴더는 `recursive:false`로만 정리한다. 원본에 새 파일이 들어오면 삭제 실패로 보고하고 완성된 대상 복사본은 유지한다.
- 사용자가 검토한 `exactDestinations`에 동일한 목적지 이름이 2건 이상 지정된 경우, 첫 파일 처리 전에 전체 작업을 거부한다. 선택한 상위 폴더/하위 항목의 충돌 확인은 경로 집합으로 최적화했다.
- 기존 `FileActionsService`, `ExplorerUndoService`, `FileMemoStore`의 로컬 Owner 계약과 기존 UI 테마는 변경하지 않고 영향 테스트로 재확인했다.
- 실제 실행: R03·R20·R22·R42·R56·R02·R160 Python 변경영향 60 PASS / 41 하위검사 PASS. R20 역사 검증 문서는 R06 ZIP에서 읽어 임시 연결했으며 R03 소스에는 복제하지 않았다.
- Windows 테스트용 `LogicTests --v181-r03-transfer-safety`는 source ZIP에 작성·포함했으나 .NET 10/Windows 부재로 실행하지 못했다. 실제 NTFS/SMB 파일 경합, 서명 Setup 및 v1.8.1 공식 릴리즈는 **PENDING**. 현행 공식 v1.8.0 배포 유지.
- 상세: `docs/STABILIZATION_PLAN_v1.8.1_R03.md` / `docs/R03_VALIDATION_REPORT.md`.

## R04 (2026-11-13) — 재귀 검색 페이지·분할창 성능 및 UI 안정화

- 기준 R03 `8bc8651890da1b556ee1573669e1cac9dc3bd091`; 제품 코드 변경 5개 파일로 한정.
- 재귀 검색 2,000행 페이지를 넘길 때 전체 검색 결과 재정렬을 반복하지 않도록 조건·정렬·listing generation 키의 단일 sorted view 캐시 추가. 새 폴더/refresh/pane 상태교환 시 원본 검색·정렬 캐시 모두 무효화.
- `FileListView.Apply` 선택적 유형 필터 인자로 전체 배열 중복 생성을 제거했고, 전체 결과 선택도 동일 키의 정렬 결과를 재활용한다.
- 기존 승인 UI: 1~4분할, 파일명 인라인 편집, 가로 스크롤, `EnableRowVirtualization=True`, `EnableColumnVirtualization=False` 유지. WPF `ScrollViewer.CanContentScroll=True`를 명시.
- 소스/계약 Python 검사 총 164 PASS(하위검사 33). R17/R57/R58 역사 문서는 R06 원본에서 임시 읽기 전용 참조 후 재검증, R04 패키지에 미포함.
- 신규 Windows opt-in `UiSmokeTests --r04-recursive-page-only`는 2,205개 GUID 임시파일만 생성해 페이지 이동/캐시/정렬/검색어/refresh 무효화를 확인하도록 작성. **실제 Windows에서는 아직 미실행.**
- R04 상세: `docs/STABILIZATION_PLAN_v1.8.1_R04.md`, `docs/R04_VALIDATION_REPORT.md`.
- 다음은 Windows x64 .NET 10 / DPI 100·125·150·175% / installer / code signing / 업데이트 검증으로 구성된 R05 Release Gate. 기존 `latest.json`과 v1.8.0 안정 배포 유지.
