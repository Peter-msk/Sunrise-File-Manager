# Sunrise File Manager v1.8.2 R00 — EIASS·파일관리 통합 개발 소스

**상태: 정식 설치파일이 아닌 소스 후보.** 공식 v1.8.0 설치파일은 그대로 유지됩니다.

## Windows 빌드

관리자가 아닌 일반 Windows 개발 계정에서 .NET 10 SDK, Inno Setup 및 Sunrise Group 공식 코드서명 인증서를 준비한 다음
`BUILD.cmd --check --no-pause` → `BUILD.cmd --release --no-pause` 를 실행합니다.

릴리즈 전에 `source/tests/EiaWorkspaceProductionSmoke/` Windows 로컬 NTFS 실기, 설치·재설치·업데이트 및 UI 확인을 통과해야 합니다.

EIASS 생성은 사업 기본안의 누락 디렉터리만 대상으로 하며 NAS/SMB는 현재 차단합니다.
`docs/HANDOVER_EIASS_v1.8.2.md`에 현행 검증표와 잔여 Gate를 기록했습니다.


## 현행 버전 계약 (2026-11-15)

- **개발 후보**: 1.8.2 R00. WPF 제목·표시, csproj Assembly/FileVersion, app.manifest, Sunrise Control 보고 버전, 설치 버전 파일을 통일했습니다.
- **공식 사용자 배포**: v1.8.0 유지. `main/latest.json`·`notice.txt` 및 기존 GitHub Release를 변경하지 않습니다.
- `release/NOTICE_BODY_1.8.2.txt`는 서명된 배포 아티팩트가 생성된 이후 사용할 공지 후보이며, GitHub 배포 자체가 아닙니다.
- 과거 R01~R05의 검증 숫자와 `v1.8.1` 명칭은 **역사적 증적**입니다. 새 v1.8.2의 Windows 검증을 완료했다는 뜻이 아닙니다.
- 버전승격 요약: `docs/V182_VERSION_MIGRATION_REPORT.md`; 남은 필수 게이트: `docs/V182_RELEASE_BLOCKERS.md`.

## R01 외부 GitHub 벤치마크 반영 (v1.8.1 역사적 개발 단계)

- 공개 외부 프로젝트 분석: docs/EXTERNAL_GITHUB_BENCHMARK_R01.md
- EIASS WPF 검토창 작업상태, 취소 시 닫기 제어, 진행률, 결과 폴더 열기 보완
- 새 NuGet 의존성 없음, 기존 v1.8.0 정식 배포 unchanged
- R01은 소스 검토본이며 Windows 설치/서명/실기 테스트는 아직 완료되지 않음

## R04 — 대용량 재귀 검색 페이지 안정화 (v1.8.1 역사적 개발 단계)

- 문서: `docs/STABILIZATION_PLAN_v1.8.1_R04.md`, `docs/R04_VALIDATION_REPORT.md`
- 변경: 페이지 이동 시 정렬된 재귀 검색 결과 재사용, 검색 조건 변경 시 무효화, 유형 필터 중복 배열 제거, 2,000행 제한·열 가상화 보존.
- Windows 전용 테스트(미실행): `dotnet run --project source/tests/UiSmokeTests -- --r04-recursive-page-only <증적폴더>`
- 결과: Python 변경영향/관련 UI 계약 164 PASS, 33 하위검사 PASS. WPF/서명된 Setup은 미검증.


### v1.8.2 R01 EIASS 화면 간소화
- 사업명·용역명·평가 유형 → 폴더 목록 계산 → 기존 기준 위치 선택 → 누락 폴더 생성. 내부 충돌/경로 검증 후 확인 1회.
- Windows 로컬 NTFS/ReFS에 새 디렉터리만 생성; 기존 파일은 삭제하거나 변경하지 않음. NAS/SMB 생성은 미지원.
- 실행·서명 검증은 `validation/R01/RUN_EIASS_R01_WINDOWS.ps1`와 Windows 실화면 점검으로 확인.
