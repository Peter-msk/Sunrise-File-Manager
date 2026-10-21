using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Sunrise.FileManager.Services;

/// <summary>Local installer handoff only. Setup owns file replacement and recovery.</summary>
internal sealed class InstallerUpdateHandoff
{
    private readonly string _setup;
    private readonly string _helper;
    private readonly string _job;
    private static string ResultPath => Path.Combine(SunriseReleaseService.UpdatesRoot, "last-update-result.txt");

    private InstallerUpdateHandoff(string setup, string helper, string job) => (_setup, _helper, _job) = (setup, helper, job);

    internal static InstallerUpdateHandoff Prepare(string setup, SunriseReleaseService.Release release)
    {
        string full = Path.GetFullPath(setup);
        string job = Path.GetDirectoryName(full)!;
        string jobName = Path.GetFileName(job);
        if (!release.CanDownload || !File.Exists(full) || Path.GetFileName(full) != release.FileName ||
            !string.Equals(Path.GetDirectoryName(job), Path.GetFullPath(SunriseReleaseService.UpdatesRoot), StringComparison.OrdinalIgnoreCase) ||
            !jobName.StartsWith("job-", StringComparison.Ordinal) || !Guid.TryParseExact(jobName[4..], "N", out _) ||
            (File.GetAttributes(job) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Invalid installer handoff target");
        string helper = Path.Combine(job, "install-after-exit.cmd");
        File.WriteAllText(helper, HelperTemplate.Replace("\n", "\r\n"), Encoding.ASCII);
        return new InstallerUpdateHandoff(full, helper, job);
    }

    // Called ONLY by App.OnExit after normal Closing handlers accepted shutdown.
    internal void Launch()
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            Arguments = "/D /V:OFF /C \"\"%SFM_UPDATE_HELPER%\"\"",
            UseShellExecute = false,
            // A hidden console provides valid console input for timeout.exe.
            // CreateNoWindow/redirected input would make timeout fail immediately.
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = SunriseReleaseService.UpdatesRoot
        };
        start.Environment["SFM_UPDATE_HELPER"] = _helper;
        start.Environment["SFM_UPDATE_SETUP"] = _setup;
        start.Environment["SFM_UPDATE_JOB"] = _job;
        start.Environment["SFM_UPDATE_TASKS"] = Path.Combine(_job, "parent.csv");
        start.Environment["SFM_UPDATE_RESULT"] = ResultPath;
        start.Environment["SFM_UPDATE_SYSTEM"] = Environment.SystemDirectory;
        start.Environment["SFM_UPDATE_PID"] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        using var process = Process.Start(start) ?? throw new IOException("설치 대기 작업을 시작할 수 없습니다.");
    }

    internal void Cancel()
    {
        // Only this job's generated artifacts; never touch installed files or user data.
        foreach (string path in new[] { _helper, _setup })
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Trace.TraceWarning("Cancelled update staging retained: {0}", ex.GetType().Name); }
        }
        try { if (Directory.Exists(_job)) Directory.Delete(_job); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Trace.TraceWarning("Update job cleanup: {0}", ex.GetType().Name); }
    }

    internal static string? ConsumeFailure()
    {
        try
        {
            if (!File.Exists(ResultPath) || new FileInfo(ResultPath).Length > 256) return null;
            string status = File.ReadAllText(ResultPath).Trim();
            if (status is "WAITING_FOR_EXIT" or "INSTALLING") return null;
            File.Delete(ResultPath);
            return status switch
            {
                "SUCCESS" => null,
                "INSTALLER_EXIT_2" => "이전 업데이트 설치가 취소되었습니다. 업데이트를 다시 선택할 수 있습니다.",
                "EXIT_TIMEOUT" => "이전 프로그램의 종료를 확인하지 못해 설치를 시작하지 않았습니다.",
                _ => "이전 업데이트를 완료하지 못했습니다. 업데이트를 다시 선택하거나 공식 설치파일로 설치하십시오."
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Trace.TraceWarning("Update result unavailable: {0}", ex.GetType().Name); return null; }
    }

    // Fixed, local template. Paths travel via environment, never by code interpolation.
    // No network, elevation, process killing, file replacement or custom rollback.
    internal const string HelperTemplate = """
@echo off
setlocal EnableExtensions DisableDelayedExpansion
> "%SFM_UPDATE_RESULT%" echo WAITING_FOR_EXIT
set /a SFM_UPDATE_ATTEMPTS=0
:wait_parent
"%SFM_UPDATE_SYSTEM%\tasklist.exe" /FI "PID eq %SFM_UPDATE_PID%" /FO CSV /NH > "%SFM_UPDATE_TASKS%" 2>nul
if errorlevel 1 goto task_query_failed
set "SFM_UPDATE_ALIVE="
for /f "usebackq tokens=2 delims=," %%P in ("%SFM_UPDATE_TASKS%") do if "%%~P"=="%SFM_UPDATE_PID%" set "SFM_UPDATE_ALIVE=1"
if not defined SFM_UPDATE_ALIVE goto install
set /a SFM_UPDATE_ATTEMPTS+=1
if %SFM_UPDATE_ATTEMPTS% GEQ 90 goto exit_timeout
"%SFM_UPDATE_SYSTEM%\timeout.exe" /T 1 /NOBREAK >nul 2>&1
if errorlevel 1 goto wait_unavailable
goto wait_parent
:install
> "%SFM_UPDATE_RESULT%" echo INSTALLING
start "" /wait "%SFM_UPDATE_SETUP%" /SP- /NORESTART /SUNRISEUPDATE=1
set "SFM_UPDATE_EXIT=%ERRORLEVEL%"
if not "%SFM_UPDATE_EXIT%"=="0" goto installer_failed
> "%SFM_UPDATE_RESULT%" echo SUCCESS
goto cleanup
:installer_failed
> "%SFM_UPDATE_RESULT%" echo INSTALLER_EXIT_%SFM_UPDATE_EXIT%
goto cleanup
:task_query_failed
> "%SFM_UPDATE_RESULT%" echo TASK_QUERY_FAILED
goto cleanup
:wait_unavailable
> "%SFM_UPDATE_RESULT%" echo WAIT_UNAVAILABLE
goto cleanup
:exit_timeout
> "%SFM_UPDATE_RESULT%" echo EXIT_TIMEOUT
:cleanup
del /q "%SFM_UPDATE_TASKS%" >nul 2>&1
del /q "%SFM_UPDATE_SETUP%" >nul 2>&1
del /q "%~f0" & rmdir "%SFM_UPDATE_JOB%" >nul 2>&1 & exit /b
""";
}
