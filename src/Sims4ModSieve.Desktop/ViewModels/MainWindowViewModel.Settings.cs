using System.IO;
using Sims4ModSieve.Desktop.Infrastructure;
using Sims4ModSieve.Desktop.Services;

namespace Sims4ModSieve.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly SettingsSupportService settingsSupport;
    private bool isSettingsVisible;
    private string settingsSection = "folders";
    private string? trayRoot;
    private string settingsFeedback = string.Empty;
    private string modsFolderFeedback = string.Empty;
    private string trayFolderFeedback = string.Empty;
    private string diagnosticsFeedback = string.Empty;
    private string aboutFeedback = string.Empty;
    private string licenseText = string.Empty;
    private IReadOnlyList<DiagnosticLog> diagnosticLogs = [];
    private DefaultFolderCandidate? defaultFolderCandidate;

    public sealed record DefaultFolderCandidate(string Kind, string Path);

    public RelayCommand OpenSettingsCommand { get; private set; } = null!;
    public RelayCommand SelectSettingsSectionCommand { get; private set; } = null!;
    public RelayCommand ChangeDefaultModsRootCommand { get; private set; } = null!;
    public RelayCommand ChangeTrayRootCommand { get; private set; } = null!;
    public RelayCommand ClearTrayRootCommand { get; private set; } = null!;
    public RelayCommand FindDefaultFolderCommand { get; private set; } = null!;
    public RelayCommand ApplyDefaultFolderCandidateCommand { get; private set; } = null!;
    public RelayCommand CancelDefaultFolderCandidateCommand { get; private set; } = null!;
    public RelayCommand OpenModsFolderCommand { get; private set; } = null!;
    public RelayCommand OpenTrayFolderCommand { get; private set; } = null!;
    public AsyncRelayCommand RefreshDiagnosticsCommand { get; private set; } = null!;
    public AsyncRelayCommand CopyDiagnosticsCommand { get; private set; } = null!;
    public RelayCommand OpenLogFolderCommand { get; private set; } = null!;
    public RelayCommand OpenFeedbackCommand { get; private set; } = null!;
    public RelayCommand OpenProjectCommand { get; private set; } = null!;
    public RelayCommand OpenLicenseCommand { get; private set; } = null!;

    public bool IsSettingsVisible { get => isSettingsVisible; private set => SetProperty(ref isSettingsVisible, value); }
    public string SettingsSection => settingsSection;
    public bool IsFoldersSettings { get => settingsSection == "folders"; set { if (value && !IsBusy) SelectSettingsSection("folders"); } }
    public bool IsDiagnosticsSettings { get => settingsSection == "diagnostics"; set { if (value && !IsBusy) SelectSettingsSection("diagnostics"); } }
    public bool IsAboutSettings { get => settingsSection == "about"; set { if (value && !IsBusy) SelectSettingsSection("about"); } }
    public string? TrayRoot
    {
        get => trayRoot;
        private set
        {
            if (!SetProperty(ref trayRoot, value)) return;
            OnPropertyChanged(nameof(TrayRootDisplay));
            OnPropertyChanged(nameof(HasTrayRoot));
            OnPropertyChanged(nameof(HasNoTrayRoot));
            OnPropertyChanged(nameof(TrayRootActionText));
            OnPropertyChanged(nameof(HasTrayRootAvailabilityWarning));
        }
    }
    public string TrayRootDisplay => TrayRoot ?? "未设置";
    public bool HasTrayRoot => TrayRoot is not null;
    public bool HasNoTrayRoot => !HasTrayRoot;
    public string TrayRootActionText => HasTrayRoot ? "更改" : "选择文件夹";
    public bool HasTrayRootAvailabilityWarning => HasTrayRoot && !Directory.Exists(TrayRoot);
    public string SettingsFeedback { get => settingsFeedback; private set => SetProperty(ref settingsFeedback, value); }
    public string ModsFolderFeedback { get => modsFolderFeedback; private set => SetProperty(ref modsFolderFeedback, value); }
    public string TrayFolderFeedback { get => trayFolderFeedback; private set => SetProperty(ref trayFolderFeedback, value); }
    public string DiagnosticsFeedback { get => diagnosticsFeedback; private set => SetProperty(ref diagnosticsFeedback, value); }
    public string AboutFeedback { get => aboutFeedback; private set => SetProperty(ref aboutFeedback, value); }
    public string LicenseText { get => licenseText; private set => SetProperty(ref licenseText, value); }
    public bool HasLicenseText => !string.IsNullOrEmpty(LicenseText);
    public string ProductVersion => settingsSupport.ProductVersion;
    public IReadOnlyList<DiagnosticLog> DiagnosticLogs => diagnosticLogs;
    public bool HasNoDiagnosticErrors => !diagnosticLogs.Any(log => log.HasContent || log.ReadFailed);
    public DefaultFolderCandidate? FolderCandidate => defaultFolderCandidate;
    public bool HasModsFolderCandidate => defaultFolderCandidate?.Kind == "Mods";
    public bool HasTrayFolderCandidate => defaultFolderCandidate?.Kind == "Tray";

    private RelayCommand SettingsCommand(Action action) => new(_ =>
    {
        if (!IsBusy) action();
    }, _ => !IsBusy);

    private void InitializeSettings()
    {
        OpenSettingsCommand = SettingsCommand(ShowSettings);
        SelectSettingsSectionCommand = new(section =>
        {
            if (!IsBusy) SelectSettingsSection(section as string);
        }, _ => !IsBusy);
        ChangeDefaultModsRootCommand = SettingsCommand(ChangeDefaultModsRoot);
        ChangeTrayRootCommand = SettingsCommand(ChangeTrayRoot);
        ClearTrayRootCommand = SettingsCommand(ClearTrayRoot);
        FindDefaultFolderCommand = new(kind =>
        {
            if (!IsBusy && kind is "Mods" or "Tray") FindDefaultFolder((string)kind);
        }, _ => !IsBusy);
        ApplyDefaultFolderCandidateCommand = SettingsCommand(ApplyDefaultFolderCandidate);
        CancelDefaultFolderCandidateCommand = SettingsCommand(CancelDefaultFolderCandidate);
        OpenModsFolderCommand = SettingsCommand(() => RunFolderOperation("Mods", () => settingsSupport.OpenFolder(ModsRoot ?? string.Empty)));
        OpenTrayFolderCommand = SettingsCommand(() => RunFolderOperation("Tray", () => settingsSupport.OpenFolder(TrayRoot ?? string.Empty)));
        RefreshDiagnosticsCommand = new(RefreshDiagnosticsAsync, _ => DiagnosticsFeedback = "无法读取诊断信息", () => !IsBusy);
        CopyDiagnosticsCommand = new(async () =>
        {
            await RefreshDiagnosticsAsync();
            RunSettingsOperation(() => settingsSupport.CopyDiagnosticText(diagnosticLogs),
                message => DiagnosticsFeedback = message, "诊断信息已复制", "无法复制，请重试");
        }, _ => DiagnosticsFeedback = "无法复制，请重试", () => !IsBusy);
        OpenLogFolderCommand = SettingsCommand(() => RunSettingsOperation(() => settingsSupport.OpenFolder(settingsSupport.LogDirectory),
            message => DiagnosticsFeedback = message, string.Empty, "无法打开日志文件夹"));
        OpenFeedbackCommand = SettingsCommand(() => RunSettingsOperation(() => settingsSupport.OpenProjectPage(feedback: true),
            message => DiagnosticsFeedback = message, string.Empty, "无法打开反馈页面"));
        OpenProjectCommand = SettingsCommand(() => RunSettingsOperation(() => settingsSupport.OpenProjectPage(),
            message => AboutFeedback = message, string.Empty, "无法打开项目主页"));
        OpenLicenseCommand = new(document =>
        {
            if (IsBusy || document is not ("LICENSE" or "THIRD-PARTY-NOTICES.md")) return;
            RunSettingsOperation(() =>
            {
                LicenseText = settingsSupport.OpenLicenseDocument((string)document);
                OnPropertyChanged(nameof(HasLicenseText));
            }, message => AboutFeedback = message, string.Empty, "无法读取许可文件");
        }, _ => !IsBusy);
    }

    private void ShowSettings()
    {
        IsHomeVisible = false;
        IsScannerVisible = false;
        IsConflictVisible = false;
        IsSettingsVisible = true;
        IsModsRootAvailable = HasModsRoot && Directory.Exists(ModsRoot);
        OnPropertyChanged(nameof(HasTrayRootAvailabilityWarning));
        if (IsDiagnosticsSettings) RefreshDiagnosticsCommand.Execute(null);
    }

    private void SelectSettingsSection(string? section)
    {
        if (section is not ("folders" or "diagnostics" or "about") || section == settingsSection) return;
        settingsSection = section;
        OnPropertyChanged(nameof(SettingsSection));
        OnPropertyChanged(nameof(IsFoldersSettings));
        OnPropertyChanged(nameof(IsDiagnosticsSettings));
        OnPropertyChanged(nameof(IsAboutSettings));
        if (IsDiagnosticsSettings) RefreshDiagnosticsCommand.Execute(null);
    }

    private void ChangeDefaultModsRoot() => RunFolderOperation("Mods", () =>
    {
        var path = folderPicker.PickFolder("选择默认 Mods 文件夹", ModsRoot);
        if (path is not null) SaveFolder("Mods", path);
    });

    private void ChangeTrayRoot() => RunFolderOperation("Tray", () =>
    {
        var path = folderPicker.PickFolder("选择 Tray 文件夹", TrayRoot);
        if (path is not null) SaveFolder("Tray", path);
    });

    private void ClearTrayRoot() => SaveFolder("Tray", null);

    private bool SaveFolder(string kind, string? path)
    {
        var normalized = NormalizePathOrNull(path);
        if (path is not null && normalized is null)
        {
            SetFolderFeedback(kind, "文件夹路径无效");
            return false;
        }
        var candidate = new DuplicateSettings(kind == "Mods" ? normalized : ModsRoot, [], kind == "Tray" ? normalized : TrayRoot);
        if (!TrySaveSettings(candidate)) return false;
        if (kind == "Mods")
        {
            ModsRoot = normalized;
            IsModsRootAvailable = HasModsRoot && Directory.Exists(ModsRoot);
        }
        else
        {
            TrayRoot = normalized;
            OnPropertyChanged(nameof(HasTrayRootAvailabilityWarning));
        }
        SetFolderFeedback(kind, string.Empty);
        CancelDefaultFolderCandidate();
        return true;
    }

    private void FindDefaultFolder(string kind) => RunFolderOperation(kind, () =>
    {
        defaultFolderCandidate = settingsSupport.FindDefaultFolder(kind) is { } path ? new(kind, path) : null;
        NotifyFolderCandidate();
        SetFolderFeedback(kind, defaultFolderCandidate is null ? "未找到默认目录" : string.Empty);
    });

    private void ApplyDefaultFolderCandidate()
    {
        if (defaultFolderCandidate is { } candidate) SaveFolder(candidate.Kind, candidate.Path);
    }

    private void CancelDefaultFolderCandidate()
    {
        defaultFolderCandidate = null;
        NotifyFolderCandidate();
    }

    private void NotifyFolderCandidate()
    {
        OnPropertyChanged(nameof(FolderCandidate));
        OnPropertyChanged(nameof(HasModsFolderCandidate));
        OnPropertyChanged(nameof(HasTrayFolderCandidate));
    }

    public async Task RefreshDiagnosticsAsync()
    {
        diagnosticLogs = await settingsSupport.ReadDiagnosticsAsync();
        OnPropertyChanged(nameof(DiagnosticLogs));
        OnPropertyChanged(nameof(HasNoDiagnosticErrors));
    }

    private void SetFolderFeedback(string kind, string message)
    {
        if (kind == "Mods") ModsFolderFeedback = message;
        else TrayFolderFeedback = message;
    }

    private void RunFolderOperation(string kind, Action action)
    {
        SetFolderFeedback(kind, string.Empty);
        try { action(); }
        catch (Exception) { SetFolderFeedback(kind, "无法完成文件夹操作，请检查目录是否可访问"); }
    }

    private static void RunSettingsOperation(Action action, Action<string> feedback, string success, string failure)
    {
        try { action(); feedback(success); }
        catch (Exception) { feedback(failure); }
    }

    private void RefreshSettingsCommands()
    {
        foreach (var command in new[] { OpenSettingsCommand, SelectSettingsSectionCommand, ChangeDefaultModsRootCommand,
            ChangeTrayRootCommand, ClearTrayRootCommand, FindDefaultFolderCommand, ApplyDefaultFolderCandidateCommand,
            CancelDefaultFolderCandidateCommand, OpenModsFolderCommand, OpenTrayFolderCommand, OpenLogFolderCommand,
            OpenFeedbackCommand, OpenProjectCommand, OpenLicenseCommand }) command?.RaiseCanExecuteChanged();
        RefreshDiagnosticsCommand?.RaiseCanExecuteChanged();
        CopyDiagnosticsCommand?.RaiseCanExecuteChanged();
    }
}
