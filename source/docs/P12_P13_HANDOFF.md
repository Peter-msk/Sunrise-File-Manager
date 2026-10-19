# Sunrise File Manager P12·P13 개발 인수인계

- 기준: v1.1.0/P11 → v1.3.0/P13
- 작업일: 2026-10-17
- 상태: 구현 및 로컬 자동검증 완료, Windows 실기·Gemini 실제 연동 게이트 대기
- 적용: 범용 코드작성 시방서 v1.3 FINAL

## 변경과 소유권

| 영역 | 변경 |
|---|---|
| MainWindow / App | 4개 Chromium 호스트 및 CEF 초기화·종료 제거, AI 패널 1개, 현재 탐색기 추적, 중복 작업 방지 |
| SessionStore | 구형 브라우저 속성을 무시해 읽고 탐색기 경로·분할·보기 설정 유지, 기존 캐시는 삭제하지 않음 |
| AiCommandPanel | 명령·대상·충돌 정책, 변경 전후 표, 전송 내용 동의, 승인·취소, 진행률·파일별 결과 |
| AiScope / AiPlanService | 불변 스냅샷, 임시 ID, 엄격 JSON 필드·중복 키 검사, 허용 명령·이름·범위·충돌·원본 해시 검사 |
| AiLocalPlanner | 접두어·일련번호·새 폴더·복사·이동·확장자/수정일 분류 |
| TransferService | 기존 전송 경로 재사용, 선택적인 승인 대상 경로 계약을 추가해 미리보기 후 자동 이름 재선정 방지 |
| FileActionsService | 기존 실행 소유권 유지, Windows 예약명 공백/상첨자 별칭 검사 보강, 휴지통의 Windows 환경 계약 명시 |
| AiGeminiPlanner / proxy | 인증 HTTPS 프록시 클라이언트 및 Python 표준 라이브러리 서버 예제, JSON 스키마 출력, 시간·크기·호출 상한 |
| BUILD_WINDOWS | v1.3.0, 게시 경로 분리, CEF 잔존 검사, 이미 수행한 Python 검사 반복 없음 |

## 검증 결과

| 항목 | 상태 | 범위 |
|---|---|---|
| 원본 P11 해시 | PASS | 40개 원본 파일 변경 없음 |
| AI .NET 회귀 | PASS 59 | 30개 이름 변경, 승인 전 0건 변경, 반복 실행 거부, 원본 해시 변경, 경로/명령 거부, 취소, 부분 실패, 세션 이관 |
| 기존 .NET 회귀 | PASS 56 | 탐색·미리보기 텍스트·파일 조작·취소·충돌 |
| P13 대량 파일 | PASS | 1,000개 복사 및 1,000개 이동의 개수·SHA-256 검증 (AI 회귀에 포함) |
| 프록시 모의시험 | PASS 18 | 실제 API 연결 없이 응답·인증·한도·시간/크기·금지 명령 경계 검증 |
| 정적 XAML/계약 | PASS 142 | 이벤트·리소스·기본 화면·브라우저 제거 등 보조 검사 |
| WPF Windows 대상 빌드 | PASS | Linux .NET 10.0.100 교차 컴파일 |
| Windows x64 자체 포함 게시 | PASS | Linux 교차 게시, EXE 생성·CEF 네이티브 잔존 없음 |
| Windows에서 BUILD_WINDOWS 실행 | NOT_RUN | 별도 Windows 결과 없음 |
| Windows GUI/PDF/권한 시험 | NOT_RUN | 실제 PC 필요 |
| Gemini 라이브 E2E | NOT_RUN | 서버·모델·계정·키 미제공. 프록시는 배포하지 않음 |

`reports/`에 실행 로그를 포함합니다. 정적 검사 개수를 기능시험 통과 개수와 합산하지 않습니다. 초기 실패한 이전 작업 폴더의 로그는 최종 검증 결과로 사용하지 않습니다.

## 고친 실행 흐름

계획 생성 완료 후 전체 탐색기를 새로 고치면 선택이 해제되어 승인 단계가 실패할 수 있었습니다. 계획 생성 중에는 선택을 유지하고, 실제 파일 작업 완료 후에만 목록을 갱신하도록 변경했습니다. 승인 이후 대상 충돌이 생겨도 다른 번호의 이름을 즉석에서 선택하지 않으며 재계획이 필요합니다.

## 미완료 게이트 및 후속 작업

1. Windows x64에서 BUILD_WINDOWS 및 docs/ACCEPTANCE_TESTS.md의 실기 항목 확인.
2. Windows PDF/이미지 미리보기, 1~4분할, 세션 복원, 100/125/150% DPI 확인.
3. NTFS 접근 거부·읽기 전용·동일 드라이브 이동·접합점 등 Windows 고유 조건 확인.
4. Gemini를 사용할 경우 운영 인증·HTTPS 프록시·계정 모델을 설정한 뒤 일반 시험자료로 라이브 E2E 확인. 앱이 보호 자료의 보안등급을 자동 판정하는 기능은 없음.
5. P14에서는 항목별 영속 복구 저널, 제한적 Undo, 비정상 종료 복구를 설계. 현재는 실패 시 후속 작업 중단과 결과 표시까지 제공.

새 Windows 오류가 나오면 `WINDOWS_BUILD_RESULT.json`과 해당 단계 로그를 기준으로 수정합니다. 이번 환경에서 통과한 Python 검사 전체를 Windows 빌드에 추가하지 마세요.

## 폴더 구성

- `Sunrise.FileManager/`: v1.3.0 전체 앱 소스
- `tests/LogicTests/`: 기존 + AI 실행 회귀
- `tests/verify_source.py`: 보조 정적 검사
- `proxy/`: 선택적 Gemini 백엔드 및 모의시험
- `reports/`: 이번 검증 기록
- `docs/baseline_P11/`: 이전 단계 문서/기록; 현재 완료 상태와 구분
