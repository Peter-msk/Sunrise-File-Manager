# Sunrise File Manager 1.1.0 — R21 WPF 빌드 핫픽스

## 사용자 PC 실제 오류

- 빌드 진입점: `BUILD_ON_WINDOWS_COMPANY.cmd`
- 실패 단계: `WPF_PUBLISH` (인증서·서명·설치파일 생성 전에 중단)
- SDK: .NET 10.0.301, Windows x64
- 정확한 오류: `Services/ThemeManager.cs(83,16): error CS0246: The type or namespace name 'IOException' could not be found`

## 수정

- `source/Sunrise.FileManager/Services/ThemeManager.cs` 맨 위에 `using System.IO;` 한 줄 명시.
- WPF 임시 컴파일 프로젝트의 암시적 전역 using에 의존하지 않고 `IOException` 타입을 해석하도록 수정.
- 모든 애플리케이션 C# 파일의 비정규화된 `catch(IOException/FileNotFoundException/DirectoryNotFoundException)` 블록에 `using System.IO;`가 있는지 확인하는 회귀검사 추가.
- 프로그램 버전 `1.1.0`, 기존 중앙 Control 정책, 제품 공지·업데이트 경로, 서명 인증서, Inno Setup 스크립트, 공개 v1.0.0 피드는 변경하지 않음.

## 검증 범위

- Python 소스 회귀검사/패키징 계약검사/XAML XML 구문/ZIP 무결성 검사는 이 패키지에서 실행.
- **Windows 실제 WPF 컴파일, 인증서 서명, Inno Setup 컴파일 및 설치 실행은 테스트 PC에서 다시 확인해야 함.**
- 이번 로그에는 CS0246 한 건만 나타남. 이를 해결한 후 새 컴파일 오류가 나타날 가능성을 배제할 수 없으므로 다음 로그를 확보하여 추가 분석.

## 사용방법

1. 새 전체 소스를 별도 폴더에 압축 해제.
2. `BUILD_ON_WINDOWS_COMPANY.cmd` 하나를 실행.
3. 실패 시 `build-logs/R14_wpf_publish.log`, `R14_BUILD_RESULT.txt`를 전달.

급한 경우 기존 R20 소스의 동일한 C# 파일 최상단에 `using System.IO;` 한 줄을 추가한 뒤 빌드를 재실행해도 동일한 단일 오류가 수정됩니다.
