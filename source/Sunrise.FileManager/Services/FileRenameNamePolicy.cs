using System.IO;

namespace Sunrise.FileManager.Services;

/// <summary>Single-item Explorer rename: edit exactly what the pane displays.</summary>
public static class FileRenameNamePolicy
{
    public static string EditorText(string currentName, bool directory, bool showExtensions)
    {
        if (directory || showExtensions) return currentName;
        string stem = Path.GetFileNameWithoutExtension(currentName);
        return stem.Length > 0 ? stem : currentName;
    }

    public static int InitialSelectionLength(string currentName, bool directory, bool showExtensions)
    {
        // Explorer selects the base name even when the extension is visible.
        return EditorText(currentName, directory, false).Length;
    }

    public static string ResolveFullName(string currentName, bool directory, bool showExtensions, string editedText)
    {
        // The user's hidden-extension choice is a snapshot from the start of editing.
        // Never infer an intentional extension change from an editor that hid it.
        if (string.IsNullOrWhiteSpace(editedText))
            throw new ArgumentException("파일 또는 폴더 이름을 입력하세요.");
        if (directory || showExtensions) return FileActionsService.ValidateName(editedText);
        string stem = Path.GetFileNameWithoutExtension(currentName);
        string suffix = stem.Length > 0 ? Path.GetExtension(currentName) : string.Empty;
        return FileActionsService.ValidateName(editedText + suffix);
    }
}
