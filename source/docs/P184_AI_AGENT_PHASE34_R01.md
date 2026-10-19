# P184 — AI 파일 관리 Phase 3·4 작업 인수인계

## 원본 및 유지 항목

기준: 1.8.3 Phase 1·2 전체 소스. 기존 1~4분할 Windows 탐색기형 UI, 가장 우측 독립 미리보기 열, 그 좌측 상세정보/메모 열, 파일별 #memo, 기존 AI 검사, Gemini 고급 명령 및 Windows Portable 빌드 경로 유지.

## 3차: 파일 작업 도구 연결

- `AiNaturalLanguageService`: rename(requires prefix/suffix), recycle(explicit 휴지통) 추가. 요청 문장에 서로 다른 변경 작업이 섞이면 차단.
- `AiAgentOrchestrator.Prepare`: 탐색 후 Immutable `AiScope`와 계획 생성, `AiPlanService.Validate` 이용. 이름 변경·휴지통은 선택 항목 범위만 허용.
- `AiPlanService.BuildPlan`: 실행 단계에서 `FileActionsService.Rename`, `FileActionsService.Recycle`, `TransferService.RunAsync`를 계속 재사용. 이름 변경 경로는 원본 폴더 기준으로 생성.
- AI 휴지통 전송: 파일에 #memo 기록이 있거나 메모 손상이 확인되면 파일만 휴지통으로 보내어 연결 관계가 깨지지 않도록 전체 계획 실패. 폴더 선택 시 그 내부의 #memo는 해당 폴더와 함께 휴지통으로 이동(Windows Shell 동작에 따름). 일반 Windows 탐색기의 삭제 정책은 바꾸지 않음.
- 사용자 1차 승인 + 휴지통 2차 승인 유지. 영구 삭제 불허. 전 단계에서 원본/대상 변경 감지 시 새 계획 필요. 절대적인 롤백 보장은 없음.

## 4차: 본문 검색 및 분류

- `AiDocumentSearchService`: 사용자가 승인한 로컬 문서 키워드만 검색. 문서 500개, 원본 256MB 총량 제한. 파일당 PDF 35MB, Office 30MB 제한.
- `FilePreviewService.ReadSearchableDocumentText`: 기존 추출기 재사용. PDF는 첫 4쪽(문서 전체 검색 아님), HWPX는 저장된 이미지/미리보기 텍스트가 아닌 Contents/section XML의 앞 5개 섹션, DOCX는 word/document.xml, 각 최대 16,000자.
- 실제 XML/PDF 본문에 없는 인공 안내 문구/페이지 제목이 검색어에 일치하는 문제를 방지.
- 암호·손상·스캔 PDF·추출 불가 문서는 미확인 처리(일치 문서가 아님), 읽기 오류 수는 검색 화면 상태에 별도 표시.
- `AiContentClassifier`에 PDF/DOCX/HWPX를 지원 형식으로 추가. 여러 규칙에 일치하면 자동 이동·분류 제외.
- 전 과정에서 외부 모델에 문서 본문 전송하지 않음. AI 해석은 아직 *로컬 제한 규칙 기반*, 문서 의미적 유사도/요약/LLM 및 OCR 미구현.

## 검증과 미완료

정적 271/271 PASS, pytest 39 PASS, Windows 의존 8 SKIPPED. 신규 `AiAgentPhase34Tests.cs`는 실제 C# Windows 빌드에 포함되지만 현재 환경에서 미실행. Windows .NET 10 컴파일 및 GUI 캡처 필요. 새 실행 파일은 Windows에서 빌드 성공한 경우에만 생성됨.

## 주요 사용 예

1. 선택 파일 대상으로 `파일명 앞에 '완료_' 붙여줘` → 작업 계획 확인 → 승인.
2. 선택 파일 대상으로 `선택한 파일을 휴지통으로 보내줘` → 계획 확인 → 별도 휴지통 승인 (파일별 #memo가 있으면 중단).
3. `본문에 '안성천' 포함된 PDF 파일 찾아줘` → 로컬 본문 읽기 동의 → 검색 범위 지정 → 결과 확인.
4. `본문에 '수질' 포함된 HWPX 파일 복사해 줘` → 대상 폴더 지정 → 내용 동의 → 계획 확인 → 승인.
