# Sunrise File Manager — current source pointer (v1.8.1 R05 verification)

- 제품 코드 기준: v1.8.1 R05, GitHub Draft PR #3 (development candidate only).
- 사용자 공식 설치형 릴리즈 및 `main/latest.json`: v1.8.0 stable (별도 검증/서명 후 변경).
- R05 ZIP은 C# 소스 및 현재 계약 테스트의 원본을 포함한다. 과거 R06의 역사 검증 자료는 신뢰할 수 있는 별도 원본 ZIP으로 보관하고 코드 검증 결과로 자동 승계하지 않는다.
- Windows .NET10/WPF·파일시스템·DPI·실제 설치/코드서명 시험은 별도 게이트이고, 소스 정적 PASS로 대체하지 않는다.
- Windows 증적 생성: `powershell -NoProfile -ExecutionPolicy Bypass -File .\validation\R05\RUN_WINDOWS_GATES.ps1`.
- 이 포인터는 과거 v1.7.9 DEV10의 기준본이나 이전 단계 SHA-256 증거를 덮어쓰지 않는다.
