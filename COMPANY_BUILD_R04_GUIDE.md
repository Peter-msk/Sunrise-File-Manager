# Sunrise File Manager R04 — 회사 전용 빌드 안내

## 목적

회사 SecureDisk 정책을 존중하여 **빌드 과정에서 PDF/HWPX/DOCX 등 문서를 열거나 내용을 검사하는 테스트를 실행하지 않음**. 차단된 자료에 우회 접근하지 않으며, SDK·WPF 컴파일·Portable 게시 단계는 정상적으로 유지함.

## 사용 방법

- 회사 PC에서는 최상위 `BUILD_ON_WINDOWS_COMPANY.cmd`를 실행함.
- 개인 개발 PC에서는 기존 `BUILD_ON_WINDOWS.cmd`를 그대로 사용함. 기존 개발 빌드에는 LogicTests가 그대로 포함됨.
- 회사 모드는 `BUILD_ON_WINDOWS.cmd --company`로도 실행 가능함.
- SDK: .NET 10 x64, Windows 10/11. Ollama는 빌드에 필요 없음.

## 회사 모드에서 실행 / 미실행

| 단계 | 동작 | 비고 |
|---|---|---|
| 1/4 LogicTests | **SKIPPED** | PDF Fixture 복사·문서 본문 읽기·테스트용 파일 열기 생략 |
| 2/4 WPF 컴파일 | **실행** | C# / XAML / 리소스 컴파일. 사용자 문서 접근 없음 |
| 3/4 Portable 게시 | **실행** | self-contained win-x64 필수; 런타임 포함 |
| 4/4 전체 소스 패키징 | **실행** | `Fixtures` 및 문서 확장자 제외. C#·XAML·빌드 소스 보존 |

> 주의: 회사 모드는 LogicTests를 생략하므로 **단위 테스트 PASS를 의미하지 않음**. 문서 읽기·파싱 시험과 일반 비즈니스 로직 시험은 허용된 개발 PC에서 수행해야 함. 회사에서도 WPF 컴파일이 실패하면 로그로 확인 가능.

## 원인 및 처리

이전 `MSB3030`은 `LogicTests.csproj`에 지정된 `Fixtures/river_report.pdf`의 출력 디렉터리 복사 도중 발생했음. 반드시 SecureDisk 정책 때문에 발생했다고 단정할 수는 없으며, 경로 인코딩 또는 파일 접근 오류 가능성도 존재함. 회사 모드는 관련 테스트 프로젝트를 실행하지 않으며, 해당 리소스 항목도 `SunriseCompanyBuild=1`일 경우 포함하지 않도록 조건부 처리함.

## 회사 빌드 출력

- `build-logs/01_logic_tests.log`: `SKIPPED=COMPANY_DOCUMENT_ACCESS_POLICY`
- `build-logs/BUILD_RESULT.txt`: 최종 `MODE=1` 및 단계별 상태
- `dist/SunriseFileManager_Portable_COMPANY_R04/app/Sunrise.FileManager.exe`
- `dist/SunriseFileManager_Windows_x64_Portable_FullSource_COMPANY_R04.zip`
- `dist/.../COMPANY_BUILD_POLICY.txt`: 수행·생략 정책 요약

별도 출력 경로를 사용하여 기존 R02 빌드 결과를 덮어쓰지 않음.

## 보안·운영 제약

- 이 모드는 **빌드 절차만** 변경함. 실행 후 프로그램의 파일 미리보기·문서 분석 메뉴 자체를 무력화하거나 SecureDisk를 우회하는 코드는 추가하지 않음.
- 사용자가 금지된 문서를 프로그램에서 열려는 경우 회사의 접근 통제를 그대로 존중해야 함.
- 회사 모드는 문서 내용을 읽거나 네트워크로 전송하는 검사를 수행하지 않음.
- 소스 내 테스트 Fixture는 개발용 원본 소스에 유지되지만 회사 빌드 결과 패키지에는 포함하지 않음.
- 초기 ZIP 압축 해제는 Windows의 정상 파일 추출 작업으로 별도로 진행됨. SecureDisk 정책상 파일 추출 자체가 금지되는 폴더에서 우회 실행하지 말 것.

## 검증 경계

R04 소스는 Linux 환경에서 빌드 스크립트 정적 검사 및 기존 Python 회귀검사를 수행함. Windows SDK / WPF 컴파일 및 회사 SecureDisk 환경에서의 실제 동작은 이 대화 환경에서 재현되지 않았으며, 회사 PC에서 `BUILD_ON_WINDOWS_COMPANY.cmd` 실행 결과가 필요함.

## 회사 전용 배포본의 실수 방지

`COMPANY_SOURCE_ONLY.flag`가 있는 회사 전용 소스 압축파일에서는 일반 `BUILD_ON_WINDOWS.cmd`를 실수로 실행해도 회사 모드를 자동 선택함. `BUILD_AND_CAPTURE_WINDOWS.cmd`는 문서 접근 위험을 방지하기 위해 회사 전용 소스에서 실행을 거부함. 개발용 전체 소스 ZIP에는 이 표시 파일이 없어서 기존 테스트를 정상 실행할 수 있음.
