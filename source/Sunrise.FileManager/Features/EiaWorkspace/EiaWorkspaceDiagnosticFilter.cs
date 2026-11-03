using System;

namespace Sunrise.FileManager.Features.EiaWorkspace;

/// <summary>
/// DEV16: display-only filtering over an already completed, read-only path
/// observation. The view never changes the scan, its BLOCKED count, or any
/// approval/write policy. No filesystem access occurs here.
/// </summary>
public static class EiaWorkspaceDiagnosticFilter
{
    public const string All = "ALL";
    public const string Blocked = "BLOCKED";
    public const string Missing = "MISSING";
    public const string Existing = "EXISTS_DIRECTORY";

    public static bool Matches(EiaFolderPathObservation? row, string? statusFilter,
        string? keyword)
    {
        if (row is null) return false;
        var status = statusFilter ?? All;
        if (status != All && status != Blocked && status != Missing && status != Existing)
            return false; // Unknown UI state fails closed as an empty view.
        if (status != All && !string.Equals(row.Status, status, StringComparison.Ordinal))
            return false;
        var query = (keyword ?? "").Trim();
        if (query.Length == 0) return true;
        return row.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            row.AbsolutePath.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            row.ScopeLabel.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            row.Detail.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            row.StatusLabel.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
