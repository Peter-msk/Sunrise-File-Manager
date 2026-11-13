# Sunrise File Manager v1.8.1 — R03 일반 파일 작업 안전화 실행계획

- 기준: `Sunrise_File_Manager_v1.8.1_R02_SOURCE.zip` / 기존 R02 GitHub 커밋 `14cff52f2c0c7d3f9e06a558d019e1cbb90e0d2e`.
- 우선순위: 다른 드라이브 폴더 이동(P0) → 승인된 정확한 목적지 중복(P0) → 상하위 중복선택 처리(P1) → 기존 Undo/Redo 및 메모 동반 이동 회귀(P0).
- 작업 소유권: `Services/TransferService.cs`만 제품 코드 변경. 기존 `FileActionsService`, `ExplorerUndoService`, `FileMemoStore`, `DirectoryScanner`, UI 테마 및 업데이트 표준은 수정하지 않음.
- 공식 배포 범위: **개발 후보만**. `main`, 공식 v1.8.0 Release, `latest.json` 및 `notice.txt` 변경 금지.

## 검토 결과와 개선 사항

| 게이트 | 위험·증상 | 조치 | 검증 및 잔여 위험 |
|---|---|---|---|
| R03-P0-A | cross-volume directory move: `VerifySourceUnchanged` 이후 `Directory.Delete(source, recursive:true)`로 늦게 생성된 파일까지 삭제 가능 | 계획에 포함된 파일만 개별 재검증/정리하고, 하위·상위 폴더는 `recursive:false`만 사용 | 원본에 변형이 있으면 실패·대상 복사본 유지·원본/대상 경로 제공. 삭제 단계 시작 후 부분 삭제와 외부 경쟁 업데이트 위험은 Windows 실기에서 추가 검증 필요 |
| R03-P0-B | 정확한 출력 경로 `exactDestinations`가 서로 중복되더라도 첫 작업부터 시작 | 사전 계획 단계에서 대소문자 비구분 `HashSet`으로 모든 예약 경로 검사. 중복이면 첫 쓰기 이전에 `IOException` | 중복 경로 test source + .NET test 시나리오 |
| R03-P1-C | 최상위 1,000개 선택 중 폴더/하위 항목의 중복 선택 O(n²) 검사 | 정상화한 전체 경로 집합과 각 항목의 부모경로 순회 `O(n * depth)`로 변경 | 디렉터리 트리·중복 선택 회귀 시나리오 |
| R03-P0-D | 이름 변경/Undo/Redo/메모: 기존 이력 손상·사용자 데이터 충돌 | 로컬 오너 변경 범위 외이므로 기존 구현 유지, 별도 자동 실행 테스트로 충돌·이력 보존 확인 예정 | Python source contract PASS. Windows C# 테스트 작성·미실행 |
| R03-P1-E | 2,000~20,050 목록·검색/정렬 성능 | `DirectoryScanner`가 이미 2,000행 페이지 가상화/상단 선택 유지 경로를 소유해 본 단계에 추가 리팩터링하지 않음 | 기존 R50/R52 기준 PASS 증적 재사용. 대량 목록 실제 UI 성능은 Windows 실기 필수 |

## 데이터 안전 경계

1. 복사 완료 전에 원본 삭제 금지. 완료 후에도 원본의 **승인된 스냅샷 항목**만 정리.
2. 계획에서 예측하지 못한 파일이 등장하면 비재귀 폴더 삭제가 실패하고, 이미 검증된 대상 복사본은 제거하지 않음. 새 파일/메모는 삭제하지 않음.
3. 정리 시작 후 파일 단위 취소를 추가하지 않음. 이 단계에서 취소하면 의도적으로 불완전한 원본을 유발하므로 기존 단일 정리 단계의 완료 또는 명시적 실패 정책 유지.
4. 사용자 설정·기존 정식 Setup·Control 업데이트 메타 데이터 변경 금지. 회사 NAS·SMB DLP/ACL 우회 금지.
5. Windows에서 파일 동일성(identical size/time but replaced by another process)과 마지막 검증 이후 쓰기 경쟁까지 완전 차단했다고 주장하지 않음. Windows 핸들/잠금 및 실제 SMB 환경 시험이 필요한 잔여 위험.

## R03 검증 계획

### 사전 Source Gate
- `pytest` 신규 R03 source contract 8건 및 변경영향 기존 테스트 실행.
- C# LogicTests `--v181-r03-transfer-safety` 전용 경로 추가. 테스트는 GUID 임시 폴더에만 쓰기.
- 기존 R02·R01 PASS 증적은 수정하지 않은 경로에서 재사용.

### Windows 미실행 Gate
- `dotnet run --project source/tests/LogicTests -- --v181-r03-transfer-safety`
- 2개 원본 → 동일 정확한 출력 경로 지정: 첫 쓰기 전 거부.
- cross-volume 검증 경로의 디렉터리 이동 성공 및 원본 비재귀 정리.
- 복사 중 새 파일 생성: 원본/신규파일 보존 + 실패 보고 + 대상 복사본 확인.
- 이동 충돌 후 Undo 실패, 대상 변화 없음, 충돌 해소 후 Undo/Redo 정상.
- NTFS/SMB/ReFS ACL, 재분석 지점, 파일 잠금 및 최대 1,000개 이동(Windows 실기).
- `BUILD.cmd --release --no-pause`와 회사 인증서 서명/인스톨러/Control 호환 검증은 Release Gate에서만 실행.

## 후속 순서

- **R04**: 대용량 목록·검색·정렬에서 현행 캐시/가상화 회귀 및 DPI 100/125/150/175% UI 시각검증, 실제 Windows WPF 빌드.
- **R05(Release Gate)**: Windows 설치/기능 실행, 코드서명/SHA256/기존 설정 복구, 승인 후에만 `v1.8.1` stable 반영.
