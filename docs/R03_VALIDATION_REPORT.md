# Sunrise File Manager v1.8.1 R03 — 실행 및 인수인계 검증보고

## 적용

- 기준 소스: v1.8.1 R02 ZIP (498 파일)
- 제품 코드 수정 1개: `source/Sunrise.FileManager/Services/TransferService.cs`.
- 신규 C# 단독 테스트: `source/tests/LogicTests/V181R03TransferSafetyTests.cs`.
- 기존 C# 테스트 진입점 한 곳 수정: `source/tests/LogicTests/Program.cs`.
- Python 소스 계약 8개: `source/tests/test_v181_r03_transfer_safety.py`.
- 변경 사유: 복사 이후 원본 폴더 재귀 정리 위험, exactDestinations 이름 중복 사전 검사 누락, 다중 선택 O(n²).

## 실제 실행 증적 — 2026-11-13

| 검증 | 범위 | 결과 |
|---|---|---|
| 신규 R03 계약 | `source/tests/test_v181_r03_transfer_safety.py` | **8 PASS** |
| 변경영향 합산 | 신규 R03 + R20 batch copy + R22 network transfer + R42 partial outcomes + R56 report consistency + R02 EIASS rename + R160 memo | **60 PASS, 41 subtests PASS** |
| 과거 R20 검증 파일 참조 | 소스 ZIP에 없는 `validation/R20/VERIFY_R20_WINDOWS.ps1`를 이전 R06 기준본에서 읽어 임시 심볼릭 링크로 참조 후 테스트; 패키지에는 불포함 | **원인 = 역사 검증 문서 미포함; 60 PASS 상태** |
| 신규 C# Native LogicTests | 전용 `--v181-r03-transfer-safety` 코드 작성 | **Windows/.NET 10 부재로 미실행** |
| Windows 설치본·DPI/경합 시험 | Windows 실행/코드서명/실제 장치 요구 | **미실행 / PENDING** |

## 안전성·제약

- `Directory.Delete(plan.Source, recursive:true)`는 운영 전송 경로에서 제거. 스테이징 폴더(`.sunrise-staging-{guid}`)에 대한 고유 임시경로 정리만 재귀 처리 가능하며 사용자 원본과 혼동 금지.
- 계획 파일 목록 정리 중 일부 항목의 삭제 후 새 항목/권한 변화가 발생할 수 있어 **전체 원본 복원 보장/원자적 이동**을 주장하지 않는다. 이 경우 검증된 목적지 복사본을 유지하고 원본·대상 경로를 모두 표시한다.
- Windows에서 파일 ID 재검증/핸들 경합/ACL 환경이 미실행이므로 데이터 안전성 최종 판정 불가.
- R02/R01의 PASS 데이터는 제품 변경 영향이 없으면 재실행하지 않는다. `main`, v1.8.0 설치파일, `latest.json`, `notice.txt` 변경 없음.

## R03 상태

**PASS_STATIC / WINDOWS_DYNAMIC_PENDING / RELEASE_GATE_PENDING**
