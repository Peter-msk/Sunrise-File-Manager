# Sunrise File Manager v1.8.1 R01 — 외부 GitHub 공개 저장소 벤치마킹 및 코드 안정화

- 조사 기준일: 2026-11-13 (KST)
- 조사 대상: **사용자 GitHub 저장소가 아닌 공개된 타사 오픈소스 GitHub 저장소**
- 코드 반영 대상: Sunrise File Manager 설치형 v1.8.1 후보 브랜치 `feature/eiass-v1.8.1-activation`
- 주의: GitHub star 수는 조사 시점의 값이고 수시로 변동. 높은 star는 코드의 적합성·보안성을 자동 보장하지 않는다.
- 라이선스: 실제 외부 소스 코드/디자인 에셋은 복제하지 않음. 개념과 구현 패턴을 조사하여 Sunrise 기존 구조에서 자체 작성. 새 NuGet 및 UI 프레임워크 의존성 없음.

## 공개 오픈소스 9개 비교 (별점 기준 표본)

| 공개 저장소 | stars(2026-11-13) | 직접 확인한 구현/근거 | 평가/채택 |
|---|---:|---|---|
| [Microsoft PowerToys](https://github.com/microsoft/PowerToys) | 140,923 | [PowerRename 아키텍처·미리보기](https://github.com/microsoft/PowerToys/blob/main/doc/devdocs/modules/powerrename.md) / [Rename 단위 테스트](https://github.com/microsoft/PowerToys/blob/main/src/modules/powerrename/unittests/PowerRenameManagerTests.cpp) | 제출용 파일 일괄 변경의 실행 전 미리보기·취소·Undo 재검토. 기존 파일명 변경 엔진은 수정하지 않고 현행 검증 유지 |
| [Files](https://github.com/files-community/Files) | 46,601 | [파일 작업 진행·취소](https://github.com/files-community/Files/blob/main/src/Files.App/Utils/Storage/Operations/FileOperationsHelpers.cs), [CancellationTokenSource 수명](https://github.com/files-community/Files/blob/main/src/Files.App/ViewModels/Properties/HashesViewModel.cs) | EIASS 작업 진행률·중단·결과 확인 보완 채택 |
| [Material Design in XAML](https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit) | 16,302 | [WPF Theme/Control 개요](https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit/blob/master/README.md) | 색상·레이아웃 전략 참고. 기존 Sunrise 공식 테마 변경/대규모 의존성 추가 거부 |
| [MahApps.Metro](https://github.com/MahApps/MahApps.Metro) | 9,839 | [TextTrimming 적용된 WPF MetroWindow](https://github.com/MahApps/MahApps.Metro/blob/develop/src/MahApps.Metro/Themes/MetroWindow.xaml) | 긴 경로·상태를 말줄임하고 전체값은 Tooltip으로 제공: 채택 |
| [FluentValidation](https://github.com/FluentValidation/FluentValidation) | 9,776 | 강력한 형식 검증 라이브러리 | 경로 검증 단계화 참고. 대형 의존성 추가 없음 |
| [WPF UI](https://github.com/lepoco/wpfui) | 9,752 | [SnackbarPresenter의 큐/취소 상태](https://github.com/lepoco/wpfui/blob/main/src/Wpf.Ui/Controls/Snackbar/SnackbarPresenter.cs) | 재점검/생성/완료/오류 상태의 분명한 구분 채택; 별도 Snackbar DLL 추가하지 않음 |
| [Serilog](https://github.com/serilog/serilog) | 8,069 | [구조화 이벤트 로깅](https://github.com/serilog/serilog/blob/dev/README.md) | 향후 로그 표준 후보; 민감 경로 노출·불필요 로그 파일 생성 우려로 현재 추가하지 않음 |
| [dotnet/wpf](https://github.com/dotnet/wpf) | 7,754 | [WPF 고DPI/바인딩/벡터 UI](https://github.com/dotnet/wpf/blob/main/README.md) | 기존 WPF 사용 유지; 고DPI 실기 시각 검증 대상 |
| [.NET CommunityToolkit](https://github.com/CommunityToolkit/dotnet) | 3,773 | [AsyncRelayCommand IsRunning/CanBeCanceled 상태](https://github.com/CommunityToolkit/dotnet/blob/main/src/CommunityToolkit.Mvvm/Input/AsyncRelayCommand.cs) | 중복 실행 금지·취소 중 컨트롤 잠금 채택; 기존 코드비하인드에 최소 구현 |

## 반영한 R01 변경

1. **비동기 상태 안정화**: 재점검/생성 중 이중 실행 금지, 재점검 중 사업명 입력·확인 버튼 잠금. `OnClosing`에서 중단 요청 후 작업 완료까지 검토창 파괴 방지(부분 생성 결과 보존·확인 가능).
2. **진행률 UI**: WPF `ProgressBar` 추가. 재점검은 숫자형 진행률, 운영 생성은 사전 재검증 동안 indeterminate로 시작하고 작업 진행이 보고되면 determinate로 전환. 중지/완료 시 정리.
3. **레이아웃 및 접근성**: 사업명 확인 필드 확대(160 → 240 WPF 단위), 긴 상태값은 말줄임, 전체 경로 및 지문 `ToolTip`, 작업 상태 `AutomationProperties.LiveSetting=Polite`.
4. **작업 완료 UX**: 검증된 사업 폴더만 Windows Explorer에서 여는 `결과 폴더 열기` 버튼 추가. 디렉터리 재확인이 실패하면 버튼 비활성화하며 성공/실패 통계 표시는 보존.
5. **기존 생성 안전 경계 유지**: 생성 대상은 기본안·검증된 로컬 NTFS/ReFS. 타사 코드 복제 없음. NAS/SMB 및 업데이트 배포는 미승인. 파일명 변경 엔진의 rename+rollback 로직은 임의로 변경하지 않음.

## 검증 범위·잔여 사항

- Python 정적 계약 검사: `source/tests/test_v181_eiass_production_contract.py` + `test_v181_eiass_external_benchmark_r01.py` 실행 (결과 별도 확인).
- 외부 GitHub 실측 star 수는 사용자 개인 저장소와 무관함.
- 미검증: Windows .NET 10 C# 컴파일, WPF 100/125/150/175% 배율 실제 스크린샷, 예외/타임아웃/파일잠금/취소 경쟁상태, NTFS 권한·장기경로·롤백, 서명 설치 및 v1.8.1 정식 배포.
- 운영 v1.8.0 배포·`latest.json`·공식 코드서명 상태는 수정하지 않음. R01은 코드 개선 후보이며 릴리즈 판정으로 오인 금지.
