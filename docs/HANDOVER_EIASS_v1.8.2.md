# Sunrise File Manager v1.8.2 R00 — 현행 작업 인수 및 출시 게이트

## 범위

- **기준 작업본**: `Sunrise_File_Manager_v1.8.1_R05_VERIFY1_SOURCE.zip` (518개 ZIP 엔트리, Python/Windows 검증용 자료 포함). R01~R05 제품 기능과 사용자 데이터 저장형식을 변경하지 않고 버전 표현·설치용 메타데이터를 v1.8.2로 승격.
- **현행 개발 버전**: `1.8.2`, 어셈블리 및 매니페스트 `1.8.2.0`, 설치 버전파일 `1.8.2`, Sunrise Control 현재 보고 버전 `1.8.2`, EIASS 생성 검토창 및 메인 창 표시 v1.8.2.
- **기존 공식 사용자 버전**: v1.8.0 stable. 서명된 v1.8.2 Setup이 없으므로 `main/latest.json` / `notice.txt` / 기존 GitHub Release는 절대 교체하지 않음.
- **업데이트·외부 API**: Product ID, Control ID, 설치 AppId, 원격 Control 주소, `latest.json` 스키마, 업데이트 정책·사용자 설정 경로 및 저장형식 불변.
- **회사 폴더 규칙**: 없음. EIASS 자체 기본 폴더안을 운영 대상으로 사용. NAS/SMB 폴더 생성은 여전히 불가. 원본 파일 보호와 명시적 승인/취소 유지.

## 제품 기능 계승 (v1.8.1 R05 소스)

| 영역 | 계승 코드 | 릴리즈 전 검증 |
|---|---|---|
| EIASS 폴더 생성 | 로컬 NTFS/ReFS, 최종 사용자 승인, 누락 디렉터리만 생성, 중단 및 재검사 | Windows 파일시스템·권한·경합 실기 |
| EIASS PDF 파일명 변경 | 사전 충돌 검사, 비동기 작업, 중단 후 원본·메모 복구 | Windows UI/NTFS/Undo·취소 실기 |
| 일반 일괄 이름 변경 | 2단계 임시 이름, 복구 및 작업 상태 표시 | Win32 파일잠금·중단·부분실패 실기 |
| 일반 파일 전송 | 검증된 파일만 삭제, 예상하지 못한 원본 파일 재귀 삭제 금지 | 다른 볼륨 이동/동시 파일 변경 실기 |
| 대용량 탐색 | 재귀 검색 결과 캐시, 목록 가상화, 2,000행 페이지 | 2,205개 테스트/고DPI 성능·시각 확인 |
| 일반 UI/UX | R05 메뉴·이벤트 정적 감사 498건, 리소스 570건, 중지 버튼 | 1·2·4분할 및 DPI 100~175% 실화면 |

## 빌드·릴리즈

1. Windows x64 / .NET SDK 10에서 `BUILD.cmd --check --no-pause` 이후 `validation/R05/RUN_WINDOWS_GATES.ps1`을 실행하여 이 **버전의** 원본 데이터 불변·실제 작업 동작을 확인한다.
2. 실패 Gate만 보완하고, 이미 통과한 무관한 검사는 반복하지 않는다. 원본 데이터와 실계정은 검증에 사용하지 않는다.
3. 최종 UI·설치·업데이트·AuthentiCode 검증 후 `BUILD.cmd --release --no-pause`로 서명된 `Sunrise_File_Manager_1.8.2_Setup.exe` 생성 및 SHA-256 확인.
4. 공개 GitHub Release에 해당 설치파일을 게시·다운로드 검증한 **다음**, 로컬 생성 메타데이터와 실제 파일 해시가 일치할 때 `notice.txt`, `latest.json`(마지막) 순으로 업데이트한다.
5. 위 단계를 완료하기 전에는 `1.8.2`를 *정식 출시*, *Windows 실기 PASS* 또는 *다운로드 가능*으로 공표하지 않는다.

## 역사적 테스트·문서 처리

- 기존 R01~R05 관련 파일명·테스트 플래그 `--v181-*`·보존된 검증 로그는 사용 중인 내역 추적 키이므로 변경하지 않는다. 새 빌드에 대한 기대 버전 비교 값만 1.8.2로 교정한다.
- `docs/HANDOVER_EIASS_v1.8.1.md`, `docs/R05_*` 보고서 및 `source/docs/P181_...`은 역사적 증적으로 유지한다. 과거의 테스트 수치가 이번 v1.8.2 버전의 새로운 시험 결과로 변환된 것이 아니다.
- 현행 게이트: `docs/V182_RELEASE_BLOCKERS.md`; 승격 검증 증적: `docs/V182_VERSION_MIGRATION_REPORT.md`.


## R01 UI 직결 활성화 (2026-11-15)
- 기준본 v1.8.2 R01. 기존 폴더 설계와 Native Writer는 재사용. 2차 검토창 접근 경로를 제거하고 주 화면에서 자동 재점검 + 한 번의 최종 확인으로 누락 폴더 생성.
- 레이아웃·진단표 4열·진행률·중지·결과 열기 개선. `Controls/EiaWorkspacePlanningWindow.DirectCreation.cs`는 화면 전용 실행 오케스트레이션, 파일시스템 쓰기는 `EiaWorkspaceProductionWriter`만 수행.
- 변경 상세/Windows 미검증 사항: `docs/V182_R01_EIASS_DIRECT_CREATE_UI_AUDIT.md`.
