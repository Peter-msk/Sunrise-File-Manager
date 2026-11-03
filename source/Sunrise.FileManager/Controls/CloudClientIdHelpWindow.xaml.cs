using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using Sunrise.FileManager.Services.CloudDrive;

namespace Sunrise.FileManager.Controls;

/// <summary>Static, provider-specific registration instructions; never handles OAuth tokens.</summary>
public partial class CloudClientIdHelpWindow : Window
{
    private readonly Uri _officialUri;

    public CloudClientIdHelpWindow(CloudProvider provider)
    {
        InitializeComponent();
        if (provider == CloudProvider.GoogleDrive)
        {
            ProviderHeading.Text = "Google Drive · 앱 Client ID 발급";
            StepsList.ItemsSource = new[]
            {
                "1. Google Cloud Console에서 프로젝트를 생성하거나 기존 프로젝트를 선택합니다.",
                "2. API 및 서비스 → 라이브러리에서 Google Drive API를 사용 설정합니다.",
                "3. Google Auth platform의 Branding · Audience에서 OAuth 동의 화면을 설정합니다. 외부 테스트 모드라면 테스트 사용자를 추가합니다.",
                "4. Google Auth platform → Clients → Create client에서 애플리케이션 유형을 'Desktop app(데스크톱 앱)'으로 선택합니다.",
                "5. 생성된 OAuth 2.0 Client ID를 복사하여 Sunrise의 앱 Client ID 칸에 붙여넣습니다.",
                "6. Sunrise의 '공식 로그인으로 연결'을 누르고 브라우저에서 요청 권한을 확인한 뒤 동의합니다."
            };
            SecurityNote.Text = "현재 프로그램은 Google Drive 전체 접근 범위(https://www.googleapis.com/auth/drive)를 요청합니다. 제한된 범위이므로 외부 사용자 배포 시 Google의 앱 검증·추가 보안 요구사항이 있을 수 있습니다. Client Secret 또는 JSON 자격증명 파일 전체를 Sunrise에 붙여넣지 마세요.";
            _officialUri = new Uri("https://developers.google.com/workspace/guides/create-credentials");
        }
        else
        {
            ProviderHeading.Text = "OneDrive · 앱 Client ID 발급";
            StepsList.ItemsSource = new[]
            {
                "1. Microsoft Entra 관리 센터에서 ID → 애플리케이션 → 앱 등록 → 새 등록을 선택합니다.",
                "2. 앱 이름과 사용할 계정 유형(개인 Microsoft 계정/조직 계정)을 선택해 등록합니다.",
                "3. 인증 → 플랫폼 추가 → '모바일 및 데스크톱 애플리케이션'을 선택하고 시스템 브라우저용 http://localhost 리디렉션 URI를 등록합니다.",
                "4. 필요하면 인증 → 고급 설정에서 퍼블릭 클라이언트 흐름을 허용합니다. Client Secret은 만들거나 입력할 필요가 없습니다.",
                "5. API 권한에서 Microsoft Graph의 위임 권한 Files.ReadWrite, User.Read를 확인합니다. 조직 정책에 따라 관리자 동의가 필요할 수 있습니다.",
                "6. 개요 화면의 Application (client) ID를 Sunrise에 입력합니다. 테넌트 제한이 필요한 조직 계정만 테넌트 칸을 사용합니다.",
                "7. Sunrise에서 '공식 로그인으로 연결'을 눌러 로그인 및 권한 동의를 완료합니다."
            };
            SecurityNote.Text = "이 프로그램은 offline_access, User.Read, Files.ReadWrite 위임 권한과 로컬 시스템 브라우저 OAuth(PKCE)를 사용합니다. 테넌트 칸을 비워두면 common 경로를 사용합니다. 계정 비밀번호·Client Secret은 입력하지 마세요.";
            _officialUri = new Uri("https://learn.microsoft.com/en-us/graph/auth-register-app-v2");
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenOfficial_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_officialUri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or System.IO.IOException)
        {
            MessageBox.Show(this, "공식 페이지를 열지 못했습니다. 브라우저에서 아래 주소를 직접 입력하세요.\n" + _officialUri.AbsoluteUri,
                "공식 발급 페이지", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
