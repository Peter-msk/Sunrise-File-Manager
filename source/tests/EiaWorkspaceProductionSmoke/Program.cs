using System;
using System.IO;
using System.Linq;
using System.Threading;
using Sunrise.FileManager.Features.EiaWorkspace;

if (!OperatingSystem.IsWindows())
{
    Console.WriteLine("WINDOWS_DYNAMIC_PENDING: native folder writer needs Windows NTFS/ReFS");
    return;
}

// Use only a newly created GUID test directory inside OS temp; never a user project.
string location = Path.Combine(Path.GetTempPath(), "Sunrise_EIASS_WRITE_TEST_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(location);
try
{
    string sentinel = Path.Combine(location, "user_existing.pdf");
    File.WriteAllText(sentinel, "must remain unchanged");
    var request = new EiaCaseWorkspaceRequest("DEV-1.8.1", "개별 사업", [
        new EiaServiceContract("CON-1", "전략환경영향평가")
    ], [new EiaAssessmentCase("CASE-1", "CON-1", "strategy", null)], [], [], []);
    var scenarios = Array.Empty<EiaScenarioSelection>();
    var audit = EiaWorkspaceFootprintAudit.BuildDraftAudit(request, scenarios);
    var plan = EiaWorkspaceCreationEngine.Prepare(location, request, scenarios);
    var obs = EiaWorkspaceCreationEngine.DiagnoseReadOnlyProposedPaths(
        location, audit.RootFolderName, audit.DirectoryEntries.Select(x => x.RelativePath).ToArray(), "기본안");
    var readiness = EiaWorkspaceExecutionReadinessPlanner.PrepareReadOnlyReview(
        obs, audit.FingerprintSha256, plan.FingerprintSha256, "기본안");
    var acknowledgement = EiaWorkspaceProjectReviewPlanner.AcknowledgeReadOnly(
        readiness, readiness.WorkspaceFolderName, DateTimeOffset.UtcNow);
    var fresh = EiaWorkspaceExecutionPreflight.RecheckReadOnly(readiness, acknowledgement);

    bool rejected = false;
    try { EiaWorkspaceProductionWriter.ExecuteReviewed(readiness, acknowledgement, fresh, "WRONG"); }
    catch (InvalidDataException) { rejected = true; }
    if (!rejected || Directory.Exists(plan.WorkspacePath))
        throw new InvalidDataException("Invalid final consent must not create any directory.");

    var first = EiaWorkspaceProductionWriter.ExecuteReviewed(readiness, acknowledgement,
        fresh, "CREATE " + readiness.WorkspaceFolderName);
    if (!first.Completed || first.Created != first.Outcomes.Count || first.Failed != 0 ||
        first.Outcomes.Any(o => o.Outcome != "CREATED"))
        throw new InvalidDataException("First execution must create exactly the missing directories.");
    if (!File.Exists(sentinel) || File.ReadAllText(sentinel) != "must remain unchanged")
        throw new InvalidDataException("Original file modified or removed.");

    // Rerun is allowed ONLY from a completely new plan/observation and new consent.
    var repeatPlan = EiaWorkspaceCreationEngine.Prepare(location, request, scenarios);
    var repeatObs = EiaWorkspaceCreationEngine.DiagnoseReadOnlyProposedPaths(
        location, audit.RootFolderName, audit.DirectoryEntries.Select(x => x.RelativePath).ToArray(), "기본안");
    var repeatReview = EiaWorkspaceExecutionReadinessPlanner.PrepareReadOnlyReview(
        repeatObs, audit.FingerprintSha256, repeatPlan.FingerprintSha256, "기본안");
    var repeatAck = EiaWorkspaceProjectReviewPlanner.AcknowledgeReadOnly(
        repeatReview, repeatReview.WorkspaceFolderName, DateTimeOffset.UtcNow);
    var repeatPreflight = EiaWorkspaceExecutionPreflight.RecheckReadOnly(repeatReview, repeatAck);
    var repeat = EiaWorkspaceProductionWriter.ExecuteReviewed(repeatReview, repeatAck,
        repeatPreflight, "CREATE " + repeatReview.WorkspaceFolderName);
    if (!repeat.Completed || repeat.Created != 0 || repeat.AlreadyExists != repeat.Outcomes.Count)
        throw new InvalidDataException("Repeated execution must not create or overwrite directories.");
    Console.WriteLine("EIA_WORKSPACE_PRODUCTION_LOCAL_WINDOWS_SMOKE_PASS");
}
finally
{
    // Test creates its own isolated disposable location; never clean user folders.
    Directory.Delete(location, recursive: true);
}
