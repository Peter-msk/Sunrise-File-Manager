# Sunrise File Manager v1.8.1 R05 — 독립 검증·결함 분류 및 Windows 인수보고

**검증 기준:** `Sunrise_File_Manager_v1.8.1_R05_AUDIT_SOURCE.zip` 원본(2026-11-15), 정식 배포 아님.  
**검증 범위:** 제공된 소스 자체와 R06 원본에 있는 역사 테스트 자료의 분리 검증. **사용자 사업 자료·클라우드 파일을 생성/삭제/변경하지 않음**.

## 1. 패키지 무결성 — PASS_STATIC

- 원본 R05 ZIP 항목 513개, `ZipFile.testzip() = None`, 위험한 ZIP 엔트리(상대경로 역주행·절대경로) 0개.
- ZIP 내부 파일 513개와 `/mnt/data/fm_v181_r05` 작업본을 SHA-256로 비교: 누락 0, 내용 불일치 0.
- 원본 ZIP SHA-256: `3f3ab892b535373f2cc75e961f4c416a01d3d236aed9f4a2723fa1aece664d13`.
- 제품 WPF C# 190개, XAML 22개 존재. 현행 소스 이벤트·리소스 정적 계약검사 PASS.
- 제품 버전 선언(.csproj, app.manifest, installer/RELEASE_VERSION.txt)은 `1.8.1`로 일치. **GitHub `main`의 공식 latest.json 및 v1.8.0 설치본은 변경하지 않음.**

## 2. 실제 실행한 Python 검증

| 구간 | 결과 | 의미 |
|---|---|---|
| R05+R04+R03+R02+EIASS+핵심 UI 변경영향 | **102 PASSED / 32 subtests** | R05 원본 소스만으로 재현 가능한 정적/계약검사 |
| 관련 테스트 확대(R17 포함) | **129 PASSED / 1 FAILED / 54 subtests** | 1건은 원본 R05에 없는 역사 `validation/R17/VERIFY_R17_WINDOWS.ps1` 참조 오류 |
| 전체 역사 테스트 수집(R05 원본) | 1,308 collected / 5 collection errors | 예전 `validation` 도구·검증 스크립트가 소스 후보에 미포함 |
| 역사 검증 자료 368개를 **별도 격리 디렉터리**에 복원한 전체 시험 | **1,320 PASSED, 36 FAILED, 20 ERRORS** | 기준본·계약 연도 혼합으로 R05 검증을 그대로 대체할 수 없음 |
| R06 원본에서 역사 문서 2건도 추가한 별도 격리 전체 시험 | **1,342 PASSED, 34 FAILED** | 현대 계약과 서로 배타적인 과거 v1.8.0/생성금지 assertions 존재 |
| R06 원본의 설치/실행 보조 CMD 2개까지 격리 복원한 전체 시험 | **1,343 PASSED, 33 FAILED, 285 subtests** | 1건의 실제 소스 패키지 누락이 해결됐고, 나머지는 이전 버전 텍스트 고정/읽기 전용 제약과 충돌하는 역사 검사 |

**주의:** 위 수치는 같은 테스트를 조건별로 다시 실행한 결과이며 **각 행을 합산한 총 PASS 수치가 아님**. 전체 1,376건 수집·실행은 실험용 격리 작업본에서만 수행했다.

## 3. 확인된 문제 및 처리

| ID | 등급 | 발견 사항 | 조치 / 잔여 |
|---|---|---|---|
| V05-PKG-01 | P1 | R05 소스 ZIP에 `INSTALL_OLLAMA.cmd`가 누락되어 기존 설치 도우미 소스 계약 실패 | **R06에서 원본 바이트 그대로 복원**, 별도 수정 패키지에 포함. 외부 다운로드 도우미 자체의 Windows 실행은 미검증 |
| V05-PKG-02 | P2 | `RUN_FILE_MANAGER.cmd`도 R05 소스 ZIP에서 누락 | **R06 원본 바이트 그대로 복원**, 별도 수정 패키지에 포함 |
| V05-QA-01 | P1 | R05 소스 패키지에는 역사 validation/ 디렉터리·기준문서가 없어 모든 시대의 테스트 전량 재현 불가 | R06 원본 역사자료를 **격리 경로에만** 복원해 원인을 분류. 출시 단일 게이트로 구형 테스트 전량을 사용하지 않고 변경영향 테스트만 운용 |
| V05-QA-02 | 참고 | 역사 33개 검사는 v1.8.0 버전·읽기 전용 폴더 생성 강제·옛 UI 문구를 고정 | 의도적 계약 변경 또는 역사 기준 불일치로 **기존 PASS 승계 보류**. 구형 테스트를 임의 삭제하거나 기대값만 고쳐 PASS를 만들지 않음 |
| V05-WIN-01 | **P0** | `.NET 10` SDK/Windows WPF 없음. C# 컴파일/런타임/실제 디스크 쓰기 미실행 | `validation/R05/RUN_WINDOWS_GATES.ps1` 제공, **Windows에서 실행한 증거 없이는 PENDING** |
| V05-REL-01 | **P0** | Authenticode 서명된 v1.8.1 EXE/Setup, 실설치/업데이트/고DPI 캡처 없음 | **RELEASE BLOCKED**. GitHub Draft PR 유지, v1.8.0 stable 보존 |

## 4. 수정한 범위·보존한 범위

- **제품 C# / WPF 코드는 수정하지 않았음**. R05 기존 검증된 변경만 유지.
- R05 검증 패키지에만 R06 원본의 `INSTALL_OLLAMA.cmd`·`RUN_FILE_MANAGER.cmd`, 정확한 `CURRENT_SOURCE.md` 포인터, Windows 전용 검증 스크립트 및 이 보고서를 추가.
- `latest.json`, `notice.txt`, 공식 v1.8.0 Setup은 이 패키지에 새 버전으로 생성하거나 수정하지 않았다.
- CI/GitHub Actions를 재활성화하지 않았고 외부 계정·사용자 사업폴더에 실제 쓰기 시험을 수행하지 않았다.

## 5. Windows에서의 후속 검증 계약

승인된 Windows 10/11 x64 개발 PC(사용자 데이터가 없는 격리 테스트)에서:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\validation\R05\RUN_WINDOWS_GATES.ps1
```

이 스크립트는 서명·설치·배포 작업을 수행하지 않고, **현재 미검증인** `.NET10/WPF Release Build`, R03 파일 이동, 운영용 EIASS 폴더 생성, R05 일반 파일명 변경, EIASS PDF 파일명, R04 재귀 탐색 6개 게이트만 실행한다. 각 UI 게이트는 생성한 `UI_RESULT.json`의 `status=PASS`를 반드시 확인한다. 결과는 OS 임시폴더의 GUID 증적 폴더에 보관하고, `R05_WINDOWS_GATE_RESULT.json`에 각 오류를 기록한다.

다음 항목은 이 스크립트만으로는 **통과 판정 불가**: DPI 100/125/150/175% 실제 화면, 가시성/키보드/메뉴 클릭, NAS/SMB, 클라우드 실계정, 실제 사용자의 권한·네트워크, Authenticode/설치/업데이트/복구. 해당 실기 결과와 코드서명 증적을 별도 확인해야 정식 출시할 수 있다.

**현재 판정: `PASS_STATIC / WINDOWS_DYNAMIC_PENDING / RELEASE_BLOCKED`.**
