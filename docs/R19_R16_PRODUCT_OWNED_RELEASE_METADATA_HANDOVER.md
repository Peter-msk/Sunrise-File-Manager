# Sunrise File Manager 1.0.0 R19 R16 — 제품별 메타데이터 정상화

## 소유권·원천
- Sunrise Control 공개 `control.txt` : `sunrise_file_manager` 실행 허용/점검/차단, 업데이트 허용/차단/필수, 최소버전만 제어. **공지/버전/해시를 소유하지 않음.**
- File Manager 비공개 GitHub 저장소 최상위 `latest.json`, `notice.txt`: **제품별** 버전·공지·설치파일 정보의 원천.
- 프로그램은 File Manager 저장소의 두 파일 주소를 조회하고 Control 제품별 미러를 사용하지 않음.
- `installer/RELEASE_VERSION.txt`가 정확한 버전의 단일 원천이며, `release/NOTICE_BODY.txt`가 공지 본문 입력임.

## 빌드 절차
1. `BUILD_ON_WINDOWS_COMPANY.cmd`: .NET WPF 게시 → 서명 → Inno Setup 서명 설치파일 완성·검증.
2. **실제 Setup 파일 확정 이후**: `BUILD_RELEASE_INSTALLER.cmd`가 .NET 10 SDK 기반 `build-tools/Sunrise.FileManager.ReleaseMetadata` 실행.
3. 생성기가 Setup 실제 크기·SHA256을 계산하고 `latest.json`, `notice.txt`를 제품 소스 루트 및 `dist/release-metadata/`에 생성. 실패하면 릴리스 빌드 실패로 판정.
4. `download_url`, `release_url`은 실제 외부 공개 경로가 검증되기 전에는 **빈 문자열**, `update_endpoint_configured=false` 유지. 아무 설치파일도 자동 업로드·게시·실행하지 않음.
5. 발행 절차: 서명 및 실기 설치 테스트 완료 → 설치파일을 접근 가능한 배포 위치에 게시 → 실제 URL/해시/크기를 검증 → 제품 저장소의 `latest.json`, `notice.txt` 게시. Control은 필요할 경우 `minimum_version`만 갱신.

## 비공개 GitHub 접근 제한 (필수 확인)
비공개 `Peter-msk/Sunrise-File-Manager` 저장소의 `raw.githubusercontent.com` 주소는 로그인/인증 없는 일반 PC에서 **404**가 발생할 수 있음. 제품별 파일 소유권을 유지하는 것과 익명 인터넷 배포는 서로 다름. 현재 코드는 **사용자 지시대로 제품 저장소를 직접 조회**하므로, 공개 메타데이터 채널이 마련되기 전에는 일반 PC의 공지/업데이트 확인이 실패할 수 있음. 개인 GitHub 토큰 내장이나 무단 저장소 공개는 금지.

제품 소스의 최신 공지와 버전 정보를 읽기 전용으로 노출하는 별도 공개 배포 경로(예: 제품별 GitHub Pages 또는 전용 제품 배포 저장소)를 **사용자 승인 후** 구성하고, 해당 제품 경로로만 런타임 URL을 전환할 수 있음. 중앙 Control 저장소를 제품별 공지 호스트로 다시 사용하지 않음.

## Sunrise Capture 참조 근거
GitHub `Peter-msk/Sunrise_capture` 릴리스 4.2.0에 공개된 소스 ZIP은 실제 빌드 스크립트가 아닌 3개 메타데이터 파일뿐임. 확보된 v4.2.0 작업 인수인계서는 `BUILD.cmd → build_pipeline.py → PyInstaller/SignTool/Inno → release_metadata` 흐름을 설명함. 본 R16은 **동일한 제품별 메타데이터 생성 단계/소유권**을 채택하되, Capture의 구현 코드를 1:1 복사했다고 주장하지 않음. 추가 Python 의존성은 요구하지 않음.

## 검증 상태
- 소스 정적 계약 및 Python 회귀검사: R16 테스트 기록 확인.
- Windows 네이티브 `.NET 10` 도구 컴파일, 실제 서명/설치, 공지 접근성: **PENDING**.
- 서명 키, 토큰, 사용자 업무파일 및 개인정보는 메타데이터에 기록하지 않음.
