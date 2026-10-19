# Sunrise File Manager v1.8.5 — AI Agent 5·6차 개발/인수인계서

## 기준·유지

기준: `Sunrise_File_Manager_v1.8.4_AI_AGENT_PHASE34_FULL_SOURCE_R01.zip`의 **전체 소스**. 원본 1~4분할 파일 탐색 UI, 가운데 상세정보·메모 열, 최우측 독립 미리보기 열, F8 파일 정리 도킹, PDF/HWPX/DOCX 로컬 본문 키워드 검색, `#memo` 이동·변경 동반, 충돌 승인·휴지통 2차 승인·작업 기록·복구 정책을 유지한다.

## 5차 — Peter 및 모델 어댑터

- `IAiIntentAdapter`, `AiOfflineIntentAdapter`, `AiPeterBridgeIntentAdapter` 도입. 기존 Gemini 고급 계획 생성(`AiGeminiPlanner`)도 보존.
- F8 → 자연어 명령의 AI 제공 방식에서 로컬(기본), Gemini(기존 고급 명령), Peter(인증 루프백 브리지)를 선택할 수 있도록 구성. 별도의 상시 AI 대화창을 만들지 않는다.
- Peter 브리지는 `/v1/intent`의 구체적 **신규 Sunrise v1 계약**으로 클라이언트 구현. 전달 데이터는 `schema_version`, `capability`, 사용자가 입력한 `request` 세 필드뿐. 자동 수집한 파일명·절대경로·본문 데이터 전송 없음.
- HTTP(S) loopback only, Bearer 인증 토큰 24~256자, 20초 제한, 16KiB 상한, 무프록시·무리다이렉트, JSON 중복/추가/필수 필드 차단. 보호·내부 자료 거부, 일반 자료에 대한 사용자 전송 승인 필수.
- Peter가 반환한 작업 유형·검색어·확장자·본문 조건·수정일·접두어/접미어에 대해 원래 사용자 요청에 실제로 포함된 내용인지 추가 검증. AI 결과는 파일을 수정할 권한이 없는 `Intent`만 허용.
- 기존 `AiAgentOrchestrator.PrepareIntent` → `AiPlanService.Validate` → 사용자 승인 → `AiPlanService.ExecuteAsync` 경로만 사용. Peter 모델을 통해 셸, 파일 시스템 변경, 원격 스크립트 실행 불가.
- **실제 Peter 서버의 기존 인터페이스는 제공되지 않았음.** 신규 계약의 클라이언트·호출 경계·테스트는 구현하였으나, Peter 실제 ToolRuntime/ModelGateway 구현과의 상호운용은 대기. 세부 계약: `source/peter_bridge/PETER_BRIDGE_V1.md`.

## 6차 — UI·검증·배포 안정화

- Windows UI 실기 테스트에 Peter 제공 방식의 F8 배치 검사·캡처(`p56-peter-ai-dock.png`) 추가. 독립 미리보기·메모 열, F8 하단 도킹, 최소 창 너비 기존 검증 유지.
- `VERIFY_UI_WINDOWS.ps1`에서 실제 생성된 PNG 파일의 SHA256·크기·Windows 버전과 생성 시점을 `UI_CAPTURE_EVIDENCE.json`에 기록하고 결과 ZIP 생성. 사용자 업무 파일이 아닌 독립 시험 폴더만 사용.
- 기존 빌드/Portable 배포 엔진과 테스트 해시 캐시 방식을 유지하고 버전을 v1.8.5로 조정. 소스 변경에 따라 `TESTED_SOURCES.json`은 stale로 표시하여 첫 Windows 빌드에서 신규 테스트를 반드시 실행하도록 함.
- 기존 `AiJournalStore`와 `AiRecoveryService`의 부분 완료·중단 재검증 흐름 유지. 자동 되돌리기는 약속하지 않으며 파일 변경은 승인 후 실행됨.

## 검증 한계 및 잔여 작업

Python 소스 계약·기존 회귀 테스트와 XML 정적 검사를 실행했으며, Linux에 .NET SDK/Windows WPF가 없어 신규 C# 테스트·실제 Windows 컴파일·실기 DPI/멀티모니터 캡처는 실행하지 못함. 배포용 EXE 미포함. Windows 테스트는 `BUILD_AND_CAPTURE_WINDOWS.cmd` 실행 후 생성되는 `source/WINDOWS_BUILD_RESULT.json`, `source/WINDOWS_UI_SCREENSHOTS.zip`을 근거로 검토.

실제 Peter 연동은 Peter 호스트측 신규 Bridge v1 계약 구현 및 인증·모델 호출·취소·에러 처리를 확인한 뒤 완료 판정할 것. Peter가 오프라인이어도 로컬 명령은 계속 동작하도록 설계.
