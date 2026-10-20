# Sunrise File Manager R19 R11 — 단일 공식 빌드 및 서명자 재사용

## 문제와 원인

- Windows PC에서 .NET SDK 10.0.301, Inno Setup, SignTool은 확인되었다. 도구 부재가 원인이 아니다.
- `CN=Sunrise Group` 인증서가 12개 확인되었는데 File Manager 전용 `group_signer.sha1`은 없었다. R09/R10의 `UNPINNED_GROUP_CERT_MANUAL_REVIEW`는 임의 신원 전환 방지용 중단이다.
- 이전 설치파일의 서명자 지문이나 복구용 공개 `.cer` 없이 어느 인증서가 기존 공식 File Manager 게시자인지 추정해서는 안 된다.
- 기존 R10에는 별도 승인 도구가 있었으나 공식 빌드에서 복구 동선이 끊어져 있었다.

## 이번 변경

1. `BUILD_ON_WINDOWS_COMPANY.cmd`에서 도구 사전검사와 읽기 전용 인증서 상태 검사를 자동 수행한다.
2. PIN이 이미 있으면 승인 안내 없이 기존 SignTool/Inno 공식 서명 경로로 진입한다.
3. 이전 실패의 공개 인증서가 남은 경우 R09 복구 로직에 맡긴다.
4. 정확히 `UNPINNED_GROUP_CERT_MANUAL_REVIEW`일 때만 **명시적 사용자 승인** 경로를 안내하고, 사용자가 선택하면 기존 `APPROVE_EXISTING_GROUP_SIGNER.cmd`를 같은 창에서 호출한다.
5. 승인 성공 뒤 인증서 검사를 다시 수행하고 같은 빌드에서 설치 단계를 진행한다. 다른 오류에는 승인 프롬프트를 띄우지 않는다.
6. `--noninteractive`에서는 미승인 신원을 임의 선택하지 않고 실패한다.

## Sunrise Capture 대조

Capture v4.2.0 인수인계서에는 `CMD → Python build_pipeline.py → certreq/certutil → SignTool → Inno Setup`, 그리고 SHA-1 라벨과 값 분리 및 40자리 검증, Windows EXE 실제 서명 PASS가 기록되어 있다. **Capture의 전체 Python 소스 파일은 이번 작업에서 확보하지 못했으므로 문자 그대로의 코드 이식을 주장하지 않는다.** File Manager는 CMD 기반 `certutil` 조회·SignTool `/sha1` 서명과 R09 인증서 공개파일 SHA-1 복구, R10 승인 도구를 그대로 재사용한다.

## 사용법

새 ZIP을 새 폴더에 압축 해제하고 `BUILD_ON_WINDOWS_COMPANY.cmd`를 더블클릭한다. 이미 PIN이 있는 PC는 자동 빌드한다. PIN이 없는 다중 인증서 환경에서만 승인 여부를 묻는다. 승인 지문은 회사에서 허가한 코드서명 인증서와 확인해야 한다. 인증서 목록에서 가장 최근 것을 자동 선택하지 않는다.

비대화형: `BUILD_ON_WINDOWS_COMPANY.cmd --noninteractive`
개발 미서명 EXE: `BUILD_DEV_EXE.cmd`

## 보안·호환성

- 개인키/PFX는 소스, 로그, 설치파일에 넣지 않는다. 인증서 제거·자동 회전·AV 정책 예외 및 Trusted Root 조작 없음.
- 기존 빌드 내부의 .NET 게시, SignTool EXE/DLL 서명, Inno Setup 설치파일 서명, UI·메모·Ollama 코드는 수정하지 않는다.
- 회사 빌드는 LogicTests와 업무문서 내용 검사를 수행하지 않는다.
- 자체서명 인증서는 공인 조직 신원이나 SmartScreen/V3 허용을 보장하지 않는다.

## 검증 경계

- Python 정적 회귀와 CMD 문구·흐름 계약검사는 수행할 수 있다.
- Windows CMD 분기 실행, 실제 개인키/SignTool 사용 가능성, 설치형 EXE 생성, 설치·업그레이드·제거와 V3는 대상 PC 실기 검증이 필요하다.
- 성공 여부는 `R14_cert_provision.log`, `R14_signing_preflight.log`, `R15_INSTALLER_BUILD_RESULT.txt`의 단계와 최종 signed Setup 존재·서명 상태로 판정한다.
