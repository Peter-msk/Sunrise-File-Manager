# Sunrise File Manager v1.8.1 R05 — 전체 기능·코드·메뉴·UI/UX 출시 전 감사

- **감사 기준본**: `Sunrise_File_Manager_v1.8.1_R04_SOURCE.zip` (개발 기준 커밋 `ffbdc1e691f3a4a31db9cf3d658c3bd31771241b`)
- **감사 성격**: 전체 정적 구조/연결 감사 + 영향 높은 결함의 코드 심사·수정 + 변경영향 계약검사. **Windows 실행/시각/파일시스템 실기검사 아님**.
- **규범**: Sunrise 개발표준 v2.8의 `UI·UX 현행계약·시각검증`, `개발원칙·안전경계·코드품질` 모듈, R01~R04 기능 현행계약을 우선한다. 원본 보존, 기존 PASS 비침범, local owner 우선.

## 1. 감사 범위와 실제 수량

| 항목 | 소스 정적 조사 결과 | 의미 / 한계 |
|---|---:|---|
| 제품 C# 소스 | 190개 | 개별 메서드 실행 성공을 뜻하지 않음 |
| WPF XAML | 22개 | 전수 XML 파싱 성공 |
| WPF 메뉴 항목 | 249개 | 최상위/하위 메뉴 전체의 선언 수, 실제 보이는 항목 수와 다름 |
| 버튼 | 201개 | 숨겨진 호환성 컨트롤도 포함 |
| TextBox / DataGrid | 46개 / 23개 | 런타임 데이터·DPI 표시 미검증 |
| 코드비하인드 이벤트 연결 | 498개 | 선언된 **해당 클래스**에서 핸들러 이름 확인, 누락 0 |
| UI 리소스 정의 / 참조 | 106개 / 570회 | 이름의 존재성 검사, 실제 테마/디자인 렌더링은 별개 |

정확한 메뉴 트리/라우팅/파일 소유자는 `docs/R05_MENU_FEATURE_MATRIX.json`으로 보존했다. 사용자에게 보이는 메뉴의 가시성, 핫키 충돌, 실제 클릭 가능 영역은 Windows 런타임에서 추가로 확인해야 한다.

### 최상위 정보 구조

- **메인 셸**: `EIASS ▾`, `설정 · 기능 ▾`. 후자는 파일 관리, 검색·문서, 분할·작업 공간, 전송·복구, 클라우드·AI, 패널·화면, 프로그램 정보의 **7개 기능 그룹**으로 구성.
- **각 로컬 탐색창**: 파일, 편집, 보기, 즐겨찾기, 추가 기능, 정보의 **6개 고정 메뉴**. 동일 명령의 전역/활성창 진입점은 사용자 문맥의 차이가 있으므로 일괄 삭제하지 않는다.
- **레이아웃 소유권**: 상단 42 DIP 단일 헤더, 메인 파일/공유 검사 패널, 고정 54 DIP 우측 도구 레일, 하단 작업 상태/진행률. 1~4분할과 가로 스크롤 계약 보존.
- **데이터 목록**: 파일 리스트 행 가상화 ON, 열 가상화 OFF, 수평 스크롤 및 열 최소폭 유지. 컴팩트 분할에서 검색줄을 두 번째 행으로 배치하는 현재 로직 확인.

## 2. 공개 GitHub 벤치마킹 — 적용 가능성과 배제

| 외부 저장소 | 공개 소스 참조 | 차용한 **설계 패턴** | 결정 |
|---|---|---|---|
| Microsoft PowerToys / PowerRename | https://github.com/microsoft/PowerToys/tree/main/src/modules/powerrename | 미리보기 → 충돌 검토 → 명시적 실행 → Undo | 실제 파일명 변경 엔진/검토 창 현행 경로만 보완 |
| Files | https://github.com/files-community/Files/tree/main/src/Files.App/Utils/Storage/Operations | 대량 작업·진행/취소·충돌·안전 완료 인지 | 취소를 활성화된 파일 작업 바깥에 배치 |
| .NET Community Toolkit | https://github.com/CommunityToolkit/dotnet/blob/main/src/CommunityToolkit.Mvvm/Input/AsyncRelayCommand.cs | 단일 실행 소유권·취소 가능한 비동기 명령 | 기존 `RunLocalFileJobAsync` 재사용, 패키지 추가 불필요 |
| WPF UI | https://github.com/lepoco/wpfui | 상태 피드백과 메뉴·진입점 분명히 하기 | 비활성 기능 사유 툴팁 + 진행/복구 문구 |
| MahApps.Metro | https://github.com/MahApps/MahApps.Metro | 긴 경로/파일명 클리핑 방지 | 표 셀 말줄임 + 전체 이름 툴팁, 좁은 창 가로 이동 안내 |

**저작권/의존성**: 외부 코드를 복제하지 않고 동작 설계만 비교했다. 새 NuGet/타사 UI 라이브러리 추가 없음. WPF 전역 테마 변경 없음.

## 3. 전 영역 기능 감사 매트릭스

| 기능 영역 | 소스 Owner / 구현 확인 | 정적 결과 | 우선 잔여 검증 |
|---|---|---|---|
| 기본 파일 생성·복사·이동·이름변경·삭제 | `FileActionsService`, `TransferService`, `RecycleBinService` | P0: 일반 일괄 이름 변경 동기 실행 확인·R05 보완 | Windows NTFS/권한/잠금/파일 간 변경 동작 |
| Undo/Redo·메모 동반 파일 작업 | `ExplorerUndoService`, `FileMemoStore` | 기존 결과·회귀 소스 연결 확인, R05 성공 시에만 Undo 기록 | 부분 실패·실제 memo 파일/폴더 inverse 복구 |
| 폴더 탐색·목록·정렬·재귀 검색 | `ExplorerPane.*`, `DirectoryScanner`, `FileListView` | R04 sorted-view cache/2,000행/행 가상화 계약 보존 | p50/p95, 2,205·10,000행 Windows 측정 |
| 1~4분할·세션/드래그·탭 | `MainWindow.*`, `WorkspaceSplitLayoutEngine`, `SessionStore` | 핸들러·메뉴 연결 PASS; 전역 상태 범위 유지 | 실제 각 분할·최소창·키보드/드래그 |
| 상단/탐색창 메뉴 구조 | `MainWindow.xaml`, `ExplorerPane.xaml` | 2+6 기본 진입 메뉴, 498 이벤트 소유자 PASS; EIASS 진입점 상태 표시 수정 | Windows 가시성, 메뉴 항목 실제 클릭/포커스 |
| EIASS 기본 사업폴더 생성 | `EiaWorkspaceProductionWriter`, `NativeDirectoryWriter` | 운영용 코드 존재, 승인/재점검 동작 유지 | 로컬 NTFS 실기/ACL/reparse/cancel; NAS는 미지원 |
| EIASS PDF 파일명/목차 | `EiassRenameDialog`, `EiassFileNameService` | R02 비동기·검토/Undo 경로 보존 | 실제 PDF, memo, 1~1,000개 변경 |
| 클라우드 계정·원격 복사·복구 | `CloudDrive/*`, `CloudDriveWindow` | 소스·메뉴 연결 및 관련 기존 회귀 계약 PASS | 실제 OAuth/토큰 만료/권한/네트워크 장애 |
| AI·스마트 검색·OCR | `Ai*`, `WindowsOcrService`, `SmartSearchWindow` | UI 엔트리/핸들러 확인, 구현 미삭제 | Ollama/모델 실행·네트워크·응답 지연 |
| 상세정보·메모·미리보기·압축 | `FileDetailsPanel`, `FileMemoPanel`, `FilePreviewPanel`, `ArchiveService` | 독립 패널·메뉴·공용 도크 코드 보존 | 대형 파일·손상 압축·PDF 렌더 Windows 실기 |
| 테마·글꼴·DPI·레이아웃 | `App.xaml`, `ThemeManager`, `UiLayoutPolicy` | 리소스 106개/참조 570개 누락 0 | 실제 100/125/150/175% 화면 캡처 |
| 버전·설치·업데이트·Control | `SunriseReleaseService`, `SunriseControlService`, installer/ | 공식 v1.8.0 배포 메타 유지 | v1.8.1 Windows 빌드/서명·업데이트 전 과정 |

이 매트릭스의 **정적 결과는 모든 기능의 동작 PASS가 아니다**. 실제 실행을 보지 못한 기능은 그대로 미검증으로 관리한다.

## 4. R05 발견 결함과 실제 수정

| ID | 심각도 | 발견 근거 | 보완 결과 |
|---|---|---|---|
| R05-P0-01 | P0 | 탐색창 일반 일괄 이름변경(`Rename_Click`)이 `FileActionsService.BatchRename`을 UI 이벤트 스레드에서 직접 호출 | 기존 `RunLocalFileJobAsync`로 이동; 승인 계획 snapshot, 취소 후 복구/완료 후 Undo만 기록 |
| R05-P0-02 | P0 | 일괄 이름변경 도중 탐색창 전체 `IsEnabled=false`여서 그 안에 취소 버튼을 놓을 수 없음 | MainWindow 상태표시줄에 별도 `파일 작업 중지` 추가; 해당 소유 pane의 CancellationToken 연결, 진행 상태 안내 |
| R05-P0-03 | P0 | 일반 파일/폴더 혼합 일괄 변경은 취소 토큰을 전달받지 않음 | 서비스 시그니처에 optional CancellationToken, 항목간 취소와 완료 이동 역순 rollback 추가; 실패 시 원본/대상 보고 |
| R05-P1-01 | P1 | `BatchRenameDialog`가 잘못된 시작번호를 **자동 1로 대체**하여 사용자가 승인하는 이름과 기대값이 달라질 가능성 | 사용자가 일련번호를 켰을 때 오류 표시·적용 비활성; 숫자/붙여넣기 제한 |
| R05-P1-02 | P1 | 255자를 넘는 생성 후보를 미리보기에서 `변경 가능`으로 표시할 수 있음 | 미리보기와 mixed batch 실행 직전 이름 길이 검사, 안전 실패 |
| R05-P1-03 | P1 | EIASS 계획 메뉴 툴팁이 R00 이전의 '실제 자동 생성 비활성' 안내를 유지 | 로컬 NTFS/ReFS 운영 생성 지원·NAS 제외 안내로 수정 |
| R05-P1-04 | P1 | 클라우드 탐색창이 활성일 때 EIASS PDF 변경 메뉴가 아무 반응 없이 끝날 수 있음 | 서브메뉴 진입 시 실행 가능 상태 확인, 비활성 항목의 사유 툴팁 제공, 클릭 순간 cloud 재확인 |
| R05-P2-01 | P2 | 일괄변경 표에서 긴 파일명은 읽기 어려우며 대량 행 표시 최적화 부족 | 행 가상화, 말줄임+전체 툴팁, 상태 문구 줄바꿈 및 접근성 LiveSetting |
| R05-P2-02 | P2 | 좁은 메인창 헤더의 추가 명령이 좌우 이동 영역에 있을 때 사용자 안내 부족 | 기존 가로스크롤 보존, 전체 기능 안내 툴팁/접근성 이름 추가 |

## 5. 검증 증거 및 제한

- **전체 WPF 구조**: 22/22 XAML 파싱, 498개 이벤트에 대해 **해당 x:Class Owner**에서 핸들러 이름 확인 498/498, 미해결 0. 리소스 참조 570건 중 이름 미정의 0.
- **Python 변경영향**: `source/tests/test_v181_r05_full_app_audit.py` 및 R02~R04/클라우드/탐색·분할/메모 등 변경 소비자 계약을 실행한다. 검사는 소스 구조/문자열/결정적 모델이며 **실제 C#을 호출하지 않는다**.
- **Windows 전용 신규 테스트**: `source/tests/UiSmokeTests/R05BatchRenameAudit.cs`와 `UiSmokeTests --r05-batch-rename-only`. 임시 GUID 테스트 데이터에서 무효 번호·초과 길이·사전 취소·정상 변경 및 역변경 후 원본 바이트 보존 점검. **작성만 완료·실행하지 못함**.
- **불변성**: R04 캐시/분할 기능, 기존 EIASS 생성기, 회사 테마 토큰, 본주 v1.8.0 정식 Release/`latest.json`/`notice.txt`는 변경하지 않음.

## 6. 출시 전 잔여 필수 게이트 (R05 하위작업)

1. Windows 10/11 x64, .NET 10 WPF Release compile. 컴파일 오류 없고 신규 이벤트/클래스 연결 정상인지 확인.
2. 변경영향 전용 `UiSmokeTests --r05-batch-rename-only` 및 기존 `--eiass-rename-only`, `EiaWorkspaceProductionSmoke`, R04 페이지 실기 `--r04-recursive-page-only`를 **변경 항목 중심으로** 실행.
3. 실제 Windows 최소·기본 창 크기, 1/2/4분할, 100/125/150/175% DPI, 큰 글꼴/긴 경로·메뉴 가시성 스크린샷, 키보드·중지 버튼 실기 확인.
4. Cross-volume 이동 시 원본 보존, 권한 거부, 파일 점유, 링크/재분석 지점, 취소·복구 후 실 데이터 diff/해시 확인.
5. v1.8.0 → v1.8.1 설치/업데이트, 설정 보존, Control 호환, Authenticode 주 EXE/Setup 검증, 최종 SHA256 후 공식 Release 및 `latest.json` 갱신.

**최종 판정: 소스 정적 감사 및 R05 우선보완 진행. Windows/서명 설치 실기 전까지 정식 출시 보류.**
