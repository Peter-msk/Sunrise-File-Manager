# P14 작업 기록·검증형 복구 인수인계

- 버전: v1.4.0 / P14
- 기준: v1.3.0 / P13 원본 보존
- 적용 시방서: 범용 코드작성 시방서 v1.3 FINAL
- 상태: 구현·자동검증 완료, Windows 실기 게이트 대기

## 구현

1. `AiJournalStore.cs`: 파일별 계획·실행 의도·완료·오류·되돌림 상태를 암호화된 추가 전용 기록으로 관리합니다. Windows DPAPI는 현재 사용자 범위이며 명령행·설정에 키를 넣지 않습니다. 각 레코드의 순서/이전 해시, 단계별 상태전이, 최대 크기, 작업 ID를 검증합니다. 매 의도/결과를 Flush(true)로 저장하며 기록이 없거나 실패하면 다음 파일 작업을 진행하지 않습니다. 중복 기록 생성은 CreateNew로 거부합니다.
2. `AiRecoveryService.cs`: 중단 상태 검사와 되돌리기를 담당합니다. 기록과 실제 파일의 존재/해시/메타데이터를 대조하고 이름 변경·이동의 역방향 작업만 기존 FileActionsService/TransferService로 실행합니다. 미리보기 이후 기록 또는 파일이 바뀌면 재승인이 필요합니다.
3. `AiHistoryWindow.xaml(.cs)`: 최근 작업 목록, 중단 상태 검사, 되돌리기 미리보기, 실행 승인, 진행률·취소·항목별 결과를 제공합니다.
4. `AiCommandPanel`: 기록 창을 연결하고 휴지통 별도 2차 승인 및 시작 시 중단 기록 알림을 추가했습니다. 중단 작업은 자동 재실행하지 않습니다.
5. `WindowsRecycleService.cs`: 기존 FileActionsService의 휴지통 실행 경계를 Windows IFileOperation의 FOFX_RECYCLEONDELETE로 보강합니다. 전용 STA에서 실행하며 오류 시 영구 삭제 API로 전환하지 않습니다. 수동/AI 작업이 같은 파일 서비스 소유권을 사용합니다.

상세 기록에는 복구에 필수적인 경로가 있으므로 암호화합니다. 기존 평문 요약 JSON은 개수·상태·작업 ID만 유지합니다. P13 기록에는 상세 경로가 없으므로 자동 복구 대상으로 만들지 않습니다.

## 상태전이와 중단 처리

- 실행: pending → started(쓰기 전 저장) → done / uncertain. 사전 검사 실패는 blocked.
- 되돌리기: done/recovered_done → undo_started(쓰기 전 저장) → undone / undo_uncertain.
- 쓰기 후 기록 저장이 실패하면 started가 남습니다. 검사에서 이름 변경·이동의 양쪽 경로와 지문을 대조한 후 recovered_done / not_applied / uncertain으로 분류합니다.
- 되돌리는 도중 종료된 경우도 원래 경로·변경 후 경로를 대조해 중복 변경을 방지합니다.
- 출처를 확정할 수 없는 복사·폴더 생성·휴지통 중단은 확인 필요로 남깁니다. 원본을 지우거나 전체 명령을 다시 실행하지 않습니다.
- 잘린 기록·변조·레코드 재배열·잘못된 상태전이는 거부합니다. 오류를 무시해 빈 정상 기록으로 바꾸지 않습니다.

작업 단위 잠금은 실행부터 최종 기록까지 유지하며, 되돌리기와 상태 검사도 같은 잠금을 사용합니다. 전체 기록을 매 항목마다 다시 복호화하지 않고 열린 작업의 저장 길이와 상태를 사용합니다.

## 자동검증

- 기존 AI 회귀: 58 PASS. P13에서 금지했던 recycle 계획이 P14 로컬 허용으로 바뀌어 해당 부정 시험 1개를 P14 이중 승인 시험으로 대체했습니다.
- 기존 기본 로직: 56 PASS.
- P14 신규 회귀: 47 PASS (Linux).
- 프록시 모의시험: 18 PASS.
- 보조 정적 계약: 164 PASS.
- Windows 대상 WPF 교차 컴파일·Windows x64 자체 포함 게시: 별도 로그 참고.

P14 시험에는 기록 재조회, 1회성 승인, 변경 파일/원래 경로 충돌 차단, 되돌리기 취소·잔여 항목 재승인, 기록 버전 변경 차단, 기록 저장 전/후 실패 주입, 중단·역방향 중단 모의검사, 기록 잘림·변조·재배열 차단, 무효 상태전이의 저장 전 차단, 작업 단위 동시 실행 차단이 포함됩니다.

암호화 상태전이 시험은 Linux에서 인증 암호화(AES-GCM) 시험용 경계를 사용합니다. 실제 앱의 기본값은 Windows DPAPI이며 Linux에서 평문으로 대체하지 않습니다. 테스트용 키는 실행마다 생성하며 앱 소스에 포함되지 않습니다. Windows 로직 시험에는 실제 DPAPI 왕복 시험이 따로 있습니다.

## 미검증/제한

- Windows GUI, 실제 DPAPI, 실제 Shell 휴지통 및 NTFS 권한별 실기: NOT_RUN.
- 비정상 종료 시험은 저장된 시점과 파일 상태를 구성한 모의시험입니다. Windows 프로세스 강제 종료·정전 실기 통과를 뜻하지 않습니다.
- Gemini 라이브: NOT_RUN. 기존 프록시 코드는 유지되며 운영 서버는 배포하지 않았습니다.
- 복구 기록의 완전한 보존은 OS·장치의 정상 저장 계약에 의존합니다. 모든 하드웨어 장애 복구를 보장하지 않습니다.
- 전체 작업의 일괄 원자적 롤백, 복사/새 폴더 삭제형 Undo, 휴지통 자동 복원은 제공하지 않습니다.
- 기록 누적 정리/보존기간 UI는 후속 범위입니다.

## Windows 재현 절차

BUILD_WINDOWS.ps1을 실행하고 WINDOWS_BUILD_RESULT.json과 실패 단계 로그를 확인합니다. 정적 검사 164개와 Python 모의시험을 빌드마다 반복하지 않습니다. docs/ACCEPTANCE_TESTS.md에 따라 일반 시험자료로 실기 확인한 뒤 배포 상태를 확정합니다.

## 후속

Windows 실기 오류를 먼저 반영하고 P15의 로컬 SHA-256 중복 탐지·메타데이터 분류, 이후 P16 UI/성능/배포 안정화로 진행합니다. 신규 기능보다 현재 미검증 항목을 실제 Windows에서 확인하는 것이 우선입니다.

## Windows 공식 참고

IFileOperation flags: https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperation-setoperationflags
FOF_ALLOWUNDO는 가능한 경우 복구 정보를 보존하는 플래그이고 FOFX_RECYCLEONDELETE는 휴지통으로 보내는 플래그입니다. 실제 API 동작 검증은 위의 Windows 실기 게이트에 남깁니다.
