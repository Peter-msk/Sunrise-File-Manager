# Sunrise File Manager v1.8.1 R05 — 코드/메뉴/UI·UX 감사 및 변경영향 검증보고

**일자:** 2026-11-15 (KST). **기준**: R04 소스 ZIP 및 GitHub feature PR #3 (기준 커밋 `ffbdc1e691f3a4a31db9cf3d658c3bd31771241b`). **프로필:** 설치형 Windows C# WPF.

## 수행

1. 소스 패키지 ZIP 전개/CRC 전수 확인. 사용자 실제 파일/클라우드/사업폴더에는 접근하거나 쓰지 않음.
2. 외부 공개 GitHub PowerToys/Files/WPF UI/MahApps/CommunityToolkit 실제 저장소 및 소스 구현 패턴 조사. 현행 Sunrise UI/Foundation/Local Owner 우선, 새 패키지 도입하지 않음.
3. 전체 C# 파일 190개, XAML 22개, 메뉴 항목 249개, 버튼 201개, TextBox 46개, DataGrid 23개 구조 재고조사. 해당 화면 Owner의 XAML 이벤트 498건 중 미해결 0. UI Key 106개/570회 이름 참조 중 미정의 0.
4. R05 P0/P1: 일반 일괄 이름변경 blocking, 취소 접근성, 입력 무효번호 대체, 이름 길이 검사, EIASS 메뉴 상태/안내, 가로스크롤 안내 등 수정. 기존 파일 전송/테마/분할 변경 없이 Local Owner만 수정.
5. R05 + R04/R03/R02/EIASS 주변 계약 및 MainWindow/파일복구/Cloud/폴더동기화/메모/접근성 영향 범위 Python 검사 수행.

## 실제 테스트 결과

| 검사 | 결과 |
|---|---|
| 변경영향 및 주변 소비자 Python 19개 테스트 파일 | **195 PASSED**, 추가 41 subtests PASSED |
| XAML/XML 전체 파싱 | **22/22 PASS** |
| WPF x:Class 소유자 이벤트 연결 | **498/498 PASS**, 이름 미해결 0 |
| 정적 리소스 참조 | **570/570 이름 정의 확인**, 미정의 0 |
| 코드 리뷰 시 변경한 순수 UI/XAML 핸들러 | Source static contract PASS |
| Windows `dotnet build` / WPF 실제 실행 | **NOT RUN** (Windows/.NET10 없음) |
| Windows 신규 `--r05-batch-rename-only` 실기시험 | **NOT RUN** (테스트 코드 작성/포함만 완료) |
| Authenticode / 실제 Setup / 설치 및 업데이트 | **NOT RUN / NOT RELEASED** |

**진단 이력:** 첫 주변회귀 실행에서 역사 R02의 소스 텍스트에 정확한 `if (...) return;`를 요구하는 테스트 1건이 실패했다. 기존 공통 shell 차단 계약을 원래 형태로 복원하고 EIASS 전용 사용 안내를 그 앞에 배치해 의미를 보존함. 같은 영향범위를 재검사하여 **최종 195 PASS, 41 subtests PASS**로 확정. 오래된 v1.8.0의 '자동 생성 불가'를 필수 툴팁으로 가정하는 역사 테스트는 1.8.1 현행 코드와 의미가 달라 현행 검증에서 제외함.

## Windows에서 다음 실행할 정확한 확인

- Windows 10/11 x64, .NET 10에서 코드 컴파일: `dotnet build source/Sunrise.FileManager/Sunrise.FileManager.csproj -c Release`.
- 신규 고립 WPF: `dotnet run --project source/tests/UiSmokeTests -- --r05-batch-rename-only <독립증적폴더>`.
- 관련 현행 EIASS: 기존 `UiSmokeTests --eiass-rename-only`, `EiaWorkspaceProductionSmoke`.
- R04 대량 뷰: `UiSmokeTests --r04-recursive-page-only`.
- Windows UI: 100·125·150·175% DPI, 최소/기본 크기, 1·2·4분할, 메뉴 이용·취소 중 원본/Undo 확인.
- 파일 실기: NTFS ACL/충돌/중단, cross-volume 원본/복사본/메모 상태; cloud/AI는 실제 운영 자격 증명과 승인된 테스트 계정에서 검증.

Windows에서 PASS했던 무관한 검사는 재실행하지 않고, 위 변경영향 항목만 추가/재검증한다. 공식 `main`/v1.8.0 릴리즈/`latest.json`/`notice.txt`는 보존. **이 보고서의 총 PASS 수는 동작·시각·설치 전체 출시 게이트 PASS가 아니다.**
