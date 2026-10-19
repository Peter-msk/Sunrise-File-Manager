# Sunrise File Manager 1.0.0 R17 — Google Drive 기준본 비교·통합 감사

- 비교 수행일: 2026-10-20
- Google Drive: `4. Sunrise File Manager` (폴더 ID `1Up6v2Ly5eMozMXqAMcB2jU1SmMRIhLe8`)
- Drive ZIP: `Sunrise_File_Manager_COMPANY_FULL_SOURCE_R12_AUDITED.zip` (원본 426,479 bytes)
- 작업 기준: `Sunrise_File_Manager_1.0.0_R16.zip` (원본 1,009,542 bytes)
- R17 릴리즈 자격: 소스/정적 검증 완료, Windows WPF 빌드·그룹서명·설치·업데이트 **미검증**

## 1. 바이트 단위 비교

| 구분 | 결과 |
|---|---:|
| Drive R12 ZIP 파일 수 | 177 |
| 로컬 R16 ZIP 파일 수 | 205 |
| 경로·내용이 완전히 같은 파일 | 148 |
| 경로는 같지만 내용이 다른 파일 | 29 |
| Drive에만 있는 파일 | 0 |
| R16에만 있는 파일 | 28 |

경로 비교와 ZIP 내부 원본 바이트 비교를 모두 수행함. 상세 경로·SHA256은 `docs/R17_DRIVE_COMPARE.json` 참조.

## 2. 충돌 해결 기준

1. R16의 **공식 설치형 Setup EXE 빌드**, R14 Sunrise Group Authenticode 서명, AppMutex, 라이선스 고지, 회사 문서 테스트 생략 정책을 우선 유지함.
2. Drive R12에서 확인된 **긴 breadcrumb 경로의 가로 휠 스크롤**, 경로 변경 후 끝부분 표시, **좁은 탐색기에서 표시 옵션 축약 및 검색 열 폭 조절**은 R17에 선별 복원함.
3. R16에서 추가한 보기·필터 메뉴 및 작은 화면 명령 버튼 축약을 유지하여 표시 옵션이 감춰져도 메뉴 접근이 가능하도록 함.
4. R12의 `BUILD_ON_WINDOWS.cmd` 장문 빌드 로직은 R16의 `BUILD_DEV_FULL_SOURCE.cmd`에 별도 보존되어 있으므로, 기본 진입점에는 복원하지 않음. R16 기본 경로는 반드시 설치형이어야 함.
5. R12의 구형 OneFile/OneDir 사용자 배포 경로를 공식 배포로 되돌리지 않음.
6. 원본 Drive R12 ZIP은 삭제·덮어쓰기하지 않음. R17 새 산출물로 별도 등록함.

## 3. R17 변경 파일 (기능 변경 최소화)

- `source/Sunrise.FileManager/Controls/ExplorerPane.xaml`: `BreadcrumbScroll`, 휠 핸들러, `DisplayOptionsPanel` 명칭 복구
- `source/Sunrise.FileManager/Controls/ExplorerPane.xaml.cs`: 브레드크럼 수평 휠 처리 복구
- `source/Sunrise.FileManager/Features/Explorer/ExplorerPane.NavigationAndWatcher.cs`: 이동 직후 경로 끝으로 스크롤
- `source/Sunrise.FileManager/Features/Explorer/ExplorerPane.ViewAndFiltering.cs`: 분할창 폭에 따른 표시 옵션·주소 영역 축약, 검색열 폭 165/230 분기 복구
- `tests/test_r17_drive_merge_contract.py`: 복구 UI·기존 설치형 빌드 계약 검사 추가
- `README.md`, `RELEASE_STATUS.json`, `FULL_SOURCE_SHA256_MANIFEST.json` 및 문서: 기준본 식별·인수인계 자료 갱신

## 4. 보존 및 위험

- Windows 실제 WPF 렌더링과 100/125/150% DPI, breadcrumb 휠과 파일목록 일반 스크롤 동시 동작은 회사 또는 별도 허용 Windows 환경의 실기 검증 필요.
- 새로 연결한 `Breadcrumb_PreviewMouseWheel`은 경로 ScrollViewer에서만 처리하므로 폴더 트리·파일 목록 휠 이벤트는 변경하지 않음.
- 현재 온라인 업데이트 서버·URL과 설치파일 서명 인증서의 Windows 실기 테스트는 미확정/미실행. 임의 배포·서명 성공 표시는 금지.
- R12 대비 29개 공통파일 차이를 일괄 덮어쓰지 않았음. 의미 기반 검토 후 필요한 4개 소스 파일의 UI 동작만 복원함.

## 5. 출력물

- `Sunrise_File_Manager_1.0.0_R17.zip`: **소스 전체 + 검증자료 + 문서** (Windows Setup EXE 아님)
- `Sunrise_File_Manager_1.0.0_R17_작업인수인계서.md`: 릴리즈 인수인계 및 재개 방법
- `Sunrise_File_Manager_1.0.0_R17_개발계획서.md`: 우선순위별 후속 실행계획
