# Sunrise File Manager 1.0.0 R17 — 개발계획서

- 기준일: 2026-10-20
- 제품/기술: Sunrise File Manager / Windows 10 이상 x64 / WPF .NET 10 / Ollama 선택 연동
- 현재 승인 산출물: R17 소스 및 정적 감사. **설치 EXE는 아직 실기 승인 전**
- 최종 배포 목표: 사용자별 설치형 Setup EXE + `CN=Sunrise Group` Authenticode 서명 + 업데이트 및 제거 지원

## 1. 개발 목표와 완료 기준

사용자는 설치 EXE 하나를 실행해 파일 관리자를 설치하고 시작 메뉴에서 실행하며, 이전 탐색 탭·마지막 폴더·라이트/다크·분할 상태·`#memo` 기록이 재시작과 재설치 후에도 유지되어야 한다. 회사 SecureDisk 정책과 충돌 없이 사용 가능해야 하며 사내 문서 본문을 빌드·검증 과정에서 무단 열지 않아야 한다. 설치파일은 그룹서명과 SHA256 확인이 끝난 뒤 배포한다.

## 2. 개발 단계와 인수기준

| 단계 | 우선도 | 수행 범위 | 수락 기준 | 상태 |
|---|---|---|---|---|
| D0 기준본 정합 | P0 | Drive R12 vs R16 코드·빌드 비교, 필요한 R12 UI 동작 R17 반영 | Drive 전용 누락 0, diff/CRC/SHA와 비교 기록 | **SOURCE 완료** |
| D1 빌드 검증 | P0 | `BUILD_ON_WINDOWS_COMPANY.cmd` → .NET10 게시 → R14 서명 → ISCC Setup 생성 | 실제 Windows x64에서 Signed Setup 생성·로그 확인 | **PENDING** |
| D2 설치 생명주기 | P0 | 설치·시작메뉴·바탕화면 선택·재설치·제거·업그레이드 | 동일 AppId 유지, 앱 및 설정 보존 확인 | **PENDING** |
| D3 실기 UI | P1 | 다크/라이트 글자 대비, 폴더 휠, breadcrumb, 파일 목록 열 폭, 1~4분할, DPI | 100/125/150% 정상, 잘림·가독성 결함 없거나 근거 있는 수정 기록 | **PENDING** |
| D4 파일 기능 안정화 | P1 | 다중 탭 파일 작업, 폴더 트리, 미리보기, `#memo`, Ollama 선택 연동 | 샘플·공개 테스트 자료로 비파괴 smoke PASS, 회사 문서는 무단 검사 금지 | **PENDING** |
| D5 빌드 최적화 | P2 | 내부 OneDir ZIP 중복 생성 최소화, 산출물/로그 분리 | 설치형 빌드 결과·서명 순서·실패시 이전판 보존 | 대기 |
| D6 온라인 업데이트 | P2 | 서명된 Setup 다운로드, 사용자 승인, HTTPS/SHA256, 버전/공지 | 승인된 서버 주소·JSON 계약 존재, 사용자 동의 후 전체 Setup 실행 | 요구사항 확정 대기 |
| D7 구조 개선 | P3 | 과도한 MainWindow/ExplorerPane 부분 클래스 책임 정리 및 Services 하위 모듈 그룹화 | 동작 불변, 테스트·빌드 PASS, 기능별 영향 범위 축소 | 대기 |

## 3. 개발 세부 원칙

### A. 설치/업데이트

- **설치형**은 유일한 공식 사용자 배포. OneDir은 설치 내부 payload, OneFile은 비공식 레거시.
- Inno Setup AppId 고정, `PrivilegesRequired=lowest`, 실행 중 앱 감지에 `AppMutex` 사용, 동작 중 파일 관리 작업 중단 여부 안내.
- 오프라인 설치·제거와 온라인 업데이트를 분리. 업데이트 서버 정보가 없다면 자동 다운로드 기능을 억지로 구현하지 않음.
- 설치 후 DLL/런타임은 관리 경로에 배치하고 사용자는 바로가기로 실행. 설정/메모 폴더는 설치 삭제 대상이 아님.

### B. 보안과 서명

- `CN=Sunrise Group` 기존 인증서 재사용. 서명 실패를 성공으로 처리하지 않음.
- 파일 메타데이터 `Company=Sunrise Group`, 실제 서명 정보와 WinTrust 신뢰 상태를 구분해 기록.
- 개인키/PFX/password를 소스·설치파일·로그로 배포하지 않음. SecureDisk 권한 우회·자동 elevation 금지.
- 서명 완료 후 최종 설치파일 SHA256 생성. V3 오탐은 인증서 존재와 별도 점검.

### C. UI/모듈화

- 상태·설정 단일 저장 로직 및 분할창 개별 상태를 유지하며, HTML 모형은 레이아웃 사전검증에만 사용.
- `ExplorerPane`의 검색/필터/정렬과 `MainWindow`의 분할·AI 도킹 구조는 기능 기준으로 변경 범위를 제한.
- 파일 복사·이동·삭제 등 파괴적 작업은 실행 전 사용자 승인·대상 재확인 구조 유지.
- 다크모드 메뉴 선택색·비활성색·포커스 표시·문자 대비 실기 검증을 별도 수행.

## 4. 다음 작업의 구체적 명령과 기록

1. Windows 빌드 PC에 원본 R17 ZIP을 별도 폴더에 풀고 `BUILD_ON_WINDOWS_COMPANY.cmd` 수행.
2. `build-logs/R15_INSTALLER_BUILD_RESULT.txt`와 `R14` 서명·ISCC 로그를 확보하되 인증서 비밀번호나 사내 문서 경로를 공유하지 않음.
3. 생성된 `dist/Sunrise_File_Manager_1.0.0_Setup.exe` 서명자·SHA256과 Windows 앱 등록 검사.
4. 신규 설치→실행→설정 변경→프로그램 재시작→다음 버전 설치→앱 제거에서 로컬 설정과 `#memo` 보존.
5. 오류 발견 시 오류 로그와 최소 재현 단계만 공유; R17 원본은 보존하고 R18 등 새 리비전으로 패치.

## 5. 정량 검증 및 릴리즈 단계

- SOURCE: 정적 파싱, unit/contract, 누락 파일, ZIP CRC/SHA. 이는 Windows 실행 승인을 대신하지 않음.
- PACKAGE: 실제 설치 EXE 내부 파일·라이선스·서명/해시 및 설치 스크립트 검사.
- WINDOWS DYNAMIC: 실기 앱 시작, 100/125/150% DPI, 대량 파일 목록/다중 분할, 실제 설치/제거 확인.
- RELEASE: 모든 필수 검증 증거와 롤백 방법 확보 후 배포 승인.

## 6. 일정 의존성

D1~D2에는 Windows 빌드 PC(.NET 10 SDK, SignTool, Inno Setup, 그룹 인증서)가 반드시 필요하다. D6에는 최종 서버 URL·배포 권한·업데이트 포맷 승인이 필요하다. 임의 일정이나 승인되지 않은 자격정보를 도입하지 않는다.
