using System.IO;
using System.Windows;
using System.Windows.Controls;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

public partial class ExplorerPane
{
    private void ShellNewTemplates_Opened(object sender, RoutedEventArgs e)
    {
        ShellNewTemplatesMenu.Items.Clear();
        if (!_actionsEnabled || ActiveFolder is null) return;
        try
        {
            var templates = ShellNewTemplateService.Discover();
            foreach (var template in templates)
            {
                var item = new MenuItem { Header = template.Title, Tag = template };
                item.Click += ShellNewTemplate_Click;
                ShellNewTemplatesMenu.Items.Add(item);
            }
            if (templates.Count == 0) ShellNewTemplatesMenu.Items.Add(new MenuItem { Header = "등록된 안전한 템플릿이 없습니다.", IsEnabled = false });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { ShellNewTemplatesMenu.Items.Add(new MenuItem { Header = "템플릿 목록을 읽지 못했습니다.", IsEnabled = false }); }
    }
    private void ShellNewTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (!_actionsEnabled || ActiveFolder is not string folder || sender is not MenuItem { Tag: ShellNewTemplateService.Template template } || BeforeFileChange?.Invoke() == false) return;
        try
        {
            string created = ShellNewTemplateService.Create(folder, template);
            _undoService.RecordCreated(created, "Windows 새 문서");
            UpdateUndoButtonState();
            _restoreSelectionPath = created;
            if (FileActionCompleted is null) RefreshCurrent(); else FileActionCompleted.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        { MessageBox.Show(Window.GetWindow(this), ex.Message, "Windows 문서 만들기", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
