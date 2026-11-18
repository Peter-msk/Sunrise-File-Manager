# Sunrise File Manager v1.8.1 R05 — 출시 전 개선 우선순위 및 단계별 계획

## 기준·보존 계약
- R04 소스·기존 디자인 승인/분할 계약 유지. 신규 외부 UI 프레임워크 대체 금지.
- 1차: 전체 WPF 메뉴/기능 연결, 리소스, UI 구조 감사. 2차: P0 파일보존/취소, P1 메뉴·입력·미리보기, P2 시각·접근성.
- 원본 사용자 파일, 클라우드 파일, 실제 사업폴더는 검사 과정에서 수정하지 않는다. 테스트는 격리 Temp 경로만 사용.

| 단계 | 소유 영역 | 산출물 | 검증 기준 | 상태 |
|---|---|---|---|---|
| R05-A | 전체 App/메뉴/Resource | WPF 22화면+249 메뉴·498 이벤트+570 리소스 참조 매트릭스 | XAML/XML, 이벤트 owner, resource-key | 정적 완료 |
| R05-B | 로컬 FileActions/Undo | 일괄 rename 비동기·협력 취소·inverse 보존 | 신규 계약검사 + Native R05 smoke | 코드 완료 / Native 미실행 |
| R05-C | BatchRenameDialog | 숫자·경로 길이 검증 및 대용량 표 UX | UI 계약검사 + Windows 시각 | 코드 완료 / 시각 미실행 |
| R05-D | EIASS 및 Shell | 메뉴 활성 상태/도움말/가로 스크롤 UX, 상태표시줄 취소 버튼 | owner event contract + Windows 동작 | 코드 완료 / Native 미실행 |
| R05-E | Cloud/AI/미리보기/테마 | 기존 기능 영향/회귀 검사 | 관련 contract + 실제 클라우드/모델/디스플레이 | 영향 정적 완료 / 서비스 실기 미실행 |
| R05-F | Windows Release Gate | signed v1.8.1 Setup/Update | 네이티브·DPI·권한/실기·서명·해시 | 보류 |

코드 변경은 실제 결함이 드러난 Local Owner의 8개 C#/XAML 및 2개 WPF 동작 참조로 제한. 전체 UI를 다른 프레임워크로 갈아끼우지 않는다. 새 테스트 파일은 R05 소스 ZIP에 포함하며, GitHub에 없는 이전 C# 테스트 전체를 불완전하게 따로 복제하지 않는다.
