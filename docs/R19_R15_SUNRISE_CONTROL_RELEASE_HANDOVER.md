# Sunrise File Manager 1.0.0 — Sunrise Control · 공지 · 업데이트 (R15)

## 기준 및 범위

- 부모 소스: R19 R14 UI_POPUP_PREVIEW FULL_SOURCE (252개 파일).
- 제품 버전 단일 릴리스 기준 `installer/RELEASE_VERSION.txt`: `1.0.0` 유지.
- 중앙 ID: `sunrise_file_manager`, 중앙 저장소 `Peter-msk/Sunrise_Control/control.txt` schema v1.
- 제품별 버전/공지: 제품 소스 `latest.json`, `notice.txt` 및 공개 Sunrise Control 저장소의 `products/sunrise_file_manager/` 미러. **File Manager GitHub 저장소는 비공개이므로 런타임에서는 공개 Control 미러 URL만 조회**.
- **실행 서명·설치 검증 전**: `latest.json`의 `download_url`, `sha256`, `file_size_bytes`를 비워둔다. 가상의 Setup/해시 금지.

## 동작

1. `App.OnStartup`: 기존 Mutex 획득 후 중앙 `control.txt` HTTPS 조회, 5초 이내 연결 실패 시 유효 TTL 캐시, 캐시 없거나 만료 시 allow fallback. `operation=maintenance/block`이면 본체 창 진입 전에 정책 안내 후 종료.
2. `update=required`이며 현 버전이 `minimum_version`보다 낮으면 필수 업데이트 창만 열고 본체 미진입. 제품 `latest.json`에 검증된 신규 installer가 존재할 때만 다운로드/실행 가능.
3. 일반 실행에서 `설정 · 기능 > 공지사항 · 버전 정보` 또는 `업데이트 확인` 선택 시 독립 창에서 제품별 `notice.txt`, `latest.json`을 로딩한다.
4. `update=block`이면 업데이트 다운로드 버튼 비활성화. 업데이트 자료에 신규 버전, release URL, 유효한 64자 SHA256, 크기 및 `Sunrise_File_Manager_<version>_Setup.exe` 형식이 모두 갖춰져야 다운로드 허용.
5. 새 설치파일은 사용자의 **다운로드 승인** → 제한된 HTTPS GitHub 릴리스 경로 → 다운로드 파일 크기·SHA256 검증 → **실행 승인** 후에만 Shell 실행. 검증 실패 시 파일 삭제 후 기존 앱 유지.
6. 다운로드·공지 조회 시 업무 파일명·내용·경로/AI 데이터 미전송.

## 제한/보안

- `Sunrise_Control/docs/Sunrise_Control_Security_Architecture_v2.0_DESIGN.md`는 디자인이며 미구현. **현재 Control v1은 서명된 정책/리스/장치 인증이 아니고, 로컬 캐시도 암호서명되지 않는다.** 이 정책을 강력한 라이선스/방변조 체계로 표현하지 않는다.
- 원격 Control 정보 실패 시 공식 v1에서 약속한 fail-open. 필요 시 Control v2 실서버 구현 후 별도 제품 업데이트로 승격.
- 원격 GitHub 메타데이터가 인증된 코드 서명 또는 WinVerifyTrust 증명을 대체하지 않는다. 공식 배포 게이트에서 별도 Authenticode 및 스마트스크린/V3 검증이 필요.
- 파일 관리/메모/AI 프로세스, 빌드 서명 CMD, Inno AppId는 유지.
- 자동 다운로드/백그라운드 파일 변경/자동 업데이트 실행 없음.

## 출시 체크리스트 (Windows PENDING)

- Windows WPF 컴파일 (SDK 10)
- Sunrise Control allow/maintenance/block 및 cache TTL/fallback 실기
- required update 최소버전 판정, 최신 feed 미게시시 안전 중단
- 업데이트 채널 게시 후 HTTPS·크기·SHA256 불일치/변조 테스트
- Setup Authenticode 서명 및 설치/업그레이드/삭제/보안 프로그램 검사
- 100/125/150% DPI 및 업데이트 UI 툴팁/버튼 겹침 점검

## 운영 단계

- Control 등록: `[product.sunrise_file_manager]` 섹션 추가, 기본 `operation=allow`, `update=allow`.
- 서명된 1.0.0 Setup 실제 생성 및 검증 **이후에만** `latest.json`에 실제 release URL, 해시, 크기를 기록.
- 이후 1.0.1+ 릴리스는 Inno AppId/서명신원/버전계약 유지. 중앙 `minimum_version` 조정은 검증된 새 설치파일 게시 **이후**에 진행.
