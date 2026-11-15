# Sunrise File Manager v1.8.2 R00 — 개발 버전 전체 승격·검증 보고

- **승격 기준본**: `Sunrise_File_Manager_v1.8.1_R05_VERIFY1_SOURCE.zip`. R01~R05 기능 변경분 전부 포함하며 기존 사용자 자료·데이터 포맷은 수정하지 않음.
- **새 개발 기준**: `v1.8.2 R00`, Source ZIP만 생성. 공식 v1.8.0 설치본/최신 업데이트 JSON/공지/다운로드 URL은 변경하지 않음.
- **변경 범위**: 앱/Control/화면/EIASS/manifest/csproj/설치 버전 값, 새 버전 릴리즈 노트, Windows 후보검증 버전, 현행 인수인계·검증계약. 기존 R05 파일 복사·이동·취소·복구 등 사용자 동작 알고리즘 변경 없음.
- **현재 제품 버전**: csproj `<Version>1.8.2</Version>` / `AssemblyVersion`·`FileVersion` `1.8.2.0`; app.manifest `1.8.2.0`; MainWindow `v1.8.2`; EIASS 생성창 `v1.8.2`; Control 버전 `1.8.2`; `installer/RELEASE_VERSION.txt=1.8.2`.
- **빌드 계약 보존**: Inno Setup AppId, 사용자 설치 경로, update wire schema, Sunrise Control ID/URL, 기존 사용자 파일 및 memo 포맷, 옛 Windows native smoke 옵션 `--v181-r03-transfer-safety` 유지. 최신 버전별 릴리즈 노트 `release/NOTICE_BODY_1.8.2.txt` 신규 등록; 기존 1.8.1 공지는 미출시 개발 이력으로 보존.
- **검증 (이 버전에서 실행)**: Python 현행 EIASS·R01~R05·신규 버전 계약 관련 **60 PASS / 11 subtests**. WPF 22개 파싱/메뉴 정적 계약은 포함, Windows C# 컴파일·설치·파일시스템 실기 결과 아님.
- **역사 계약 불일치**: `source/tests/test_v180_r05_eiass_menu.py::test_folder_planner_entry_is_not_a_writer`는 원래 v1.8.0의 `자동 생성` 안내를 요구하지만, 현행 승인 후 누락 폴더 생성 문구로 변경되어 **1건 FAIL**. 구형 테스트 코드를 조작하거나 새 버전 PASS로 재분류하지 않음; 현행 안내는 `test_v182_version_consistency.py`로 별도 점검.
- **기존 역사 검증의 33건 v1.8.0 고정 불일치**는 이전 R05 독립검증 기록으로만 보관. 이번 버전에서 재수행하거나 PASS로 승계하지 않음.
- **실행하지 못한 게이트**: Windows .NET 10 WPF Release 빌드, 실제 NTFS/ACL/동시쓰기/파일 이동·Undo·EIASS, 100/125/150/175% DPI, 클라우드·AI 실계정 동작, Authenticode signed Setup, 1.8.0→1.8.2 설치/업데이트.
- **Windows 시험**: `validation/R05/RUN_WINDOWS_GATES.ps1`는 기존 R05 게이트 이름을 유지하되 버전 1.8.2 사전 검사 및 현 버전 evidence를 기록함. `BUILD.cmd --release --no-pause`는 Windows 검증·공식 서명 승인 이전에 실행하지 않음.
- **배포 경계**: `latest.json`/`notice.txt` 공개 채널 유지. v1.8.2 `official_release:true`를 아직 게시하지 않았고 1.8.2 Setup 다운로드를 제공하지 않음.

## 1.8.2 출시 승인 순서

1. 위 현행 소스·버전 계약 PASS 및 Windows 코어 게이트 PASS.
2. Windows UI·파일 원본보호·취소/원복/충돌 실기와 업데이트/재설치 검증.
3. 코드서명과 Setup Authenticode 검증, 설치파일 SHA-256 기록.
4. 공식 GitHub Release에 파일을 먼저 업로드하여 공개 파일의 이름·크기·해시를 재확인.
5. `notice.txt`를 게시하고 `latest.json`을 마지막에 게시. 이후 실사용 업데이트 smoke.

자세한 미완료 Gate는 `docs/V182_RELEASE_BLOCKERS.md`를 참조. 이전 `docs/R05_*`와 `docs/HANDOVER_EIASS_v1.8.1.md`는 역사 기록으로 유지.
