# P16 최종 코드 인수 — v1.6.1

현재 인수 기준은 FINAL_CODE_REVIEW.md 및 reports/RELEASE_STATUS.json입니다. RC1 이후 설정 저장·불확실 상태·진행률 완료 순서·프록시 응답 경계·소스 증거 검증을 보완했습니다. 런타임은 10.0.13으로 갱신했습니다.

FINAL은 코드 인수본 확정을 뜻합니다. 제품 계획서 7장의 실제 PC 파일 작업/UI/권한 시험은 미실행이며 배포 승인은 PENDING입니다. 자동 검증 또는 교차 게시를 Windows 실기 PASS로 해석하지 않습니다.

BUILD_WINDOWS.ps1은 Windows에서 소스 일치 확인 → 네이티브 소규모 검사 → 단일 게시를 수행합니다. 일반 회귀 전체는 -RunFullTests로 선택할 수 있습니다. 각 결과는 JSON과 로그로 남기며 네이티브 GUI는 자동으로 PASS 처리하지 않습니다. Windows 확인은 ACCEPTANCE_TESTS.md를 따릅니다.

같은 크기·내용의 합성 파일 200개/50MiB에서 버퍼 재사용 전후 할당량은 약 53.2MB/1.03MB였습니다. 이는 P16 RC1의 Linux 단일 측정이며 v1.6.1의 실기 성능 측정이 아닙니다. 관련 기록은 reports/BENCHMARK.json에 보존합니다.
