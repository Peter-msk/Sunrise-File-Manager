# Sunrise File Manager v1.8.1 R02 — 변경영향 검증 기록

검증 기준: 사용자 제공 v1.8.1 R01 소스 495개 파일을 작업본으로 복사해 필요한 제품 C#/XAML 소스 5개와 네이티브 회귀테스트 1개만 수정. 작업 계획/테스트 문서를 추가. 사용자 원본, 기존 v1.8.0 공식 릴리즈, `latest.json`/`notice.txt` 미수정.

## 실제 실행 (2026-11-13)

| Gate | 범위/도구 | 결과 |
|---|---|---|
| R02 타깃 정적 계약 | `pytest source/tests/test_v181_r02_eiass_rename_stability.py` | 8 PASS |
| R01/EIASS 영향 계약 | R02 + R01 외부 OSS + 운영생성 + EIASS 기존 경로/메뉴 | 총 29 PASS (위 8 포함) |
| Explorer·배치·UI 주변회귀 | `test_v170_r02_official_a_shell.py`, `test_v175_r42_batch_partial_outcomes.py`, `test_v175_r57_final_ui_gate.py`, `test_v179_dev6_wpf_review_resilience.py` | 49 PASS / 22 subtests PASS |
| Legacy validation 자료 의존성 | 소스-only ZIP에 역사 문서가 없어 R57 테스트 2개가 FileNotFoundError. 원 R06 `validation/` 문서를 작업 폴더에 임시 symlink한 뒤 다시 시험 | 원인 = 소스 패키지 구성. 재실행 후 49 PASS |
| WPF XAML 구문 | ElementTree 파싱 / 신규 DataGrid ElementStyle 및 속성 | PASS |
| .NET C# 빌드 / Windows 실행 / 파일시스템 실기 | 현행 환경 Windows/.NET SDK 부재 | **미실행 / PENDING** |
| 최종 Signed Setup + 업데이트 | Windows 기업 서명·InnoSetup 미가용 | **미실행 / PENDING** |

## 파일 보존 및 배포 상태

- 일괄 이름 변경은 `FileActionsService.BatchRenameFilesSafely`의 기존 `MoveEntry` + 2단계 임시이름/원상복구를 사용한다. 신규 삭제/덮어쓰기 동작 없음.
- 취소 복구는 자동화된 C# Windows UI/기능 테스트 코드를 보강했지만, 동적 PASS로 표시하지 않는다.
- 기존 정식 v1.8.0 유지. 후보 v1.8.1을 stable/자동업데이트로 게시 금지. 이전 소스 PASS는 사용자 명시 기준에 따라 입력이 동일한 경우 빌드에서 `REUSED_PASS` 처리.
