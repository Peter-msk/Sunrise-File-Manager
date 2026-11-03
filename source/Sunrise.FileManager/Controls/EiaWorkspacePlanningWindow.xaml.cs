using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Sunrise.FileManager.Features.EiaWorkspace;

namespace Sunrise.FileManager.Controls;

/// <summary>Read-only DEV1 planning window. No Directory, File or updater calls.</summary>
public partial class EiaWorkspacePlanningWindow : Window
{
    public sealed class WorkstreamChoice
    {
        public string Id { get; init; } = "";
        public string Label { get; init; } = "";
        public bool Selected { get; set; }
    }

    /// <summary>Editable, in-memory UI row only. No persistence or execution authority.</summary>
    public sealed class DraftRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private string _code = "";
        private string _description = "";
        private string _caseCode = "";
        private string _contractCode = "";
        private string _workstream = "";
        private string _relatedCaseCode = "";
        private string _referenceCode = "";
        private string _agencyCode = "";
        private string _round = "";
        private string _year = "";
        private void Set(ref string field, string? value, string name)
        {
            value ??= "";
            if (field == value) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
        public string Code { get => _code; set => Set(ref _code, value, nameof(Code)); }
        public string Description { get => _description; set => Set(ref _description, value, nameof(Description)); }
        public string CaseCode { get => _caseCode; set => Set(ref _caseCode, value, nameof(CaseCode)); }
        public string ContractCode { get => _contractCode; set => Set(ref _contractCode, value, nameof(ContractCode)); }
        public string Workstream { get => _workstream; set => Set(ref _workstream, value, nameof(Workstream)); }
        public string RelatedCaseCode { get => _relatedCaseCode; set => Set(ref _relatedCaseCode, value, nameof(RelatedCaseCode)); }
        public string ReferenceCode { get => _referenceCode; set => Set(ref _referenceCode, value, nameof(ReferenceCode)); }
        public string AgencyCode { get => _agencyCode; set => Set(ref _agencyCode, value, nameof(AgencyCode)); }
        public string Round { get => _round; set => Set(ref _round, value, nameof(Round)); }
        public string Year { get => _year; set => Set(ref _year, value, nameof(Year)); }
        public IEnumerable<string> SignatureFields => new[] { Code, Description, CaseCode,
            ContractCode, Workstream, RelatedCaseCode, ReferenceCode, AgencyCode, Round, Year };
    }

    /// <summary>One explicit scenario selection; no approval or write authority.</summary>
    public sealed class ScenarioDraftRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private string _scenarioId = "", _title = "", _scopeLabel = "", _reference = "";
        private string _case = "", _contract = "", _round = "";
        private bool _confirmed, _reviewed;
        private void Set<T>(ref T field, T value, string name)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
        public string ScenarioId { get => _scenarioId; set => Set(ref _scenarioId, value ?? "", nameof(ScenarioId)); }
        public string Title { get => _title; set => Set(ref _title, value ?? "", nameof(Title)); }
        public string ScopeLabel { get => _scopeLabel; set => Set(ref _scopeLabel, value ?? "", nameof(ScopeLabel)); }
        public string ReferenceCode { get => _reference; set => Set(ref _reference, value ?? "", nameof(ReferenceCode)); }
        public string CaseCode { get => _case; set => Set(ref _case, value ?? "", nameof(CaseCode)); }
        public string ContractCode { get => _contract; set => Set(ref _contract, value ?? "", nameof(ContractCode)); }
        public string Round { get => _round; set => Set(ref _round, value ?? "", nameof(Round)); }
        public bool UserConfirmed { get => _confirmed; set => Set(ref _confirmed, value, nameof(UserConfirmed)); }
        public bool HumanLegalReviewConfirmed { get => _reviewed; set => Set(ref _reviewed, value, nameof(HumanLegalReviewConfirmed)); }
        public IEnumerable<string> SignatureFields => new[] { ScenarioId, ReferenceCode, CaseCode,
            ContractCode, Round, UserConfirmed ? "1" : "0", HumanLegalReviewConfirmed ? "1" : "0" };
    }

    public ObservableCollection<ScenarioDraftRow> ScenarioRows { get; } = new();
    private readonly EiaScenarioDefinition[] _scenarioDefinitions;
    public ObservableCollection<DraftRow> ExtraContracts { get; } = new();
    public ObservableCollection<DraftRow> ExtraCases { get; } = new();
    public ObservableCollection<DraftRow> DesignIntakes { get; } = new();
    public ObservableCollection<DraftRow> Corrections { get; } = new();
    public ObservableCollection<DraftRow> FollowupYears { get; } = new();

    private readonly List<WorkstreamChoice> _types;
    private EiaWorkspaceUiPreview? _lastPreview;
    private string? _lastInputSignature;
    private EiaWorkspaceProfileRevision? _loadedProfile;
    // A UI-only review note never participates in the approved planner fingerprint.
    // Neither this comparison nor its note is persisted or granted filesystem authority.
    private EiaWorkspaceProfileProjectComparison? _currentComparison;
    private int _destinationInspectionGeneration;
    private bool _destinationInspectionRunning;
    private int _profileDestinationInspectionGeneration;
    private bool _profileDestinationInspectionRunning;
    private CancellationTokenSource? _destinationInspectionCts;
    private CancellationTokenSource? _profileDestinationInspectionCts;
    private bool _windowClosed;
    // DEV15: only ONE filesystem scan may run at a time across both preview modes.
    // A cancelled synchronous Windows/SMB API may still be in flight; the next
    // scan waits asynchronously rather than starting another network traversal.
    private readonly SemaphoreSlim _destinationScanGate = new(1, 1);
    private bool _destinationProgressOpen;
    private bool _profileDestinationProgressOpen;
    // DEV16: immutable, already observed rows. View filters NEVER rescan or
    // affect strict BLOCKED/UNAPPROVED decisions.
    private IReadOnlyList<EiaFolderPathObservation> _diagnosticRows =
        Array.Empty<EiaFolderPathObservation>();
    private ICollectionView? _diagnosticView;
    private string _diagnosticSummary = "";
    // DEV23: read-only pre-execution handoff, never permission to create directories.
    // Invalidated with every scan, source/destination change, cancel or failure.
    private EiaWorkspaceExecutionReadiness? _executionReadiness;
    private string? _executionReadinessSignature;
    private string? _executionReadinessDestination;

    private async Task<T> RunExclusiveDestinationScanAsync<T>(Func<T> scan, CancellationToken token)
    {
        await _destinationScanGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            return await Task.Run(scan, token).ConfigureAwait(false);
        }
        finally
        {
            _destinationScanGate.Release();
        }
    }



    public EiaWorkspacePlanningWindow()
    {
        InitializeComponent();
        var catalog = EiaWorkspacePreviewPlanner.LoadDraftCatalog();
        _types = catalog.WorkstreamPaths.Keys.OrderBy(id => id, StringComparer.Ordinal)
            .Select(id => new WorkstreamChoice { Id = id,
                Label = catalog.Labels.TryGetValue(id, out var label) ? label : id }).ToList();
        _scenarioDefinitions = EiaWorkspaceScenarioPlanner.LoadDraftCatalog().Scenarios
            .OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
        ScenarioCatalogList.ItemsSource = _scenarioDefinitions;
        ScenarioRows.CollectionChanged += ScenarioRows_Changed;
        WorkstreamChoices.ItemsSource = _types;
        CaseWorkstreamColumn.ItemsSource = _types;
        DataContext = this;
        foreach (var collection in DetailCollections())
        {
            collection.CollectionChanged += DetailCollection_Changed;
            foreach (var row in collection) row.PropertyChanged += DetailProperty_Changed;
        }
        // An edited input invalidates the shown plan. Users must never mistake it
        // for a plan calculated from the current fields.
        ProjectCodeInput.TextChanged += Input_Changed;
        ProjectTitleInput.TextChanged += Input_Changed;
        ContractCodeInput.TextChanged += Input_Changed;
        ContractDescriptionInput.TextChanged += Input_Changed;
        CoreModeSelector.SelectionChanged += Input_Changed;
        WorkstreamChoices.AddHandler(CheckBox.CheckedEvent, new RoutedEventHandler(Input_Changed));
        WorkstreamChoices.AddHandler(CheckBox.UncheckedEvent, new RoutedEventHandler(Input_Changed));
        // Profile selectors invalidate only the separate profile viewer, never a plan.
        ProfileCompanyInput.TextChanged += ProfileSelector_Changed;
        ProfileRequirementInput.TextChanged += ProfileSelector_Changed;
        ProfileIdInput.TextChanged += ProfileSelector_Changed;
        DestinationPathInput.TextChanged += DestinationPath_Changed;
        DiagnosticStatusFilter.SelectionChanged += DiagnosticFilter_Changed;
        DiagnosticSearchInput.TextChanged += DiagnosticSearch_Changed;
        Closed += (_, _) =>
        {
            _windowClosed = true;
            _destinationInspectionCts?.Cancel();
            _profileDestinationInspectionCts?.Cancel();
        };
        UpdateBasicCaseLegend();
    }

    private void UpdateBasicCaseLegend()
    {
        var selected = _types.Where(x => x.Selected).OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
        BaseCasesText.Text = selected.Length == 0
            ? "기본 평가업무 미선택 · 상세 모드에서 평가업무를 직접 등록할 수 있습니다."
            : "기본 평가업무 자동번호: " + string.Join(" · ",
                selected.Select((x, index) => $"CASE-{index + 1:00} = {x.Label} ({x.Id})"));
    }

    private void Input_Changed(object sender, RoutedEventArgs e)
    {
        if (_types is not null) UpdateBasicCaseLegend();
        InvalidateDestinationInspection("사업 입력이 변경됐습니다. 계획을 재계산한 후 위치를 다시 점검하세요.");
        // Do not persist any input or use previous approvals after a changed field.
        if (_lastPreview is null) return;
        _lastPreview = null;
        _lastInputSignature = null;
        CopyButton.IsEnabled = false;
        InvalidateAppliedComparison("사업 입력이 변경됐습니다. 회사 표준 적용 비교를 다시 계산하세요.");
        FolderGrid.ItemsSource = null;
        ScenarioOutcomesGrid.ItemsSource = null;
        ScenarioOutcomeText.Text = "입력 변경: 업무상황 처리 결과를 다시 계산해야 합니다.";
        SummaryText.Text = "입력값이 바뀌었습니다. 폴더 계획을 다시 계산하세요.";
        StatusText.Text = "이전 미리보기는 무효화됐습니다. 실제 폴더는 생성되지 않았습니다.";
    }

    private IEnumerable<ObservableCollection<DraftRow>> DetailCollections()
    {
        yield return ExtraContracts;
        yield return ExtraCases;
        yield return DesignIntakes;
        yield return Corrections;
        yield return FollowupYears;
    }

    private void DetailCollection_Changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (DraftRow row in e.OldItems) row.PropertyChanged -= DetailProperty_Changed;
        if (e.NewItems is not null)
            foreach (DraftRow row in e.NewItems) row.PropertyChanged += DetailProperty_Changed;
        Input_Changed(this, new RoutedEventArgs());
    }

    private void DetailProperty_Changed(object? sender, PropertyChangedEventArgs e)
        => Input_Changed(this, new RoutedEventArgs());

    private void ScenarioRows_Changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (ScenarioDraftRow row in e.OldItems) row.PropertyChanged -= DetailProperty_Changed;
        if (e.NewItems is not null)
            foreach (ScenarioDraftRow row in e.NewItems) row.PropertyChanged += DetailProperty_Changed;
        Input_Changed(this, new RoutedEventArgs());
    }

    private void ScenarioFilter_Changed(object sender, TextChangedEventArgs e)
    {
        if (_scenarioDefinitions is null || ScenarioCatalogList is null) return;
        string keyword = ScenarioFilterInput.Text.Trim();
        ScenarioCatalogList.ItemsSource = _scenarioDefinitions.Where(x => keyword.Length == 0
            || x.Id.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || x.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || x.RiskGuard.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private void ScenarioCatalog_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ScenarioCatalogInfo is null) return;
        if (ScenarioCatalogList.SelectedItem is not EiaScenarioDefinition scenario)
        {
            ScenarioCatalogInfo.Text = "상황을 선택하면 적용 유형·검토 필요 여부를 표시합니다.";
            return;
        }
        string workstreams = scenario.AllowedWorkstreams.Length == 0
            ? "카탈로그상 유형 제한 없음" : string.Join(", ", scenario.AllowedWorkstreams);
        string mode = scenario.Mode switch {
            "folders" => "폴더 제안", "existing_design_intake" => "기존 설계수령 참조",
            "existing_correction_round" => "기존 보완차수 참조", _ => "안전검증만 표시(폴더 없음)"
        };
        ScenarioCatalogInfo.Text = $"적용 유형: {workstreams} / {mode} / " +
            $"{(scenario.HumanLegalReviewRequired ? "담당자 적용성 검토 필요" : "추가 담당자 확인 표시 없음")} " +
            "(공식 승인 아님)";
    }

    private void AddScenario_Click(object sender, RoutedEventArgs e)
    {
        if (ScenarioCatalogList.SelectedItem is not EiaScenarioDefinition scenario) return;
        if (ScenarioRows.Count >= 300)
        {
            StatusText.Text = "업무상황은 최대 300건까지 선택할 수 있습니다.";
            return;
        }
        ScenarioRows.Add(new ScenarioDraftRow {
            ScenarioId = scenario.Id, Title = scenario.Title,
            ScopeLabel = scenario.Scope switch { "case" => "평가업무", "contract" => "계약", _ => "사업 공통" },
            // Reference/confirmation/review remain blank until explicitly entered.
            UserConfirmed = false, HumanLegalReviewConfirmed = false
        });
        DetailTabs.SelectedItem = ScenarioTab;
    }

    private void RemoveScenario_Click(object sender, RoutedEventArgs e)
    {
        if (ScenarioRowsGrid.SelectedItem is ScenarioDraftRow row) ScenarioRows.Remove(row);
    }

    private void DetailMode_Changed(object sender, RoutedEventArgs e)
        => Input_Changed(this, e);

    private void DetailCell_Changed(object sender, DataGridCellEditEndingEventArgs e)
        => Input_Changed(this, new RoutedEventArgs());

    private void DetailRow_Changed(object sender, DataGridRowEditEndingEventArgs e)
        => Input_Changed(this, new RoutedEventArgs());

    private void AddContract_Click(object sender, RoutedEventArgs e) => ExtraContracts.Add(new DraftRow());
    private void AddCase_Click(object sender, RoutedEventArgs e) => ExtraCases.Add(new DraftRow());
    private void AddIntake_Click(object sender, RoutedEventArgs e) => DesignIntakes.Add(new DraftRow());
    private void AddCorrection_Click(object sender, RoutedEventArgs e) => Corrections.Add(new DraftRow());
    private void AddFollowup_Click(object sender, RoutedEventArgs e) => FollowupYears.Add(new DraftRow());

    private static void RemoveSelected(DataGrid grid, ObservableCollection<DraftRow> source)
    {
        if (grid.SelectedItem is DraftRow row) source.Remove(row);
    }
    private void RemoveContract_Click(object sender, RoutedEventArgs e) => RemoveSelected(ContractRowsGrid, ExtraContracts);
    private void RemoveCase_Click(object sender, RoutedEventArgs e) => RemoveSelected(CaseRowsGrid, ExtraCases);
    private void RemoveIntake_Click(object sender, RoutedEventArgs e) => RemoveSelected(IntakeRowsGrid, DesignIntakes);
    private void RemoveCorrection_Click(object sender, RoutedEventArgs e) => RemoveSelected(CorrectionRowsGrid, Corrections);
    private void RemoveFollowup_Click(object sender, RoutedEventArgs e) => RemoveSelected(FollowupRowsGrid, FollowupYears);

    private void CommitDetailedEdits()
    {
        // DEV20: CommitEdit returns false when validation leaves a cell/row
        // in edit mode. Never calculate, inspect or copy a plan from the OLD
        // bound value while the user sees an uncommitted NEW value in a grid.
        foreach (var grid in new[] { ContractRowsGrid, CaseRowsGrid, IntakeRowsGrid,
            CorrectionRowsGrid, FollowupRowsGrid, ScenarioRowsGrid })
        {
            if (!grid.CommitEdit(DataGridEditingUnit.Cell, true) ||
                !grid.CommitEdit(DataGridEditingUnit.Row, true))
            {
                grid.Focus();
                throw new InvalidOperationException(
                    "입력 표의 편집이 확정되지 않았습니다. 표시된 셀의 오류를 수정한 뒤 다시 실행하세요.");
            }
        }
    }

    private string CurrentInputSignature()
    {
        // Framed inputs prevent delimiter collisions (copy is still read-only).
        var signature = new StringBuilder();
        void Frame(string? value)
        {
            value ??= "";
            signature.Append(value.Length).Append(':').Append(value);
        }
        foreach (var item in new[] { ProjectCodeInput.Text, ProjectTitleInput.Text,
            ContractCodeInput.Text, ContractDescriptionInput.Text,
            (CoreModeSelector.SelectedItem as ComboBoxItem)?.Tag as string ?? "",
            DetailExpander.IsExpanded ? "DETAIL" : "BASIC" }) Frame(item);
        foreach (var type in _types.OrderBy(x => x.Id, StringComparer.Ordinal))
        { Frame(type.Id); Frame(type.Selected ? "1" : "0"); }
        if (DetailExpander.IsExpanded)
        {
            foreach (var collection in DetailCollections())
            {
                Frame(collection.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                foreach (var row in collection)
                    foreach (string field in row.SignatureFields) Frame(field);
            }
            Frame(ScenarioRows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var row in ScenarioRows)
                foreach (string field in row.SignatureFields) Frame(field);
        }
        return signature.ToString();
    }

    private EiaWorkspaceUiDetailedInput ReadDetailedInput(string mode)
    {
        var selected = _types.Where(x => x.Selected).OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
        // Basic cases retain the IDs disclosed by the UI. Additional rows supply
        // their own case IDs and may reference earlier cases across contracts.
        var contracts = new[] { new EiaWorkspaceUiContractInput(
            ContractCodeInput.Text, ContractDescriptionInput.Text) }
            .Concat(ExtraContracts.Select(x => new EiaWorkspaceUiContractInput(x.Code, x.Description)))
            .ToArray();
        var cases = selected.Select((x, i) => new EiaWorkspaceUiCaseInput(
            $"CASE-{i + 1:00}", ContractCodeInput.Text, x.Id))
            .Concat(ExtraCases.Select(x => new EiaWorkspaceUiCaseInput(
                x.Code, x.ContractCode, x.Workstream, x.RelatedCaseCode))).ToArray();
        return new EiaWorkspaceUiDetailedInput(
            ProjectCodeInput.Text, ProjectTitleInput.Text, contracts, cases,
            DesignIntakes.Select(x => new EiaWorkspaceUiIntakeInput(
                x.CaseCode, x.ReferenceCode, x.Round)).ToArray(),
            Corrections.Select(x => new EiaWorkspaceUiCorrectionInput(
                x.CaseCode, x.AgencyCode, x.Round)).ToArray(),
            FollowupYears.Select(x => new EiaWorkspaceUiFollowupInput(
                x.CaseCode, x.Year, x.Round)).ToArray(), mode);
    }

    private EiaWorkspaceUiScenarioInput[] ReadScenarioInputs() => ScenarioRows.Select(x =>
        new EiaWorkspaceUiScenarioInput(x.ScenarioId, x.ReferenceCode, x.CaseCode,
            x.ContractCode, x.Round, x.UserConfirmed, x.HumanLegalReviewConfirmed)).ToArray();

    private EiaWorkspaceUiPreview BuildDetailedPlan(string mode) =>
        EiaWorkspaceUiComposer.BuildDetailedDraft(ReadDetailedInput(mode), ReadScenarioInputs());

    // Recompute from the current input and current draft templates before any
    // clipboard export or profile review. No cached preview is an approval token.
    private EiaWorkspaceUiPreview RequireFreshVisiblePlan()
    {
        if (_lastPreview is null || _lastInputSignature != CurrentInputSignature())
            throw new InvalidOperationException("사업 입력이 변경됐습니다. 폴더 계획을 다시 계산하세요.");
        string mode = (CoreModeSelector.SelectedItem as ComboBoxItem)?.Tag as string
            ?? throw new ArgumentException("폴더 구성 유형을 선택하세요.");
        var fresh = DetailExpander.IsExpanded
            ? BuildDetailedPlan(mode)
            : EiaWorkspaceUiComposer.BuildSingleContractDraft(
                ProjectCodeInput.Text, ProjectTitleInput.Text,
                ContractCodeInput.Text, ContractDescriptionInput.Text,
                _types.Where(x => x.Selected).Select(x => x.Id).ToArray(), mode);
        EiaWorkspaceUiComposer.RequireSameReadOnlyPreview(_lastPreview, fresh);
        return fresh;
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        // Reset stale approval-like output before committing an edited cell:
        // failed validation must NEVER retain a copyable previous plan.
        _lastPreview = null;
        _lastInputSignature = null;
        CopyButton.IsEnabled = false;
        InvalidateDestinationInspection("폴더 계획을 다시 계산한 후 저장 위치를 점검하세요.");
        InvalidateAppliedComparison("사업 계획을 다시 계산하고 미승인 회사 초안을 재대조하세요.");
        FolderGrid.ItemsSource = null;
        ScenarioOutcomesGrid.ItemsSource = null;
        SummaryText.Text = "입력값 검토 중…";
        try
        {
            CommitDetailedEdits();
            string mode = (CoreModeSelector.SelectedItem as ComboBoxItem)?.Tag as string
                ?? throw new ArgumentException("폴더 구성 유형을 선택하세요.");
            var result = DetailExpander.IsExpanded
                ? BuildDetailedPlan(mode)
                : EiaWorkspaceUiComposer.BuildSingleContractDraft(
                    ProjectCodeInput.Text, ProjectTitleInput.Text,
                    ContractCodeInput.Text, ContractDescriptionInput.Text,
                    _types.Where(x => x.Selected).Select(x => x.Id).ToArray(), mode);
            _lastPreview = result;
            _lastInputSignature = CurrentInputSignature();
            SummaryText.Text = $"{result.RootFolderName} | {result.Entries.Count}개 상대 폴더 (말단 {result.LeafCount}개)";
            int scenarioCount = result.ScenarioOutcomes?.Count ?? 0;
            int scenarioSafetyGates = result.ScenarioOutcomes?.Count(x => x.OutcomeKind == "FUTURE_SAFETY_GATE_NOT_VERIFIED") ?? 0;
            int linkedRecords = result.ScenarioOutcomes?.Count(x => x.OutcomeKind.StartsWith("REFERENCES_", StringComparison.Ordinal)) ?? 0;
            ScenarioOutcomesGrid.ItemsSource = result.ScenarioOutcomes;
            ScenarioOutcomeText.Text = $"선택 상황 {scenarioCount}건 · 기존 자료 참조 {linkedRecords}건 · 안전검증 미완료 {scenarioSafetyGates}건 · 공식승인 아님";
            StatusText.Text = $"{(DetailExpander.IsExpanded ? "복합계약·차수·상황 검토" : "기본 계획")} · 회사 정책 {result.CompanyPolicyVersion} · 검증 대기 {result.UnverifiedGates.Count}종 · " +
                $"지문 {result.FingerprintSha256[..12]}… · 생성 기능 없음";
            UpdateFilter();
            CopyButton.IsEnabled = true;
            InspectDestinationButton.IsEnabled = !_destinationInspectionRunning;
            RefreshAppliedComparisonAvailability();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.IO.InvalidDataException)
        {
            SummaryText.Text = "폴더 계획을 계산하지 못했습니다.";
            StatusText.Text = "입력 오류: " + ex.Message;
            // Let the single authoritative planner validate paths. UI only moves focus
            // to a missing required field; it does not duplicate domain validation.
            foreach (var field in new[] { ProjectCodeInput, ProjectTitleInput,
                ContractCodeInput, ContractDescriptionInput })
            {
                if (!string.IsNullOrWhiteSpace(field.Text)) continue;
                field.Focus();
                break;
            }
            MessageBox.Show(this, ex.Message, "환경평가 용역 폴더 계획", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UpdateFilter()
    {
        if (FolderGrid is null) return;
        if (_lastPreview is null) { FolderGrid.ItemsSource = null; return; }
        var query = FilterInput.Text.Trim();
        FolderGrid.ItemsSource = _lastPreview.Entries.Where(row =>
            query.Length == 0 || row.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            row.CategoryLabel.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            row.OwnerLabel.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            row.Basis.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e) => UpdateFilter();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CommitDetailedEdits();
            RequireFreshVisiblePlan();
            Clipboard.SetText(EiaWorkspaceUiComposer.CreateCopyableReport(_lastPreview!));
            StatusText.Text = "최신 미승인·읽기 전용 계획을 클립보드에 복사했습니다.";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            StatusText.Text = "클립보드를 사용할 수 없습니다. 다른 프로그램의 사용이 끝난 뒤 다시 시도하세요.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                   IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // A changed draft template or uncommitted user input invalidates *all*
            // previously shown plan and profile review surfaces, not only the button.
            if (_lastPreview is not null) Input_Changed(this, new RoutedEventArgs());
            StatusText.Text = "계획 복사 차단: " + ex.Message;
            CopyButton.IsEnabled = false;
        }
    }

    // DEV16: no filesystem accesses during diagnostic view filtering. The
    // full, unfiltered observation is the only source of count/status truth.
    private string CurrentDiagnosticStatus() =>
        (DiagnosticStatusFilter.SelectedItem as ComboBoxItem)?.Tag as string
        ?? EiaWorkspaceDiagnosticFilter.All;

    private void DiagnosticFilter_Changed(object sender, SelectionChangedEventArgs e) =>
        RefreshDiagnosticFilter();

    private void DiagnosticSearch_Changed(object sender, TextChangedEventArgs e) =>
        RefreshDiagnosticFilter();

    private void RefreshDiagnosticFilter()
    {
        if (_diagnosticView is null || DestinationReviewSummary is null) return;
        _diagnosticView.Refresh();
        string status = CurrentDiagnosticStatus();
        string search = DiagnosticSearchInput.Text;
        int visible = _diagnosticRows.Count(row =>
            EiaWorkspaceDiagnosticFilter.Matches(row, status, search));
        // The BLOCKED count from the complete result must never be hidden by
        // a user-selected filter and cannot be used as a write approval.
        DestinationReviewSummary.Text = _diagnosticSummary +
            $" · 현재 표시 {visible}/{_diagnosticRows.Count}건 (표시 필터만 적용)";
    }

    // Display only; this is never a cached permission, approval, or creation plan.
    private void ShowDestinationDiagnostics(EiaFolderPathObservations report, string source)
    {
        _diagnosticRows = report.Rows
            .OrderBy(x => x.Status == "BLOCKED" ? 0 : 1)
            .ThenBy(x => x.ScopeLabel, StringComparer.Ordinal)
            .ThenBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
        _diagnosticSummary = source + " · 기존 " + report.Existing +
            " · 새 폴더 필요 " + report.Missing + " · 충돌/접근 불가 " + report.Blocked +
            " (총 " + report.Rows.Count + ") · 읽기 전용 관찰값/미승인";
        _diagnosticView = CollectionViewSource.GetDefaultView(_diagnosticRows);
        _diagnosticView.Filter = item => item is EiaFolderPathObservation row &&
            EiaWorkspaceDiagnosticFilter.Matches(row, CurrentDiagnosticStatus(),
                DiagnosticSearchInput.Text);
        DestinationDiagnosticGrid.ItemsSource = _diagnosticView;
        RefreshDiagnosticFilter();
        DestinationReviewTabs.SelectedIndex = 1;
    }

    private void ExecutionReadiness_Click(object sender, RoutedEventArgs e)
    {
        // DEV23: do not mistake a historical snapshot for live filesystem authority.
        // No CreateDirectory, creation engine executor, update service, or user
        // approval action may be reached from this button.
        if (_windowClosed || _destinationInspectionRunning || _profileDestinationInspectionRunning ||
            _executionReadiness is null || _executionReadinessSignature != CurrentInputSignature() ||
            _executionReadinessDestination != DestinationPathInput.Text ||
            _lastPreview is null || _lastInputSignature != CurrentInputSignature())
        {
            ClearDestinationDiagnostics("검토할 유효한 최신 위치 점검 결과가 없습니다. 위치를 다시 점검하세요.");
            return;
        }
        var dialog = new EiaWorkspaceExecutionReviewWindow(_executionReadiness) { Owner = this };
        dialog.ShowDialog();
    }

    private void ClearDestinationDiagnostics(string reason)
    {
        _executionReadiness = null;
        _executionReadinessSignature = null;
        _executionReadinessDestination = null;
        if (ExecutionReadinessButton is not null)
            ExecutionReadinessButton.IsEnabled = false;
        _diagnosticView = null;
        _diagnosticRows = Array.Empty<EiaFolderPathObservation>();
        _diagnosticSummary = "";
        if (DestinationDiagnosticGrid is not null)
            DestinationDiagnosticGrid.ItemsSource = null;
        if (DestinationReviewSummary is not null)
            DestinationReviewSummary.Text = reason;
    }

    private void InvalidateDestinationInspection(string message)
    {
        _destinationInspectionCts?.Cancel();
        _destinationProgressOpen = false;
        InvalidateProfileDestinationInspection("저장 위치 또는 사업 계획이 변경됐습니다. 프로파일 포함 점검을 다시 실행하세요.");
        _destinationInspectionGeneration++;
        ClearDestinationDiagnostics("현재 경로별 진단 결과 무효화: " + message);
        if (DestinationInspectionStatus is not null)
            DestinationInspectionStatus.Text = message;
        if (InspectDestinationButton is not null)
            InspectDestinationButton.IsEnabled = !_destinationInspectionRunning && _lastPreview is not null;
        if (CancelDestinationButton is not null)
            CancelDestinationButton.IsEnabled = _destinationInspectionRunning &&
                _destinationInspectionCts is { IsCancellationRequested: false };
    }

    private void CancelDestination_Click(object sender, RoutedEventArgs e)
    {
        _destinationInspectionGeneration++;
        _destinationInspectionCts?.Cancel();
        _destinationProgressOpen = false;
        CancelDestinationButton.IsEnabled = false;
        ClearDestinationDiagnostics("위치 점검 취소됨: 결과를 사용하지 않습니다.");
        DestinationInspectionStatus.Text = "중지 요청됨 · 진행 중인 Windows/NAS 조회 반환 후 중단됩니다.";
    }

    private void DestinationPath_Changed(object sender, TextChangedEventArgs e) =>
        InvalidateDestinationInspection("저장 위치가 변경됐습니다. 다시 점검해야 합니다.");

    private void ChooseDestination_Click(object sender, RoutedEventArgs e)
    {
        // Native Windows picker does not create or modify a directory.
        var picker = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "기존 저장 기준 폴더 선택 (읽기 전용)",
            Multiselect = false
        };
        if (picker.ShowDialog(this) == true)
            DestinationPathInput.Text = picker.FolderName;
    }

    private async void InspectDestination_Click(object sender, RoutedEventArgs e)
    {
        if (_destinationInspectionRunning) return;
        EiaWorkspaceUiPreview fresh;
        EiaCaseWorkspaceRequest request;
        EiaScenarioSelection[] selections;
        try
        {
            CommitDetailedEdits();
            fresh = RequireFreshVisiblePlan();
            string mode = (CoreModeSelector.SelectedItem as ComboBoxItem)?.Tag as string
                ?? throw new ArgumentException("구성 유형이 지정되지 않았습니다.");
            if (DetailExpander.IsExpanded)
                (request, selections) = EiaWorkspaceUiComposer.CompileDetailedInputs(
                    ReadDetailedInput(mode), ReadScenarioInputs());
            else
            {
                request = EiaWorkspaceUiComposer.CompileSingleContractRequest(
                    ProjectCodeInput.Text, ProjectTitleInput.Text,
                    ContractCodeInput.Text, ContractDescriptionInput.Text,
                    _types.Where(x => x.Selected).Select(x => x.Id).ToArray(), mode);
                selections = Array.Empty<EiaScenarioSelection>();
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException)
        {
            InvalidateDestinationInspection("폴더 계획 점검 차단: " + ex.Message);
            return;
        }
        // A newer scan always owns the shared diagnostics grid.
        if (_profileDestinationInspectionRunning)
            ProfileDestinationStatus.Text = "기본안 점검으로 이전 프로파일 점검을 중단했습니다.";
        _profileDestinationInspectionCts?.Cancel();
        _profileDestinationProgressOpen = false;
        _profileDestinationInspectionGeneration++;
        string basePath = DestinationPathInput.Text;
        int generation = ++_destinationInspectionGeneration;
        string signature = CurrentInputSignature();
        using var scanCts = new CancellationTokenSource();
        _destinationInspectionCts = scanCts;
        var token = scanCts.Token;
        _destinationInspectionRunning = true;
        _destinationProgressOpen = true;
        // DEV21: never show the previous scan while a new snapshot is being checked.
        ClearDestinationDiagnostics("새 기본안 위치 점검 중 · 이전 진단 결과 폐기");
        InspectDestinationButton.IsEnabled = false;
        CancelDestinationButton.IsEnabled = true;
        int expected = fresh.Entries.Count + 1;
        var progress = new Progress<int>(count =>
        {
            if (!_windowClosed && !token.IsCancellationRequested &&
                generation == _destinationInspectionGeneration && _destinationInspectionRunning &&
                _destinationProgressOpen)
                DestinationInspectionStatus.Text = $"읽기 전용 위치 점검 중 · {count}/{expected} 경로";
        });
        DestinationInspectionStatus.Text = _profileDestinationInspectionRunning
            ? "이전 프로파일 점검의 네트워크 조회 종료 대기 중… (쓰기 없음)"
            : "기존 저장 위치와 폴더 상태를 확인 중… (쓰기 없음)";
        try
        {
            var (diagnostics, inspection) = await RunExclusiveDestinationScanAsync(() =>
            {
                var scanned = EiaWorkspaceDestinationInspector.Diagnose(
                    basePath, request, selections, fresh, token,
                    (done, _) => ((IProgress<int>)progress).Report(done));
                token.ThrowIfCancellationRequested();
                EiaDestinationInspection? verified = scanned.Blocked == 0
                    ? EiaWorkspaceDestinationInspector.Inspect(basePath, request, selections, fresh, token)
                    : null;
                // DEV21: the detailed grid and strict status may come from different
                // filesystem snapshots. Reject mismatched states instead of showing
                // a stale row beside a misleading 'no conflicts' summary.
                if (verified is not null)
                    EiaWorkspaceDestinationInspector.RequireConsistentObservation(scanned,
                        fresh.FingerprintSha256, verified.PlanFingerprint);
                token.ThrowIfCancellationRequested();
                return (scanned, verified);
            }, token);
            _destinationProgressOpen = false;
            if (_windowClosed || token.IsCancellationRequested ||
                generation != _destinationInspectionGeneration ||
                signature != CurrentInputSignature() || basePath != DestinationPathInput.Text)
                return;
            ShowDestinationDiagnostics(diagnostics, "기본안 저장 위치");
            if (inspection is not null)
            {
                _executionReadiness = EiaWorkspaceExecutionReadinessPlanner.PrepareReadOnlyReview(
                    diagnostics, fresh.FingerprintSha256, inspection.PlanFingerprint, "기본안");
                _executionReadinessSignature = signature;
                _executionReadinessDestination = basePath;
                ExecutionReadinessButton.IsEnabled = true;
            }
            DestinationInspectionStatus.Text = inspection is not null
                ? $"기존 {inspection.ExistingDirectories}개 · 새 폴더 필요 {inspection.MissingDirectories}개 " +
                  $"(총 {inspection.TotalDirectories}개) · 충돌 없음 · 생성/승인 아님"
                : $"충돌/접근 불가 {diagnostics.Blocked}개 · 경로별 진단 탭에서 확인 · 생성 차단";
            DestinationInspectionStatus.ToolTip = diagnostics.WorkspacePath;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (!_windowClosed && generation == _destinationInspectionGeneration)
                DestinationInspectionStatus.Text = "위치 점검 취소 · 결과를 사용하지 않습니다.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
            InvalidDataException or IOException or UnauthorizedAccessException or System.Security.SecurityException or
            NotSupportedException)
        {
            if (!_windowClosed && !token.IsCancellationRequested &&
                generation == _destinationInspectionGeneration &&
                signature == CurrentInputSignature() && basePath == DestinationPathInput.Text)
            {
                ClearDestinationDiagnostics("기본안 위치 점검 실패/상태 변경 · 결과 재점검 필요");
                DestinationInspectionStatus.Text = "위치 점검 실패/충돌: " + ex.Message;
                DestinationInspectionStatus.ToolTip = null;
            }
        }
        finally
        {
            _destinationProgressOpen = false;
            _destinationInspectionRunning = false;
            if (ReferenceEquals(_destinationInspectionCts, scanCts))
                _destinationInspectionCts = null;
            CancelDestinationButton.IsEnabled = false;
            InspectDestinationButton.IsEnabled = !_windowClosed && _lastPreview is not null;
        }
    }

    // Every project edit, destination change and profile selector change invalidates
    // the profile preflight too. No cached status is permission to create folders.
    private void InvalidateProfileDestinationInspection(string message)
    {
        _profileDestinationInspectionCts?.Cancel();
        _profileDestinationProgressOpen = false;
        _profileDestinationInspectionGeneration++;
        ClearDestinationDiagnostics("프로파일 포함 경로 진단을 다시 실행하세요: " + message);
        if (ProfileDestinationStatus is not null)
        {
            ProfileDestinationStatus.Text = message;
            ProfileDestinationStatus.ToolTip = null;
        }
        RefreshProfileDestinationAvailability();
    }

    private void CancelProfileDestination_Click(object sender, RoutedEventArgs e)
    {
        _profileDestinationInspectionGeneration++;
        _profileDestinationInspectionCts?.Cancel();
        _profileDestinationProgressOpen = false;
        CancelProfileDestinationButton.IsEnabled = false;
        ClearDestinationDiagnostics("프로파일 포함 위치 점검 취소됨: 결과를 사용하지 않습니다.");
        ProfileDestinationStatus.Text = "중지 요청됨 · 진행 중인 Windows/NAS 조회 반환 후 중단됩니다.";
    }

    private void RefreshProfileDestinationAvailability()
    {
        if (InspectProfileDestinationButton is null) return;
        CancelProfileDestinationButton.IsEnabled = _profileDestinationInspectionRunning &&
            _profileDestinationInspectionCts is { IsCancellationRequested: false };
        InspectProfileDestinationButton.IsEnabled = !_profileDestinationInspectionRunning &&
            _currentComparison is not null && _loadedProfile is not null &&
            _lastPreview is not null && _lastInputSignature == CurrentInputSignature() &&
            DetailExpander.IsExpanded && !string.IsNullOrWhiteSpace(DestinationPathInput.Text);
    }

    private async void InspectProfileDestination_Click(object sender, RoutedEventArgs e)
    {
        if (_profileDestinationInspectionRunning) return;
        EiaWorkspaceUiPreview shownBaseline;
        EiaWorkspaceProfileRevision latestProfile;
        EiaWorkspaceProfileProjectComparison shownComparison;
        EiaCaseWorkspaceRequest request;
        EiaScenarioSelection[] selections;
        string company, requirement, profileId;
        try
        {
            CommitDetailedEdits();
            shownBaseline = RequireFreshVisiblePlan();
            if (!DetailExpander.IsExpanded || _loadedProfile is null || _currentComparison is null)
                throw new InvalidOperationException("회사 초안을 현재 사업에 적용 비교한 뒤 위치를 점검하세요.");
            shownComparison = _currentComparison;
            company = ProfileCompanyInput.Text;
            requirement = ProfileRequirementInput.Text;
            profileId = ProfileIdInput.Text;
            latestProfile = EiaWorkspaceDraftStore.LoadLatest(EiaWorkspaceDraftStore.DefaultRoot,
                company, requirement, profileId);
            if (latestProfile.RevisionNumber != _loadedProfile.RevisionNumber ||
                latestProfile.FingerprintSha256 != _loadedProfile.FingerprintSha256)
                throw new InvalidOperationException("회사 초안의 최신 개정본이 변경됐습니다. 다시 조회하세요.");
            string mode = (CoreModeSelector.SelectedItem as ComboBoxItem)?.Tag as string
                ?? throw new ArgumentException("폴더 구성 유형을 선택하세요.");
            (request, selections) = EiaWorkspaceUiComposer.CompileDetailedInputs(
                ReadDetailedInput(mode), ReadScenarioInputs());
            var current = EiaWorkspaceProfilePlanComparator.Compare(latestProfile,
                request, selections, company, requirement);
            EiaWorkspaceProfileDestinationInspector.RequireUnchangedComparison(shownComparison, current);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
            InvalidDataException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            InvalidateProfileDestinationInspection("프로파일 포함 위치 점검 차단: " + ex.Message);
            return;
        }
        // Do not allow the older basic scan to overwrite the shared detailed grid.
        if (_destinationInspectionRunning)
            DestinationInspectionStatus.Text = "프로파일 점검으로 이전 기본안 점검을 중단했습니다.";
        _destinationInspectionCts?.Cancel();
        _destinationProgressOpen = false;
        _destinationInspectionGeneration++;
        string basePath = DestinationPathInput.Text;
        string signature = CurrentInputSignature();
        int generation = ++_profileDestinationInspectionGeneration;
        using var scanCts = new CancellationTokenSource();
        _profileDestinationInspectionCts = scanCts;
        var token = scanCts.Token;
        _profileDestinationInspectionRunning = true;
        _profileDestinationProgressOpen = true;
        // DEV21: discard prior basic/profile rows before scanning the new revision.
        ClearDestinationDiagnostics("새 회사 프로파일 위치 점검 중 · 이전 진단 결과 폐기");
        RefreshProfileDestinationAvailability();
        int expected = shownComparison.AppliedPathCount + 1;
        var progress = new Progress<int>(count =>
        {
            if (!_windowClosed && !token.IsCancellationRequested &&
                generation == _profileDestinationInspectionGeneration && _profileDestinationInspectionRunning &&
                _profileDestinationProgressOpen)
                ProfileDestinationStatus.Text = $"기본/회사 위치 점검 중 · {count}/{expected} 경로";
        });
        ProfileDestinationStatus.Text = _destinationInspectionRunning
            ? "이전 기본안 점검의 네트워크 조회 종료 대기 중… (쓰기 없음)"
            : "회사·발주처 추가 경로와 기존 저장 위치를 확인 중… (쓰기 없음)";
        try
        {
            var (diagnostics, inspection) = await RunExclusiveDestinationScanAsync(() =>
            {
                var scanned = EiaWorkspaceProfileDestinationInspector.Diagnose(
                    basePath, latestProfile, request, selections, company, requirement,
                    shownBaseline, shownComparison, token,
                    (done, _) => ((IProgress<int>)progress).Report(done));
                token.ThrowIfCancellationRequested();
                EiaProfileDestinationInspection? verified = scanned.Blocked == 0
                    ? EiaWorkspaceProfileDestinationInspector.Inspect(
                        basePath, latestProfile, request, selections, company, requirement,
                        shownBaseline, shownComparison, token)
                    : null;
                if (verified is not null)
                    EiaWorkspaceProfileDestinationInspector.RequireConsistentObservation(scanned,
                        shownComparison.AppliedFingerprintSha256,
                        verified.PathInspectionFingerprintSha256);
                token.ThrowIfCancellationRequested();
                return (scanned, verified);
            }, token);
            _profileDestinationProgressOpen = false;
            if (_windowClosed || token.IsCancellationRequested ||
                generation != _profileDestinationInspectionGeneration ||
                signature != CurrentInputSignature() || basePath != DestinationPathInput.Text ||
                !ReferenceEquals(_currentComparison, shownComparison) ||
                company != ProfileCompanyInput.Text || requirement != ProfileRequirementInput.Text ||
                profileId != ProfileIdInput.Text)
                return;
            // The persisted revision may change while a slow NAS scan is running.
            var latestAgain = EiaWorkspaceDraftStore.LoadLatest(EiaWorkspaceDraftStore.DefaultRoot,
                company, requirement, profileId);
            if (latestAgain.RevisionNumber != latestProfile.RevisionNumber ||
                latestAgain.FingerprintSha256 != latestProfile.FingerprintSha256)
                throw new InvalidOperationException("점검 중 회사 초안 개정본이 변경됐습니다.");
            ShowDestinationDiagnostics(diagnostics, "기본안 + 회사·발주처 추가");
            if (inspection is not null)
            {
                _executionReadiness = EiaWorkspaceExecutionReadinessPlanner.PrepareReadOnlyReview(
                    diagnostics, shownComparison.AppliedFingerprintSha256,
                    inspection.PathInspectionFingerprintSha256, "기본안 + 회사·발주처 미승인 초안");
                _executionReadinessSignature = signature;
                _executionReadinessDestination = basePath;
                ExecutionReadinessButton.IsEnabled = true;
            }
            ProfileDestinationStatus.Text = inspection is not null
                ? $"기본 기존 {inspection.ExistingBaselineDirectories}/필요 {inspection.MissingBaselineDirectories} · " +
                  $"회사 추가 기존 {inspection.ExistingAddedDirectories}/필요 {inspection.MissingAddedDirectories} " +
                  $"(총 {inspection.TotalDirectories}개) · 충돌 없음 · 미승인/생성 없음"
                : $"기본/회사 경로 충돌·접근 불가 {diagnostics.Blocked}개 · 아래 경로별 진단 확인 · 생성 차단";
            ProfileDestinationStatus.ToolTip = diagnostics.WorkspacePath;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (!_windowClosed && generation == _profileDestinationInspectionGeneration)
                ProfileDestinationStatus.Text = "프로파일 포함 위치 점검 취소 · 결과를 사용하지 않습니다.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
            InvalidDataException or IOException or UnauthorizedAccessException or System.Security.SecurityException or
            NotSupportedException)
        {
            if (!_windowClosed && !token.IsCancellationRequested &&
                generation == _profileDestinationInspectionGeneration &&
                signature == CurrentInputSignature() && basePath == DestinationPathInput.Text)
            {
                ClearDestinationDiagnostics("프로파일 포함 위치 점검 실패/상태 변경 · 재점검 필요");
                ProfileDestinationStatus.Text = "프로파일 포함 위치 점검 실패/충돌: " + ex.Message;
                ProfileDestinationStatus.ToolTip = null;
            }
        }
        finally
        {
            _profileDestinationProgressOpen = false;
            _profileDestinationInspectionRunning = false;
            if (ReferenceEquals(_profileDestinationInspectionCts, scanCts))
                _profileDestinationInspectionCts = null;
            RefreshProfileDestinationAvailability();
        }
    }

    private void ResetProfileView(string appliedReason)
    {
        InvalidateAppliedComparison(appliedReason);
        _loadedProfile = null;
        CompareProfileButton.IsEnabled = false;
        ProfileEntriesGrid.ItemsSource = null;
        ProfileConflictsGrid.ItemsSource = null;
        ProfileChangesGrid.ItemsSource = null;
    }

    private void ProfileSelector_Changed(object sender, TextChangedEventArgs e)
    {
        ResetProfileView("회사·과업지시서 식별자가 변경됐습니다. 초안을 다시 조회하세요.");
        ProfileStatusText.Text = "프로파일 식별자가 바뀌었습니다. 저장된 미승인 초안을 다시 조회하세요.";
    }

    private void LoadProfile_Click(object sender, RoutedEventArgs e)
    {
        ResetProfileView("회사 초안을 새로 읽었습니다. 사업 적용 비교를 다시 요청하세요.");
        try
        {
            // Read the existing LocalAppData draft only; never create/repair storage.
            var revision = EiaWorkspaceDraftStore.LoadLatest(EiaWorkspaceDraftStore.DefaultRoot,
                ProfileCompanyInput.Text, ProfileRequirementInput.Text, ProfileIdInput.Text);
            var inspected = EiaWorkspaceProfileUiInspector.Inspect(revision);
            DisplayProfile(inspected);
            _loadedProfile = revision;
            CompareProfileButton.IsEnabled = revision.RevisionNumber > 1;
            RefreshAppliedComparisonAvailability();
            ProfileStatusText.Text = $"{revision.ProfileId} · 개정 {revision.RevisionNumber} · " +
                $"지문 {revision.FingerprintSha256[..12]}… · UNAPPROVED · 경로 제안 {inspected.Entries.Count}건 · " +
                $"회사/발주처 대조 {inspected.Conflicts.Count}건 · 생성 권한 없음";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                   IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ProfileStatusText.Text = "초안 조회 불가: " + ex.Message;
        }
    }

    private void CompareProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_loadedProfile is null || _loadedProfile.RevisionNumber <= 1) return;
        try
        {
            // No cached revision may be compared if another process has replaced/appended
            // the underlying chain. Fail closed and require an explicit fresh lookup.
            var latest = EiaWorkspaceDraftStore.LoadLatest(EiaWorkspaceDraftStore.DefaultRoot,
                ProfileCompanyInput.Text, ProfileRequirementInput.Text, ProfileIdInput.Text);
            if (latest.RevisionNumber != _loadedProfile.RevisionNumber ||
                latest.FingerprintSha256 != _loadedProfile.FingerprintSha256)
                throw new InvalidOperationException("새 개정본을 감지했습니다. 최신 초안을 다시 조회하세요.");
            // Verify chain identity and both content digests on every comparison.
            var previous = EiaWorkspaceDraftStore.LoadDraft(EiaWorkspaceDraftStore.DefaultRoot,
                _loadedProfile.CompanyCode, _loadedProfile.RequirementCode,
                _loadedProfile.ProfileId, _loadedProfile.RevisionNumber - 1);
            var inspected = EiaWorkspaceProfileUiInspector.Inspect(_loadedProfile, previous);
            DisplayProfile(inspected);
            ProfileStatusText.Text = $"개정 {previous.RevisionNumber} → {_loadedProfile.RevisionNumber} · " +
                $"차이 {inspected.Changes.Count}건 · 변경은 초안 제안 비교이며 실제 폴더 삭제·이동 없음";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                   IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ResetProfileView("초안 개정 비교에 실패했습니다. 프로파일을 다시 조회하세요.");
            ProfileStatusText.Text = "개정 비교 불가: " + ex.Message + " · 초안을 다시 조회하세요.";
        }
    }

    private void InvalidateAppliedComparison(string reason)
    {
        InvalidateProfileDestinationInspection("프로파일 적용 비교가 변경됐습니다. 다시 비교한 후 위치를 점검하세요.");
        if (CompareAppliedProfileButton is null) return;
        _currentComparison = null;
        CompareAppliedProfileButton.IsEnabled = false;
        ProfileAppliedPathsGrid.ItemsSource = null;
        ProfileAppliedDecisionsGrid.ItemsSource = null;
        ProfileAppliedFingerprintText.Text = "";
        ProfileReviewNotesInput.Text = "";
        ProfileReviewNotesInput.IsEnabled = false;
        CopyProfileReviewButton.IsEnabled = false;
        ProfileAppliedSummary.Text = reason;
    }

    private void RefreshAppliedComparisonAvailability()
    {
        if (CompareAppliedProfileButton is null) return;
        CompareAppliedProfileButton.IsEnabled = _loadedProfile is not null &&
            DetailExpander.IsExpanded && _lastPreview is not null &&
            _lastInputSignature == CurrentInputSignature();
    }

    private void CompareAppliedProfile_Click(object sender, RoutedEventArgs e)
    {
        // No stale preview, changed selectors, or edited grid cell may cross this boundary.
        InvalidateAppliedComparison("현재 사업 적용 비교를 검증 중입니다.");
        try { CommitDetailedEdits(); }
        catch (InvalidOperationException ex)
        {
            InvalidateAppliedComparison("회사 표준 적용 비교 차단: " + ex.Message);
            return;
        }
        if (_loadedProfile is null || !DetailExpander.IsExpanded || _lastPreview is null ||
            _lastInputSignature != CurrentInputSignature())
        {
            ProfileAppliedSummary.Text = "현재 사업 계획·회사 초안을 다시 조회한 뒤 비교하세요. 이전 결과는 재사용하지 않습니다.";
            return;
        }
        try
        {
            // The displayed baseline is rechecked in full, including paths,
            // scenario outcomes and safety advice, before applying an external draft.
            var fresh = RequireFreshVisiblePlan();
            string mode = (CoreModeSelector.SelectedItem as ComboBoxItem)?.Tag as string
                ?? throw new ArgumentException("폴더 구성 유형을 선택하세요.");
            var input = ReadDetailedInput(mode);
            var scenarioInputs = ReadScenarioInputs();
            // Another process may have appended a profile revision after the user loaded it.
            // Reject stale revisions rather than silently applying an earlier company draft.
            var latestProfile = EiaWorkspaceDraftStore.LoadLatest(EiaWorkspaceDraftStore.DefaultRoot,
                ProfileCompanyInput.Text, ProfileRequirementInput.Text, ProfileIdInput.Text);
            if (latestProfile.RevisionNumber != _loadedProfile.RevisionNumber ||
                latestProfile.FingerprintSha256 != _loadedProfile.FingerprintSha256)
                throw new InvalidOperationException("회사 초안 개정본이 변경됐습니다. 최신 초안을 다시 조회하세요.");
            var (request, scenarios) = EiaWorkspaceUiComposer.CompileDetailedInputs(input, scenarioInputs);
            var comparison = EiaWorkspaceProfilePlanComparator.Compare(latestProfile,
                request, scenarios, ProfileCompanyInput.Text, ProfileRequirementInput.Text);
            if (comparison.SupportsFilesystemWrites || !comparison.UserApprovalRequired ||
                comparison.ProfileFingerprintSha256 != latestProfile.FingerprintSha256 ||
                comparison.ProjectRoot != fresh.RootFolderName)
                throw new InvalidOperationException("회사 표준 적용 비교가 미승인 계획과 불일치합니다.");
            // The user-visible baseline and the baseline from the applied-profile planner
            // must describe exactly the same directories, not merely have equal counts.
            var visiblePaths = new HashSet<string>(fresh.Entries.Select(x => x.RelativePath),
                StringComparer.OrdinalIgnoreCase);
            var comparedBasePaths = comparison.PathDifferences
                .Where(x => x.ChangeLabel == "기본 계획 유지")
                .Select(x => x.RelativePath).ToArray();
            if (visiblePaths.Count != comparedBasePaths.Length ||
                !visiblePaths.SetEquals(comparedBasePaths))
                throw new InvalidOperationException("화면 기본 계획과 회사 표준 비교의 기준 폴더가 다릅니다.");
            _currentComparison = comparison;
            ProfileAppliedPathsGrid.ItemsSource = comparison.PathDifferences;
            ProfileAppliedDecisionsGrid.ItemsSource = comparison.RuleDecisions;
            ProfileAppliedFingerprintText.Text = comparison.AppliedFingerprintSha256;
            ProfileReviewNotesInput.IsEnabled = true;
            CopyProfileReviewButton.IsEnabled = true;
            ProfileAppliedSummary.Text = $"기본 {comparison.BaselinePathCount}개 → 프로파일 초안 {comparison.AppliedPathCount}개 · " +
                $"추가 제안 {comparison.AddedPathCount}개 · 제외 제안 {comparison.RemovedPathCount}개 · " +
                $"미검증 안전항목 {comparison.PendingSafetyGates.Count}건 · 미승인·실제 생성/삭제 없음";
            ProfileStatusText.Text = $"{comparison.ProfileId} 개정 {comparison.RevisionNumber} · " +
                $"적용 지문 {comparison.AppliedFingerprintSha256[..12]}… · 공식 승인 아님";
            RefreshAppliedComparisonAvailability();
            RefreshProfileDestinationAvailability();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                   IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // A failed comparison cannot keep the previous fingerprint or reviewer note.
            InvalidateAppliedComparison("회사 표준 적용 비교 불가: " + ex.Message +
                " · 입력·초안을 다시 확인하세요.");
        }
    }

    private void CopyProfileReview_Click(object sender, RoutedEventArgs e)
    {
        try { CommitDetailedEdits(); }
        catch (InvalidOperationException ex)
        {
            InvalidateAppliedComparison("검토 메모 복사 차단: " + ex.Message);
            return;
        }
        if (_currentComparison is null || _loadedProfile is null || _lastPreview is null ||
            _lastInputSignature != CurrentInputSignature())
        {
            InvalidateAppliedComparison("사업 입력 또는 비교가 변경됐습니다. 다시 비교한 뒤 메모를 복사하세요.");
            return;
        }
        try
        {
            // A copied review record must still match the displayed plan and
            // independently recalculated applied profile, not just a cached digest.
            var fresh = RequireFreshVisiblePlan();
            // External revision changes are caught even if no UI text change occurred.
            var latest = EiaWorkspaceDraftStore.LoadLatest(EiaWorkspaceDraftStore.DefaultRoot,
                ProfileCompanyInput.Text, ProfileRequirementInput.Text, ProfileIdInput.Text);
            if (latest.RevisionNumber != _loadedProfile.RevisionNumber ||
                latest.FingerprintSha256 != _loadedProfile.FingerprintSha256)
                throw new InvalidOperationException("최신 회사 초안이 변경되었습니다.");
            string mode = (CoreModeSelector.SelectedItem as ComboBoxItem)?.Tag as string
                ?? throw new ArgumentException("폴더 구성 유형을 선택하세요.");
            var (request, scenarios) = EiaWorkspaceUiComposer.CompileDetailedInputs(
                ReadDetailedInput(mode), ReadScenarioInputs());
            var current = EiaWorkspaceProfilePlanComparator.Compare(latest, request,
                scenarios, ProfileCompanyInput.Text, ProfileRequirementInput.Text);
            if (current.SupportsFilesystemWrites || !current.UserApprovalRequired ||
                current.AppliedFingerprintSha256 != _currentComparison.AppliedFingerprintSha256 ||
                current.ProfileFingerprintSha256 != _currentComparison.ProfileFingerprintSha256 ||
                current.ProfileId != _currentComparison.ProfileId ||
                current.RevisionNumber != _currentComparison.RevisionNumber ||
                current.ProjectRoot != fresh.RootFolderName ||
                current.BaselinePathCount != _currentComparison.BaselinePathCount ||
                current.AppliedPathCount != _currentComparison.AppliedPathCount ||
                current.AddedPathCount != _currentComparison.AddedPathCount ||
                current.RemovedPathCount != 0 ||
                current.PathDifferences.Count != _currentComparison.PathDifferences.Count ||
                !current.PathDifferences.SequenceEqual(_currentComparison.PathDifferences) ||
                !current.RuleDecisions.SequenceEqual(_currentComparison.RuleDecisions) ||
                !current.PendingSafetyGates.SequenceEqual(
                    _currentComparison.PendingSafetyGates, StringComparer.Ordinal))
                throw new InvalidOperationException("회사 초안 적용 비교가 변경됐습니다. 다시 비교하세요.");
            string note = ProfileReviewNotesInput.Text.Trim();
            string record = $"[미승인·읽기 전용 · 공식 승인 아님]\n" +
                $"사업 경로: {_currentComparison.ProjectRoot}\n" +
                $"회사 초안: {_currentComparison.ProfileId} / 개정 {_currentComparison.RevisionNumber}\n" +
                $"적용 지문(SHA-256): {_currentComparison.AppliedFingerprintSha256}\n" +
                $"개인 검토 메모(미저장): {note}\n" +
                "실제 폴더 생성·이동·삭제 없음; 이 기록은 회사·법정 승인 증거가 아닙니다.";
            Clipboard.SetText(record);
            ProfileStatusText.Text = "검토 메모를 클립보드에 복사했습니다. 자동 저장되지 않습니다.";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            ProfileStatusText.Text = "클립보드 사용 중입니다. 잠시 후 복사를 다시 선택하세요.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                   IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            InvalidateAppliedComparison("검토 메모 복사 불가: " + ex.Message + " · 최신 초안을 다시 조회하세요.");
        }
    }

    private void DisplayProfile(EiaWorkspaceProfileUiInspection inspected)
    {
        if (inspected.SupportsFilesystemWrites || !inspected.UserApprovalRequired ||
            inspected.ApprovalStatus != EiaWorkspaceProfilePlanner.ApprovalUnapproved)
            throw new InvalidOperationException("미승인 읽기 전용 표준 조회 조건을 위반했습니다.");
        ProfileEntriesGrid.ItemsSource = inspected.Entries;
        ProfileConflictsGrid.ItemsSource = inspected.Conflicts;
        ProfileChangesGrid.ItemsSource = inspected.Changes;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
