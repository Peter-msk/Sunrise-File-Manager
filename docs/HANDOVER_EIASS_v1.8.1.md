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
