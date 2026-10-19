# Build Fix Company R04 — 사용자 제공 R03 전체 소스 기준

## 기준 및 장애

- 기준: `Sunrise_File_Manager_OpenSource_Full_BUILD_FIX_R03.zip` (기존 150개 파일)
- Windows 오류: `LogicTests.csproj`의 `Fixtures/river_report.pdf`가 출력 폴더로 복사되지 않아 `MSB3030`.
- 이 메시지만으로 회사 보안 정책이 직접적인 실패 원인임을 확정할 수 없음. 다만 사내 SecureDisk 정책에 맞추어 문서 읽기·미리보기·검사 테스트를 회사 빌드에서 사용하지 않도록 함.

## 코드 변경

- `BUILD_ON_WINDOWS_COMPANY.cmd` 신규: 회사 전용 실행 진입점.
- `BUILD_ON_WINDOWS.cmd`: `--company` 및 회사 전용 소스 표시 파일 자동 감지. 회사 모드에서는 LogicTests 실행을 생략하고 WPF 빌드·게시·패키징만 수행함.
- `BUILD_ON_WINDOWS.cmd`: 회사 모드에서 `source/tests/**/Fixtures`, PDF/HWPX/DOCX 등 문서 확장자 파일을 패키징에서 제외함. 회사 결과물을 R02 기본 결과물과 별도 출력 경로에 저장함.
- `source/tests/LogicTests/LogicTests.csproj`: 회사 모드 `SunriseCompanyBuild=1`에서는 테스트 PDF 파일 복사 항목 제외.
- `BUILD_AND_CAPTURE_WINDOWS.cmd`: 회사 전용 소스에서는 UI 스모크 테스트 실행을 명시적으로 차단.
- README 및 정책 안내, 정적 계약 테스트 추가.

## 회사 전용 소스 ZIP의 특성

- 테스트용 `river_report.pdf`는 회사 소스 ZIP에 포함하지 않음. 프로그램 C#·XAML 소스와 테스트 C# 소스는 유지함.
- `COMPANY_SOURCE_ONLY.flag` 포함: 회사 ZIP을 풀고 일반 CMD를 실수로 실행해도 회사 모드가 자동으로 선택됨.
- 법률·보안 정책에 따라 제한된 파일을 열거나 읽도록 우회하지 않음.
- 일반 개발용 소스 ZIP에는 테스트 PDF가 유지되며 `BUILD_ON_WINDOWS.cmd`는 기존 LogicTests 실행을 유지함.

## 검증 한계

- Python 정적 계약·회귀검사와 압축 구조 검증을 수행함.
- 현재 작업 환경에 Windows WPF/.NET SDK가 없으므로 실제 C# 빌드, 게시, 사내 SecureDisk 정책 환경에서의 검증은 미수행함.
- 회사 빌드의 LogicTests 상태는 **SKIPPED**이며, PASS가 아님. 변경 없는 소스 기능의 실동작은 별도로 검증 필요함.
