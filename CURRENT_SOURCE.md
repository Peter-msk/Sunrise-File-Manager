# Sunrise File Manager — current source pointer (v1.8.2 R00)

- **현행 개발 후보**: v1.8.2 R00, v1.8.1 R05 VERIFY1 전체 소스에서 기능 변경 없이 버전 계약을 승격한 후보. 원본 검증·역사 자료와 문서 이름은 보존.
- **공식 사용자 설치형 릴리즈 및 main/latest.json**: v1.8.0 stable. 서명된 v1.8.2 설치파일과 실제 Windows 검증·공개 파일 해시가 없어 배포 메타데이터를 변경하지 않음.
- **기준 리포트**: docs/V182_VERSION_MIGRATION_REPORT.md, docs/HANDOVER_EIASS_v1.8.2.md, docs/V182_RELEASE_BLOCKERS.md.
- **검증 수행 계약**: Python 현재 버전 일치·기능 영향범위 스모크는 소스 테스트이며, 과거 R05 검증 수치를 새 v1.8.2 Windows 검증으로 승계하지 않는다.
- **Windows 테스트**: powershell -NoProfile -ExecutionPolicy Bypass -File .\validation\R05\RUN_WINDOWS_GATES.ps1 (R05는 검사 스위트 이름으로 유지됨). Windows .NET10/WPF/실파일/고DPI/서명 등은 미실행이며 필수 Gate.
- **업데이트 채널**: 기존 v1.8.0 latest.json/notice.txt 불변. v1.8.2 설치파일 Authenticode 및 SHA256 완료 전 파일·릴리즈 링크나 download_url을 생성·게시하지 않음.
- **과거 이력 보존**: docs/HANDOVER_EIASS_v1.8.1.md 및 docs/R05_INDEPENDENT_VALIDATION_2026-11-15.md는 역사 증거이며, R05 제품 기능 변경 결과는 현재 브랜치에 포함.


## v1.8.2 R01 직접 폴더 생성 화면
- 현재 작업 기준: v1.8.2 R01 (설치형 UI 소스 수정; 공식 배포 아직 v1.8.0).
- 이중 검토창 생략, 한 화면 기본안 계획 → 생성, 최종 확인 1회; Windows 현장 실행 검증 필수.
