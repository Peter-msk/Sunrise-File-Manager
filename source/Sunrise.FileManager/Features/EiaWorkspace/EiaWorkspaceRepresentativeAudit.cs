using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Sunrise.FileManager.Features.EiaWorkspace;

/// <summary>
/// v1.7.7 DEV1: five synthetic, read-only acceptance fixtures for EIA document workspace
/// templates. These examples are NOT law or company policy, not customer data, and never
/// grant approval or a license to create actual folders. No filesystem APIs are called.
/// </summary>
public sealed record EiaRepresentativeFixture(
    string FixtureId, string Description, EiaCaseWorkspaceRequest Request,
    IReadOnlyList<EiaScenarioSelection> Scenarios,
    IReadOnlyList<string> MustContainPaths,
    IReadOnlyList<string> MustNotContainPaths,
    IReadOnlyList<EiaExpectedScenario> ExpectedScenarios);

public sealed record EiaExpectedScenario(string ScenarioId, string ReferenceCode, string OutcomeKind);

public sealed record EiaRepresentativeFinding(
    string FixtureId, string Description, int DirectoryCount, int ScenarioCount,
    string PreviewFingerprintSha256, IReadOnlyList<string> Failures,
    IReadOnlyList<string> PendingHumanReviews,
    IReadOnlyList<string> UnverifiedRuntimeGates,
    bool UserApprovalRequired, bool SupportsFilesystemWrites)
{
    public bool SourceContractPassed => Failures.Count == 0;
}

public static class EiaWorkspaceRepresentativeAudit
{
    public const string FixtureCatalogVersion = "v1.7.7-DEV1-DRAFT";
    private const string EiaRoot = "40_평가업무_법정절차";

    // Synthetic user's checkmarks on individual scenario fixtures are NOT official company
    // or legal authorization; independent statutory review remains explicitly PENDING.
    private static EiaScenarioSelection Scenario(string id, string reference,
        string? caseCode = null, string? contractCode = null, int round = 0)
        => new(id, reference, caseCode, contractCode, round,
            UserConfirmed: true, HumanLegalReviewConfirmed: true);

    public static IReadOnlyList<EiaRepresentativeFixture> BuildDraftFixtures()
    {
        const string s = EiaRoot;
        var fixtures = new EiaRepresentativeFixture[]
        {
            new("STRATEGY", "전략평가: 초안 의견수렴·공동수급·주민의견 추가", new(
                "REP-STR", "전략평가_가상사업",
                [new("CTR-STR", "전략환경영향평가 용역")],
                [new("A-STR", "CTR-STR", "strategy", ConfirmedOptions: ["strategy_draft"])],
                CoreMode: "standard"),
                [Scenario("S03", "JV-01", contractCode:"CTR-STR"),
                 Scenario("S13", "OPN-01", caseCode:"A-STR")],
                [
                    $"{s}/01_전략환경영향평가/A-STR/03_초안_의견수렴/공고_공람",
                    $"{s}/01_전략환경영향평가/A-STR/90_발생사건/20_상황별_검토/S13_OPN-01/02_설명회_공청회_자료",
                    "10_계약_용역관리/07_계약별/CTR-STR/90_업무상황/S03_JV-01/01_공동도급_분담협약"
                ],
                [$"{s}/02_환경영향평가", $"{s}/03_소규모환경영향평가",
                 $"{s}/04_변경_재협의", $"{s}/05_사후환경영향조사"],
                [new("S03", "JV-01", "PROPOSED_DIRECTORIES_ONLY"),
                 new("S13", "OPN-01", "PROPOSED_DIRECTORIES_ONLY")]),

            new("ENVIRONMENTAL", "본평가: 선행 전략평가·설계 수령 1/2차·보완 1/2차", new(
                "REP-ENV", "환경평가_복합가상사업",
                [new("CTR-STR", "선행 전략평가 계약"), new("CTR-EIA", "본평가 계약")],
                [new("A-STR", "CTR-STR", "strategy"),
                 new("A-EIA", "CTR-EIA", "environmental", "A-STR", ["environmental_draft"])],
                [new("A-EIA", "DRAW", 1), new("A-EIA", "DRAW", 2)],
                [new("A-EIA", "AGENCY", 1), new("A-EIA", "AGENCY", 2)],
                [new("A-EIA", "D-CHANGE", "design_change")]),
                [Scenario("S07", "LINK-01", caseCode:"A-EIA"),
                 Scenario("S09", "DRAW", caseCode:"A-EIA", round:2),
                 Scenario("S15", "AGENCY", caseCode:"A-EIA", round:2),
                 Scenario("S11", "SCOPE-02", caseCode:"A-EIA")],
                [
                    $"{s}/02_환경영향평가/A-EIA/03_초안_의견수렴/공고_공람",
                    "20_사업_설계_기초자료/05_설계자료_수령차수/A-EIA/DRAW_수령_01/01_수령원본",
                    "20_사업_설계_기초자료/05_설계자료_수령차수/A-EIA/DRAW_수령_02/01_수령원본",
                    $"{s}/02_환경영향평가/A-EIA/80_보완_차수/AGENCY/보완_01/01_요청서_접수원본",
                    $"{s}/02_환경영향평가/A-EIA/80_보완_차수/AGENCY/보완_02/04_재제출_확정본",
                    $"{s}/02_환경영향평가/A-EIA/90_발생사건/20_상황별_검토/S11_SCOPE-02/03_추가조사_계획"
                ],
                [$"{s}/03_소규모환경영향평가", $"{s}/04_변경_재협의",
                 $"{s}/05_사후환경영향조사"],
                [new("S07", "LINK-01", "PROPOSED_DIRECTORIES_ONLY"),
                 new("S09", "DRAW", "REFERENCES_DEV2_INTAKE"),
                 new("S15", "AGENCY", "REFERENCES_DEV2_CORRECTION"),
                 new("S11", "SCOPE-02", "PROPOSED_DIRECTORIES_ONLY")]),

            new("SMALL", "소규모평가: 별도 평가 절차와 계약 변경·보완", new(
                "REP-SML", "소규모평가_가상사업",
                [new("CTR-SML", "소규모환경영향평가 용역")],
                [new("A-SML", "CTR-SML", "small")],
                CorrectionRounds: [new("A-SML", "AGENCY", 1)]),
                [Scenario("S05", "AMEND-01", contractCode:"CTR-SML"),
                 Scenario("S15", "AGENCY", caseCode:"A-SML", round:1)],
                [
                    $"{s}/03_소규모환경영향평가/A-SML/02_소규모평가서_작성_검토",
                    $"{s}/03_소규모환경영향평가/A-SML/80_보완_차수/AGENCY/보완_01/03_수정_검토본",
                    "10_계약_용역관리/07_계약별/CTR-SML/90_업무상황/S05_AMEND-01/02_변경계약_전후대비"
                ],
                [$"{s}/01_전략환경영향평가", $"{s}/02_환경영향평가",
                 $"{s}/04_변경_재협의", $"{s}/05_사후환경영향조사"],
                [new("S05", "AMEND-01", "PROPOSED_DIRECTORIES_ONLY"),
                 new("S15", "AGENCY", "REFERENCES_DEV2_CORRECTION")]),

            new("FOLLOWUP", "사후조사: 원평가 참조·연도별 차수·공사중단", new(
                "REP-FUP", "사후조사_가상사업",
                [new("CTR-EIA", "원평가 계약"), new("CTR-FUP", "사후조사 계약")],
                [new("A-EIA", "CTR-EIA", "environmental"),
                 new("A-FUP", "CTR-FUP", "followup", "A-EIA", ["followup_year"])],
                Events: [new("A-FUP", "PAUSE-01", "construction_pause")]),
                [Scenario("S32", "2027", caseCode:"A-FUP", round:1),
                 Scenario("S32", "2028", caseCode:"A-FUP", round:2),
                 Scenario("S20", "SITE-PAUSE", caseCode:"A-FUP")],
                [
                    $"{s}/05_사후환경영향조사/A-FUP/01_원평가_협의조건_인수",
                    $"{s}/05_사후환경영향조사/A-FUP/90_발생사건/20_상황별_검토/S32_2027_조사_01/02_연도별_차수별_조사",
                    $"{s}/05_사후환경영향조사/A-FUP/90_발생사건/20_상황별_검토/S32_2028_조사_02/03_연차보고_결과원본",
                    $"{s}/05_사후환경영향조사/A-FUP/90_발생사건/20_상황별_검토/S20_SITE-PAUSE/01_공사중지_통보원본"
                ],
                [$"{s}/01_전략환경영향평가", $"{s}/03_소규모환경영향평가",
                 $"{s}/04_변경_재협의"],
                [new("S32", "2027", "PROPOSED_DIRECTORIES_ONLY"),
                 new("S32", "2028", "PROPOSED_DIRECTORIES_ONLY"),
                 new("S20", "SITE-PAUSE", "PROPOSED_DIRECTORIES_ONLY")]),

            new("CHANGE", "변경협의/환경보전방안: 원평가 참조·별도 계약·재협의 검토", new(
                "REP-CHG", "환경보전방안_가상사업",
                [new("CTR-BASE", "원평가 용역"), new("CTR-CHG", "후속 변경검토 용역")],
                [new("A-BASE", "CTR-BASE", "environmental"),
                 new("A-CHG", "CTR-CHG", "change", "A-BASE")]),
                [Scenario("S17", "CHG-01", caseCode:"A-CHG"),
                 Scenario("S18", "REVIEW-01", caseCode:"A-CHG")],
                [
                    $"{s}/04_변경_재협의/A-CHG/01_기존평가_협의근거",
                    $"{s}/04_변경_재협의/A-CHG/90_발생사건/20_상황별_검토/S17_CHG-01/01_원평가_원협의_참조",
                    $"{s}/04_변경_재협의/A-CHG/90_발생사건/20_상황별_검토/S18_REVIEW-01/02_변경전후_영향검토",
                    "10_계약_용역관리/07_계약별/CTR-CHG/01_계약원본_변경이력"
                ],
                [$"{s}/01_전략환경영향평가", $"{s}/03_소규모환경영향평가",
                 $"{s}/05_사후환경영향조사"],
                [new("S17", "CHG-01", "PROPOSED_DIRECTORIES_ONLY"),
                 new("S18", "REVIEW-01", "PROPOSED_DIRECTORIES_ONLY")]),
        };
        return Array.AsReadOnly(fixtures);
    }

    /// <summary>
    /// Execute existing real planners. A fixture PASS is only a model/contract PASS.
    /// It is not a legal, customer, Windows, NAS or filesystem execution PASS.
    /// </summary>
    public static IReadOnlyList<EiaRepresentativeFinding> RunDraftAudit()
    {
        var profile = EiaWorkspaceProfilePlanner.CreateInitial(
            "QA-REPRESENTATIVE", "SUNRISE-QA", "TOR-DRAFT", [], [],
            "가상 평가 5유형의 개발중 템플릿 정합성 검사", false);
        var evidence = new EiaWorkspaceReviewEvidence("QA-SYNTHETIC", "DRAFT-ONLY",
            false, false, false, false, false, false);
        string[] allTypeRoots = EiaWorkspacePreviewPlanner.LoadDraftCatalog().WorkstreamPaths.Values
            .Select(v => v[0].Split('/')[0]).ToArray();
        var results = new List<EiaRepresentativeFinding>();
        foreach (var fixture in BuildDraftFixtures())
        {
            var failures = new List<string>();
            var scenario = EiaWorkspaceScenarioPlanner.BuildPreview(fixture.Request, fixture.Scenarios);
            var snapshot = EiaWorkspaceReadinessPlanner.BuildPreview(profile,
                fixture.Request, fixture.Scenarios, evidence, "SUNRISE-QA", "TOR-DRAFT");
            var paths = new HashSet<string>(snapshot.DirectoryPaths, StringComparer.OrdinalIgnoreCase);
            foreach (string path in fixture.MustContainPaths)
                if (!paths.Contains(path)) failures.Add("MISSING: " + path);
            foreach (string path in fixture.MustNotContainPaths)
                if (paths.Any(p => p.Equals(path, StringComparison.OrdinalIgnoreCase)
                    || p.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)))
                    failures.Add("FORBIDDEN: " + path);
            // Prevent every unselected assessment type from appearing implicitly.
            var selectedTypes = fixture.Request.AssessmentCases.Select(x => x.Workstream)
                .Distinct(StringComparer.Ordinal).ToArray();
            var catalog = EiaWorkspacePreviewPlanner.LoadDraftCatalog();
            foreach (string typeRoot in allTypeRoots)
                if (!selectedTypes.Any(id => catalog.WorkstreamPaths[id][0].Split('/')[0] == typeRoot)
                    && paths.Any(p => p.StartsWith(EiaRoot + "/" + typeRoot + "/", StringComparison.OrdinalIgnoreCase)))
                    failures.Add("UNSELECTED_WORKSTREAM: " + typeRoot);
            if (scenario.Outcomes.Count != fixture.ExpectedScenarios.Count)
                failures.Add("SCENARIO_COUNT_MISMATCH");
            foreach (var expected in fixture.ExpectedScenarios)
                if (scenario.Outcomes.Count(o => o.ScenarioId == expected.ScenarioId
                    && o.ReferenceCode == expected.ReferenceCode
                    && o.OutcomeKind == expected.OutcomeKind) != 1)
                    failures.Add("SCENARIO_OUTCOME_MISMATCH: " + expected.ScenarioId + "/" + expected.ReferenceCode);
            if (scenario.Baseline.Relationships.Count != fixture.Request.AssessmentCases.Count
                || scenario.Baseline.Contracts.Count != fixture.Request.Contracts.Count)
                failures.Add("CONTRACT_CASE_COUNT_MISMATCH");
            if (snapshot.ReviewStatus != EiaWorkspaceReadinessPlanner.PendingStatus
                || snapshot.OutstandingHumanReviews.Count == 0
                || snapshot.UnverifiedRuntimeGates.Count < 5
                || !snapshot.UserApprovalRequired || snapshot.SupportsFilesystemWrites
                || snapshot.DirectoryPaths.Count != paths.Count)
                failures.Add("READONLY_OR_APPROVAL_BOUNDARY_BROKEN");
            if (!string.Equals(snapshot.FingerprintSha256,
                EiaWorkspaceReadinessPlanner.BuildPreview(profile, fixture.Request,
                    fixture.Scenarios, evidence, "SUNRISE-QA", "TOR-DRAFT").FingerprintSha256,
                StringComparison.Ordinal))
                failures.Add("NONDETERMINISTIC_PREVIEW");
            results.Add(new(fixture.FixtureId, fixture.Description, paths.Count,
                fixture.Scenarios.Count, snapshot.FingerprintSha256,
                Array.AsReadOnly(failures.ToArray()), snapshot.OutstandingHumanReviews,
                snapshot.UnverifiedRuntimeGates, true, false));
        }
        return Array.AsReadOnly(results.ToArray());
    }
}
