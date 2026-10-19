# Sunrise File Manager — 최신 OpenSource 전체 소스 빌드 수정 R03

- 기준본: 사용자 첨부 `Sunrise_File_Manager_OpenSource_Full(1).zip` (147개 파일)
- 비교본: 이전 `Sunrise_File_Manager_OpenSource_Full_BUILD_PIPELINE_R02.zip` (132개 파일)
- 기준본에는 비교본 대비 신규 모듈 15개가 추가되고 기존 일부 모듈이 수정되었음. **최신 첨부본을 기준으로 수정**하고 기존 모듈을 되돌리지 않음.

## 오류 원인과 최소 수정

`BUILD_ON_WINDOWS.cmd` 1/4 단계 LogicTests가 참조하는
`source/Sunrise.FileManager/Features/FileSystem/FileActionsService.ArchiveAndShell.cs`의
334~335행에서 C#의 잘못된 큰따옴표 문자열 표현으로 인해 CS1001, CS1002, CS8997이 발생함.

수정 (334~335행):

```csharp
string safeName = "\"" + item.Name.Replace("\"", "\"\"") + "\"";
string safePath = "\"" + item.FullPath.Replace("\"", "\"\"") + "\"";
```

CSV 내보내기 의도를 유지하면서 따옴표를 올바르게 이스케이프하고, 쉼표·따옴표 포함 텍스트를 하나의 필드로 저장할 수 있도록 변경.

## 추가 검증

- `source/tests/LogicTests/AiOllamaAndExplorerTests.cs`: CSV 특수문자 입력 회귀 테스트 추가.
- `source/tests/test_csv_export_build_regression.py`: 문제 구문의 재유입 방지 정적 테스트 추가.
- Python 계약/회귀 검사: 51건 통과.
- 정적 소스/XAML 감사: 382개 항목 통과.
- 독립 우측 열/레이아웃 검사: 144개 항목 통과.
- 이전 전체 소스 147개 파일 누락 없음. C# 실수정 대상 2개 파일(메서드 2줄 + 테스트 1건).
- 파일별 SHA-256 목록 재생성: `FULL_SOURCE_SHA256_MANIFEST.json`.

## 빌드 실행 및 한계

이 작업 환경에는 `dotnet` SDK 및 Windows WPF 실행 환경이 없고 외부 SDK 다운로드도 불가하여 실제 Windows 빌드 성공 여부는 미검증임.

Windows에서 새 폴더에 압축 해제 후 `BUILD_ON_WINDOWS.cmd` 실행:

1. `build-logs/01_logic_tests.log`를 통해 수정 구문을 포함한 LogicTests 확인
2. `build-logs/02_wpf_build.log`에서 WPF 컴파일 확인
3. 자체 포함형 win-x64 게시 및 Portable 전체 소스 ZIP 확인

새 빌드에서 추가 오류가 발생하면 해당 단계의 로그를 검토하여 다음 개정본에서 수정해야 함.
이 패키지는 사용자의 PC에 있는 이전 실행파일을 포함하거나 정상 빌드를 보증하지 않음.
