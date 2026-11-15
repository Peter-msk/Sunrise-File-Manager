# Sunrise File Manager — current source pointer (v1.8.2 R02)

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


## v1.8.2 R02 실행 제한·테마·메뉴 사용성 보완
- **현행 개발 기준본: v1.8.2 R02** (R01 527항목 기반), 공식 배포는 여전히 v1.8.0.
- EIASS 생성: 별도 폴더 미리보기 선행 클릭 제거. 내부 자동 검증 및 최종 Yes/No·로컬 파일 보호는 유지.
- 로컬 분할창 안전 이동: 폴더를 포함한 선택을 검증형 TransferService로 연결, 충돌 건너뜀/실패 결과 확인. 원격 클라우드 폴더 MOVE는 차단 유지.
- 두 테마에 공통: 팝업 글자·스크롤바·비활성 대비·입력 포커스/탭·AI 주 행동 간소화. 소스 대비 검사 및 69 Python PASS / 11 subtests.
- **상세 감사**: `docs/V182_R02_FEATURE_THEME_AUDIT.md`. Windows .NET10 빌드/실기·시각·서명은 미완료, `validation/R02/RUN_R02_WINDOWS.ps1` 사용.
