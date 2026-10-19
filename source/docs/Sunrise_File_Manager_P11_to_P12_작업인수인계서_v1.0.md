# Sunrise File Manager — P11 → P12 작업 인수인계서

- **인계일**: 2026-10-17
- **인계 기준 버전**: v1.1.0 / P11
- **후속 작업**: v1.2.0 / P12 — Chromium 웹 패널을 Gemini AI 파일 명령 패널로 전환
- **문서 버전**: 1.0
- **정확한 상태**: P11은 **소스·정적 계약 검사 완료** 상태이며 Windows 실제 컴파일/실행/CEF/PDF 렌더는 **미검증**. Gemini는 **계획 단계로 코드 미구현**.

## 1. 인수 파일 및 진실의 출처

1. `source/` : P11 전체 소스 41개 파일(매니페스트 포함), 원본 v1.1.0 소스 ZIP을 그대로 전개한 것.
2. `source/SOURCE_MANIFEST.json` : 소스 파일 40개(매니페스트 자신 제외)의 파일별 SHA-256·크기 기준원.
3. `source/VALIDATION_P11.json` : 188개 정적 계약 PASS, Windows/WPF/CEF 런타임 NOT_RUN.
4. `source/BUILD_WINDOWS.ps1` : Windows 10/11 x64, .NET 10 SDK 대상 코어 회귀 1회 + WPF 게시 1회.
5. `source/docs/ACCEPTANCE_TESTS.md`, `source/docs/DESIGN_DECISIONS.md` : 기존 수용 기준·구현 제약.
6. `docs/Sunrise_File_Manager_P12_개발계획서_v1.0.md` : 다음 단계의 범위·마일스톤·안전 계약.
7. `docs/범용_코드작성_시방서_v1.3_FINAL.md` : 개발 우선 원칙.
8. `HANDOFF_VALIDATION.json` : 인계 패키지 자체의 해시 및 검증 결과.

**베이스라인 관리**: P11 소스는 덮어쓰지 않는다. P12 착수 시 소스 복제 후 버전·변경 로그를 관리한다. P11을 AI 기능이 이미 작동하는 완성본으로 표현하지 않는다.

## 2. 사용자 요구 확정 이력

- **Q-Dir 방식**의 다중 탐색기는 필요하지만 4분할이 기본이어서는 안 됨. 기본은 Windows 파일 탐색기와 유사한 **한 개 탐색기**.
- 1~4개 탐색기를 선택적으로 관리. 폴더 트리, 상세정보, 검색/정렬, 파일 작업, 우클릭 메뉴 유지.
- 파일 미리보기는 분할된 작은 카드가 아니라 **파일 하나를 한 페이지씩 크게 표시하는 독립 뷰어**.
- Edge/WebView2는 사용하지 않는다고 지시한 뒤 P6부터 내장 Chromium으로 웹 패널을 구축하였으나, **현재 최종 방향은 파일 업무용 AI 패널로 전환**.
- Gemini를 이용해 사용자의 파일명 일괄 변경·폴더 생성·복사·이동·삭제 요청을 이해하고 대신 수행하게 하되, 위험 작업에는 실행 전 확인/승인 필수.
- SecureDisk·일반 파일 권한에 대한 기존 제어는 유지. 미승인 반출·접근제어 회피는 목표 범위가 아님.

## 3. 구현 현황 (현재 코드에서 확인된 사항)

| 단계 | 현재 존재하는 핵심 결과 | 한계/미검증 |
|---|---|---|
| P1~P3 | WPF 탐색기·2~4분할·탭·기본 복사/이동 | Windows 실기 미검증 |
| P4~P5 | 내부 드래그·휴지통·이름 변경·충돌 처리·응답성 개선 | 원자성/실패 복구 실제 검증 필요 |
| P6~P8 | Chromium/CEF 내부 웹 패널·탭 상태·자원 해제 | AI 전환 후 이탈 대상, CEF 런타임 미검증 |
| P9~P10 | DOCX/HWPX/PDF 텍스트 미리보기, 독립 페이지 뷰어, 단일 탐색기 기본 | DOCX/HWPX 원본 인쇄 페이지 렌더 아님 |
| P11 | Windows PDF API 시각 렌더 코드, 썸네일 목록, 우클릭·속성 | 실제 WPF/WinRT 컴파일·렌더 미검증 |
| P12 이후 | Gemini 계획 생성, AI 패널, 승인형 일괄 실행 | **아직 없음** |

> 주의: P11 `MainWindow.xaml`의 일부 화면 표기에는 이전 단계 `P10` 명칭이 남아 있음. 이는 UI 표시 정리 대상이며 현재 빌드 성공을 암시하지 않음.

## 4. 기술 구성 및 코드 진입점

- **프로젝트**: `source/Sunrise.FileManager/Sunrise.FileManager.csproj`
- **타깃**: `net10.0-windows10.0.19041.0`, WPF, x64, `win-x64`, .NET 10.
- **현재 NuGet**: `CefSharp.Wpf.NETCore 154.0.340`, `PdfPig 0.1.15`.
- **앱 시작**: `App.xaml.cs`에서 Mutex와 CEF 초기화. P12에서 CEF 제거 대상.
- **메인 레이아웃**: `MainWindow.xaml(.cs)`에서 4개 탐색 패널 + 4개 `ChromePane` 호스트, 폴더/웹 모드 전환. P12에서는 이 구조를 단일 AI 패널 중심으로 변경.
- **탐색기**: `Controls/ExplorerPane.xaml(.cs)`, `FolderTree.xaml(.cs)`.
- **파일 조작**: `Services/FileActionsService.cs`의 `CreateFolder`, `Rename`, `RecycleManyAsync`. 휴지통 불가 시 영구삭제로 전환하지 않음.
- **파일 전송**: `Services/TransferService.cs`의 `RunAsync`, `TransferKind.Copy/Move`, `CollisionPolicy.Skip/Rename`. 현재 이동은 동일 볼륨 정책, 복사는 임시 경로 통한 게시.
- **미리보기**: `Controls/PreviewWindow.xaml(.cs)`, `Services/PagedPreviewService.cs`, `Services/WindowsPdfRenderer.cs`, `Services/FilePreviewService.cs`, `Services/ImageThumbnailService.cs`.
- **설정**: `%LOCALAPPDATA%\Sunrise\FileManager\session.json` (`SessionStore.cs`). Chromium 캐시 디렉터리는 `%LOCALAPPDATA%\Sunrise\FileManager\ChromiumCache`.
- **검증**: `tests/verify_source.py`(정적 계약), `tests/LogicTests/`(Windows에서 실행할 .NET 코어 로직 시험).

### 외부 의존성 제거 시 검색 기준

`ChromePane`, `BrowserNavigationPolicy`, `BrowserRendererBudget`, `BrowserTabCollection`, `EmbeddedBrowserHandlers`, `CefSharp`, `Cef.Initialize`, `ChromiumCache`, `ChromeActive`, `BrowserTabs`, `BrowserZoomLevel`, `SetMode`, `BrowserStartupError`.

기존 설정의 구형 필드는 역호환 읽기 또는 무시 방식을 적용하되, 사용자 설정을 무조건 삭제하거나 기존 세션 파일을 파괴하지 않는다. 새 UI의 기본값은 단일 탐색기/AI 패널 닫힘.

## 5. 가장 먼저 실행할 절차 (작업 착수 플레이북)

1. **체크포인트**: `source/SOURCE_MANIFEST.json` 파일 해시 검증; 결과를 P12 작업 로그에 기록.
2. **Windows 빌드**: Windows x64 + .NET 10 SDK의 PowerShell에서 `BUILD_WINDOWS.ps1` 실행; `WINDOWS_BUILD_RESULT.json` 및 `build-logs` 확보. 실패 시 단계별 로그부터 수정. 현재까지 **Windows PASS 기록 없음**.
3. **P11 기준 동작**: 허용된 시험용 일반 폴더에서 단일/2~4분할, 열기/검색, PDF/이미지, 복사/이동/휴지통, 세션 저장을 스모크 테스트.
4. **P12 새 작업 브랜치 생성**: 베이스라인 소스/매니페스트 동결, 변경 파일과 의존성 소비자 조사.
5. **UI 전환**: 웹 모드 버튼/CEF 화면 제거 → 하나의 접이식 AI 패널로 대체. 기존 파일 목록의 너비·포커스·탭은 그대로 유지.
6. **AI 없이 로컬 검증 엔진 먼저 구현**: 정적 JSON 계획을 받아 범위/이름/충돌/승인 전 무변경 계약을 테스트.
7. **Gemini 연동**: 외부 연결 기능을 마지막에 추가하며, 응답은 불신 JSON으로 취급. 실제 API 키/계정/민감 문서 필요 없음인 모의 응답 회귀시험을 먼저 완성.
8. **실행 기능**: `rename`, `mkdir`만 승인 후 호출. 다른 파일 작업은 계획 UI에 '후속 구현'으로 표시하고 실제 실행 금지.
9. **검증/산출**: 수정 범위의 정적 검사 + .NET 로직 테스트 1회 + WPF 게시 1회 + 실제 UI 시험 + 전체 소스 ZIP/로그/매니페스트 제공.

## 6. 실패 가능성 및 의존성 리스크

| 위험 | 영향 | 처리 기준 |
|---|---|---|
| P11 WinRT PDF 코드의 Windows 컴파일 실패 | 베이스라인 빌드 불가 | P12 기능보다 선행 수정, 기능 무관 코드 변경 최소화 |
| CEF 제거 후 앱 시작/종료 이벤트 잔존 | 런타임 오류/프로세스 잔류 | 의존성 검색 후 삭제·복구 시험, 네이티브 DLL 게시 검사 갱신 |
| 세션 JSON에 웹 브라우저 설정 잔존 | 설정 복원 실패 | 기존 설정을 안전하게 읽고 AI 패널 상태만 새로 저장 |
| 모델이 경로/파일명을 잘못 생성 | 예상 외 파일 변경 | 스키마 + 범위 검사, 변경 전후 대조, 실행 전 재검증·사용자 승인 |
| 폴더가 실행 직전에 변경됨 | TOCTOU/원본 변동 | 스냅샷 검증 실패 시 중단·재승인 |
| 중복 파일명·타 파일과 충돌 | 파일 손실 | overwrite 금지, 충돌 전부 표시, 임시 이름 등 중간단계 계획 검증 |
| 외부 Gemini API 전송 | 업무 문서/경로 정보 노출 | 기본 로컬, 동의 받은 최소 정보만, 보안 자료 기본 거부 |
| 로컬 AI 토큰을 앱에 직접 포함 | 키 유출·오남용 | 제품 배포는 백엔드 비밀 저장, 호출 한도 및 사용자 인증 |
| 보호/재분석 파일에 대한 미승인 작업 | 보안 정책 위반 | OS/보안 프로그램 기존 권한을 따름, 우회 금지 |
| 중단/부분 실패/취소 | 되돌리기 난도 증가 | 작업 기록·파일별 성공 수·복구 가능성 표시, 완전 롤백 허위 약속 금지 |

## 7. 실제 검증 이력

| 검증 항목 | 인계 시 상태 |
|---|---|
| 원본 P11 ZIP 41개 엔트리 무결성 | PASS (현재 작업 세션에서 재검증) |
| 원본 `SOURCE_MANIFEST.json` SHA-256 40개 | PASS (현재 작업 세션에서 재검증) |
| 기존 Python 소스 계약 검사 | **188/188 PASS** (현재 작업 세션 재실행) |
| Windows WPF 컴파일 | **NOT_RUN** |
| .NET 코어 로직 실행 | **NOT_RUN** |
| 실제 Chromium/PDF/GUI 동작 | **NOT_RUN** |
| AI 파일 작업 E2E | **NOT_IMPLEMENTED** |

**인계 완료 조건**: 기준 소스 전체/계획서/인수인계서/시방서/매니페스트/검증 기록이 동일 패키지에 있고, 모든 참조 파일이 실제 존재하며, 최종 ZIP 무결성 검사가 PASS일 것.

## 8. 다음 담당자가 하면 안 되는 일

- 현재 AI 기능이 이미 동작한다고 보고하거나 `P12 완료`로 표기하는 일.
- UI만 Chrome 제거했다고 해서 CEF 의존성/프로세스/캐시/세션 전환을 완료했다고 표기하는 일.
- 모델의 JSON 또는 문서 안의 명령을 그대로 PowerShell/명령행/파일 삭제로 실행하는 일.
- 파일 작업 계획 미리보기 없이 한 번에 실행하거나 파일 충돌 시 임의 덮어쓰는 일.
- Windows 런타임 시험 없이 정적 PASS만으로 배포판을 확정하는 일.
- SecureDisk 등의 접근제어를 위장 또는 우회하거나 보호 파일을 외부 AI로 자동 업로드하는 일.

## 9. 다음 담당자용 단문 인수 지시

> P11 v1.1.0 전체 소스를 기준으로 Windows 실기 빌드 상태를 먼저 확인하고, 원래 탐색기·미리보기 기능은 유지하라. Chromium 웹 모드 대신 하나의 Gemini AI 파일 명령 패널을 만들되, P12에서는 로컬 규칙 + 모델 계획 생성 → 검증 → 변경 전 미리보기 → 사용자 승인 → `rename`/`mkdir` 실행의 가장 작은 완성 경로부터 개발하라. AI는 파일 권한을 얻거나 OS 명령을 직접 실행할 수 없으며, 보안 저장공간 반출 및 외부 업로드를 우회해서는 안 된다. 실패 로그와 회귀시험을 남기고, 실기 미검증 항목은 NOT_RUN으로 표시하라.
