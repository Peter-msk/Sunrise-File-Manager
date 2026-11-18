# Sunrise File Manager v1.8.2 R02 — 기능 제한·다크/라이트·사용성 통합 감사

> 2026-11-16 소스 기준 / Windows WPF **실기 검증 전** / 검증 상태: 소스 계약 PASS, 실제 프로그램 기능·시각 검증 PENDING.

## 1. 감사 범위와 판정 규칙
- 기준본: `Sunrise_File_Manager_v1.8.2_R01_DIRECT_CREATE_SOURCE.zip` (527항목). R01까지의 제품 소스와 사용자 데이터 보호 계약을 보존.
- WPF XAML 22개 모두 XML 파싱 확인. 소스에는 `IsEnabled="False"` 초기 선언 35개, `Visibility="Collapsed"` 선언 45개가 존재한다. **이는 비활성 기능 35개·숨겨진 기능 45개라는 의미가 아니다**: 대부분 선택 상태·작업 진행·검토 완료 여부에 따른 동적 컨트롤이다.
- 감사 분류: (A) 불필요한 수동 선행 과정, (B) 실행 경로가 있지만 UI에서 막는 항목, (C) 상태·권한에 따른 정상 비활성, (D) 데이터 손실/보안 위험 때문에 반드시 유지할 제한, (E) 플랫폼·외부 서비스의 실제 미구현 기능.
- 외부 오픈소스 참조: Files(작업 상태 및 취소), PowerToys PowerRename(실행 전 결과 확인), WPF UI·MahApps.Metro(상태 및 메뉴 밀도), CommunityToolkit.Mvvm(중복 작업 방지). 서드파티 소스 복사·NuGet 패키지 신규 추가 없음.

## 2. 기능 사용성/실행 제한 보완 (R02 적용)
| 영역 | 기존 불편 또는 제한 | R02 실제 코드 변경 | 보호 경계 |
|---|---|---|---|
| EIASS 신규 사업폴더 | 미리보기 버튼을 별도로 눌러야 직접 생성 가능 | 사업명·용역명·대상 위치 입력 시 `누락 폴더 생성` 가능; 클릭 시 최신 미리보기와 내부 검사 자동 실행 | 최종 Yes/No 1회, 충돌·재분석 지점 검사, NTFS/ReFS 핸들 기반 디렉터리 생성. 기존 파일 무변경, SMB/NAS 계속 차단 |
| 분할창 간 이동 | 로컬 폴더 선택 시 무조건 불허 | 로컬→로컬 폴더/파일 선택, 이동 창 및 `TransferService.RunAsync` 연동 | 검증 복사 후 원본 정리, 충돌 건너뜀, 상·하위 중첩 선택 사전 차단, 취소/부분 완료 별도 표시 |
| 클라우드 이동 | 일부 클라우드 파일 이동 불가 | 미지원 원인 및 '복사' 대안 명시 | 조건부 버전 보호 휴지통 계약 미구현 공급자에서 원본 삭제 금지; 클라우드 폴더 MOVE는 미지원 |
| AI 도우미 | 단계 숫자 나열·강조 버튼 과다 | 탐색 범위 / 정리 방법 / 대상 폴더 / 작업 미리보기 순서로 단순화. 유일한 강조 버튼은 검토한 작업 실행 | 승인되지 않은 계획·영구 삭제·DRM 우회는 활성화하지 않음 |
| 구형 검토창 | 별도의 읽기 전용 검토 창 내부 버튼 비활성 | R01에서 주창 단일 실행으로 경로 대체, R02에서 미리보기 선행 클릭까지 제거 | 구형 검토/승인 모델의 읽기 전용 보호 상수는 그대로 유지 |

## 3. UI/UX 및 테마 개선
- `App.xaml` ComboBox/메뉴 팝업의 비율 0.8→1.0으로 변경: 과도하게 축소된 목록 글자 정상화.
- 세로 스크롤바/가로 스크롤바 기본 두께 6→9 DIP, 축소된 클릭 대상 보완.
- 비활성 버튼/입력 등의 전역 불투명도를 0.45/0.5 수준에서 0.68 수준으로 조정. 상태는 유지하면서 글자 판독성 보강. 비활성 버튼·입력·선택창에도 도움말 툴팁 확인 가능.
- 입력칸 기본 테두리는 `ThemeInputBorder`, 키보드 포커스 시 `Accent`, 읽기 전용 입력 배경은 `ThemeSoft`로 명확하게 분리.
- `EiaWorkspacePlanningWindow`와 `FilePropertiesWindow` 탭에 각 창 소유 스타일 부여: 선택/호버/포커스/비활성 표현, `TextPrimary` 사용. 전역 탭 스타일로 모든 화면을 변경하지 않음.
- AI 명령 패널: 불필요한 순서 번호 및 기본 버튼의 반복 강조 해소, 검증된 작업 실행만 주 행동으로 강조.
- EIASS 직접 생성 명령은 주 행동으로 유지하고 '폴더 미리보기'는 **선택** 기능으로 전환.
- 기존 Sunrise 브랜드 색상과 각 화면 레이아웃 계약, 분할창·표 열 너비 정책 변경하지 않음.

### 3-1. 다크/라이트 배색의 수치 점검 (소스의 색상값 기준)
| 글자/면 조합 | 라이트 대비 | 다크 대비 |
|---|---:|---:|
| TextPrimary / ThemeSurface | 14.35:1 | 14.44:1 |
| TextMuted / ThemeSurface | 6.00:1 | 8.47:1 |
| ThemeMenuText / ThemeMenuBar | 10.57:1 | 12.32:1 |
| ThemeButtonOnAccent / Accent | 5.93:1 | 8.68:1 |
| ThemeNavigationRailText / ThemeNavigationRail | 6.52:1 | 5.26:1 |
| ThemeNoticeText / ThemeNotice | 7.39:1 | 9.35:1 |
| ThemeSplitText / ThemeSplitInactive | 9.06:1 | 10.74:1 |

> 위 값은 `ThemeManager.cs` RGB 정의를 WCAG 상대 휘도 공식으로 계산한 **정적 값**이다. 실제 WPF ControlTemplate, 투명도, DPI 및 오버레이 합성 결과의 시각 검증을 의미하지 않는다.

## 4. 해제하지 않은 제한: 안전·외부 시스템 실조건
1. 파일 미선택, 작업 중, 계획 미승인, 취소 불가능한 단계에서 버튼 비활성: 정상 상태 전이. 무조건 활성화하면 잘못된 실행 초래.
2. 원본 버전 태그/`IConditionalCloudRetirement` 없는 클라우드 파일 MOVE, 원격 폴더 MOVE: 버전 조건부 원본 정리 미보장. 복사 대안 제공.
3. AI 영구 삭제, 로그인 자격증명 우회, DRM/암호 보호 강제 해제: 기능 지원으로 위장하거나 안전장치 제거 금지.
4. NAS/SMB에서 EIASS 운영용 신규 사업폴더 쓰기: 현재 로컬 NTFS/ReFS 전용 네이티브 핸들 생성기의 지원 범위 밖. 사전 검증 없는 네트워크 쓰기 금지.
5. 원본 훼손 없는 실측 검증이 끝나지 않은 기능(일부 HWP 렌더, 고해상도/스캔 PDF 등)은 사용 가능으로 표시하지 않음. 기존 다른 경로가 있으면 안내.

## 5. 회귀검증·미검증
- `python -m pytest -q source/tests/test_v182_r02_feature_theme_usability.py source/tests/test_v182_version_consistency.py source/tests/test_v182_r01_direct_eiass_creation.py source/tests/test_v181_r05_full_app_audit.py source/tests/test_v181_r04_recursive_ui_cache.py source/tests/test_v181_r03_transfer_safety.py source/tests/test_v181_r02_eiass_rename_stability.py`: **69 PASS, 11 subtests PASS**.
- 제품의 22개 XAML 파싱 및 양 테마 10개 텍스트/배경 조합의 대비 기준 4.5 이상 확인.
- Windows 전용 고립 시험 소스: `source/tests/CrossPaneFolderMoveSmoke` (GUID 임시폴더: 로컬 폴더 이동, 바이트 확인, 충돌 건너뜀, 취소, 상위+하위 동시 선택). 실행 방법: `validation/R02/RUN_R02_WINDOWS.ps1`.
- **필수 잔여:** 실제 Windows .NET10 WPF 컴파일·실행, 라이트/다크 100/125/150/175% DPI 화면 캡처, 로컬 폴더 이동·중단 중 재생성·원본 교체 경쟁/SMB 실기, EIASS 생성·취소 실기, 설치·코드서명·업데이트 회귀, 실클라우드 계정 기능 검증.
- **출시 판정:** 소스 검증 통과, Windows 기능·서명·실화면 검증 미완료 → PR Draft / 정식 릴리즈 보류. `main/latest.json`의 v1.8.0 및 공지 유지.
