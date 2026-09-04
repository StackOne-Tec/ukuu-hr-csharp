using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UkuuHr.Sync.Services;

namespace UkuuHr.Sync.ViewModels;

/// <summary>
/// Main view model for the Ukuu HR Access Sync Bridge GUI.
/// Drives all UI state: settings, sync control, activity log.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly SyncService _syncService = new();
    private SyncSettings _settings;
    private readonly string _settingsPath;

    // ═══════════════════════════════════════════════════════════════════════
    // Constructor
    // ═══════════════════════════════════════════════════════════════════════

    public MainViewModel()
    {
        _settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
        _settings = SyncSettings.Load(_settingsPath);

        // Apply initial date range
        var (from, to) = _settings.GetDateRange();
        CustomFrom = new DateTimeOffset(from);
        CustomTo = new DateTimeOffset(to);

        // Subscribe to sync service events
        _syncService.LogAdded += entry =>
        {
            Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                LogEntries.Add(entry);
                if (LogEntries.Count > 200)
                    LogEntries.RemoveAt(0);
                LatestLogMessage = entry.Message;
                LatestLogLevel = entry.Level;
            });
        };

        _syncService.SyncCompleted += result =>
        {
            Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                UpdateSyncResult(result);
                IsSyncing = false;
            });
        };

        _syncService.ConnectionStateChanged += connected =>
        {
            Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsDeviceConnected = connected;
            });
        };

        // Initialize date range presets
        DateRangePresets = new List<DateRangePresetItem>
        {
            new(DateRangePreset.Today, "Today"),
            new(DateRangePreset.Yesterday, "Yesterday"),
            new(DateRangePreset.Last7Days, "Last 7 Days"),
            new(DateRangePreset.Last30Days, "Last 30 Days"),
            new(DateRangePreset.ThisMonth, "This Month"),
            new(DateRangePreset.LastMonth, "Last Month"),
            new(DateRangePreset.Custom, "Custom Range"),
        };

        // Load settings into UI fields
        LoadSettingsToUi();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Observable Properties
    // ═══════════════════════════════════════════════════════════════════════

    // ── Navigation ───────────────────────────────────────────────────────────
    [ObservableProperty] private int _selectedTabIndex;

    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsDashboardTab));
        OnPropertyChanged(nameof(IsSyncTab));
        OnPropertyChanged(nameof(IsSettingsTab));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageSubtitle));
    }

    public bool IsDashboardTab => SelectedTabIndex == 0;
    public bool IsSyncTab => SelectedTabIndex == 1;
    public bool IsSettingsTab => SelectedTabIndex == 2;

    public string PageTitle => SelectedTabIndex switch
    {
        0 => "Dashboard",
        1 => "Sync Center",
        _ => "Settings"
    };

    public string PageSubtitle => SelectedTabIndex switch
    {
        0 => "Live overview of device sync activity",
        1 => "Pull access records from your device and push them to Ukuu HR",
        _ => "Configure device, cloud and retrieval settings"
    };

    // Sidebar navigation items (SelectedIndex drives SelectedTabIndex)
    public List<NavItem> NavItems { get; } = new() { new("Dashboard"), new("Sync Center"), new("Settings") };

    // ── Device Connection ────────────────────────────────────────────────────
    [ObservableProperty] private string _deviceIp = "192.168.1.137";
    [ObservableProperty] private int _devicePort = 80;
    [ObservableProperty] private bool _useHttps;
    [ObservableProperty] private string _deviceUsername = "admin";
    [ObservableProperty] private string _devicePassword = "";
    [ObservableProperty] private bool _isDeviceConnected;

    // ── Cloud API ────────────────────────────────────────────────────────────
    [ObservableProperty] private string _cloudUrl = "https://ukuuhr.com";
    [ObservableProperty] private string _apiKey = "";
    [ObservableProperty] private bool _showApiKey;

    // ── Sync Status ──────────────────────────────────────────────────────────
    [ObservableProperty] private bool _isSyncing;
    [ObservableProperty] private bool _autoSyncEnabled = true;
    [ObservableProperty] private int _syncIntervalMinutes = 5;
    [ObservableProperty] private DateTime _lastSyncTime;
    [ObservableProperty] private int _totalRecordsSynced;
    [ObservableProperty] private SyncResult? _lastSyncResult;

    partial void OnLastSyncResultChanged(SyncResult? value)
    {
        OnPropertyChanged(nameof(HasSyncError));
    }

    partial void OnLastSyncTimeChanged(DateTime value) => OnPropertyChanged(nameof(LastSyncShort));
    public string LastSyncShort => LastSyncTime == default ? "—" : LastSyncTime.ToString("HH:mm:ss");

    // ── Last Result Counters (KPI cards) ─────────────────────────────────────
    [ObservableProperty] private int _lastFetched;
    [ObservableProperty] private int _lastImported;
    [ObservableProperty] private int _lastMatched;

    // ── Date Range ───────────────────────────────────────────────────────────
    [ObservableProperty] private DateRangePreset _selectedDatePreset = DateRangePreset.Last7Days;

    partial void OnSelectedDatePresetChanged(DateRangePreset value)
    {
        OnPropertyChanged(nameof(IsCustomDateRange));
        ApplyDateRange();
    }

    // Combo box selection uses the wrapper item type so the bound types always
    // match — the old enum-vs-item mismatch silently broke preset selection.
    [ObservableProperty] private DateRangePresetItem? _selectedDatePresetItem;

    partial void OnSelectedDatePresetItemChanged(DateRangePresetItem? value)
    {
        if (value is null) return;
        SelectedDatePreset = value.Preset;   // raises IsCustomDateRange + applies the range
    }

    // Real calendar pickers for the Custom preset — no free-text date parsing,
    // so malformed input can never reach the query path.
    [ObservableProperty] private DateTimeOffset? _customFrom;
    [ObservableProperty] private DateTimeOffset? _customTo;

    partial void OnCustomFromChanged(DateTimeOffset? value) => RefreshCustomRangeDisplay();
    partial void OnCustomToChanged(DateTimeOffset? value) => RefreshCustomRangeDisplay();

    [ObservableProperty] private string _dateRangeDisplay = "";

    private void RefreshCustomRangeDisplay()
    {
        if (SelectedDatePreset != DateRangePreset.Custom) return;
        if (CustomFrom is null || CustomTo is null) return;
        DateRangeDisplay = $"{CustomFrom.Value.DateTime:yyyy-MM-dd HH:mm}  to  {CustomTo.Value.DateTime:yyyy-MM-dd HH:mm}";
    }

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? new DateTimeOffset(dt)
            : null;

    // ── Activity Log ─────────────────────────────────────────────────────────
    public ObservableCollection<SyncLogEntry> LogEntries { get; } = new();
    [ObservableProperty] private string _latestLogMessage = "Ready";
    [ObservableProperty] private LogLevel _latestLogLevel = LogLevel.Info;

    // ── Fetched Records Table ─────────────────────────────────────────────────
    public ObservableCollection<ImportedPunch> FetchedRecords { get; } = new();
    [ObservableProperty] private int _fetchedRecordCount;

    // ── Status ───────────────────────────────────────────────────────────────
    [ObservableProperty] private string _statusMessage = "Ready";
    [ObservableProperty] private string _connectionStatus = "Not connected";
    [ObservableProperty] private string _deviceInfoText = "No device connected";

    // ── Date Range Presets ───────────────────────────────────────────────────
    public List<DateRangePresetItem> DateRangePresets { get; }

    // ── Computed Properties ──────────────────────────────────────────────────
    public bool IsCustomDateRange => SelectedDatePreset == DateRangePreset.Custom;
    public bool HasSyncError => !string.IsNullOrEmpty(LastSyncResult?.ErrorMessage);

    // ═══════════════════════════════════════════════════════════════════════
    // Commands
    // ═══════════════════════════════════════════════════════════════════════

    [RelayCommand(CanExecute = nameof(CanSync))]
    private async Task SyncNow()
    {
        if (IsSyncing) return;
        IsSyncing = true;
        StatusMessage = "Syncing...";

        try
        {
            SaveSettingsFromUi();
            var result = await _syncService.SyncAsync(_settings);
            UpdateSyncResult(result);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            LatestLogMessage = ex.Message;
            LatestLogLevel = LogLevel.Error;
        }
        finally
        {
            IsSyncing = false;
        }
    }
    private bool CanSync() => !IsSyncing;

    [RelayCommand]
    private async Task TestConnection()
    {
        StatusMessage = "Testing connection...";
        SaveSettingsFromUi();

        var ok = await _syncService.TestConnectionAsync(_settings);
        if (ok)
        {
            ConnectionStatus = "Connected";
            DeviceInfoText = $"{_syncService.DeviceName} ({_syncService.DeviceModel})";
            StatusMessage = $"Connected to {_syncService.DeviceName}";
        }
        else
        {
            ConnectionStatus = "Connection failed";
            DeviceInfoText = "Not connected";
            StatusMessage = "Connection failed — check device IP and credentials";
        }
    }

    [RelayCommand]
    private void SaveSettings()
    {
        SaveSettingsFromUi();
        _settings.Save(_settingsPath);
        StatusMessage = $"Settings saved to {_settingsPath}";
    }

    [RelayCommand]
    private void ToggleAutoSync()
    {
        AutoSyncEnabled = !AutoSyncEnabled;
        if (AutoSyncEnabled)
        {
            SaveSettingsFromUi();
            _syncService.StartAutoSync(_settings, result =>
            {
                Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    UpdateSyncResult(result);
                    StatusMessage = $"Auto-sync: {result.RecordsFetched} records";
                });
            });
            StatusMessage = $"Auto-sync enabled (every {_settings.SyncIntervalMinutes} min)";
        }
        else
        {
            _syncService.StopAutoSync();
            StatusMessage = "Auto-sync disabled";
        }
    }

    [RelayCommand]
    private void ApplyDateRange()
    {
        SaveSettingsFromUi();
        var (from, to) = _settings.GetDateRange();
        if (from > to) (from, to) = (to, from);   // never allow an inverted window
        DateRangeDisplay = $"{from:yyyy-MM-dd HH:mm}  to  {to:yyyy-MM-dd HH:mm}";
        StatusMessage = $"Date range: {DateRangeDisplay}";
    }

    [RelayCommand]
    private void ClearLog()
    {
        LogEntries.Clear();
        LatestLogMessage = "Log cleared";
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Settings ↔ UI Mapping
    // ═══════════════════════════════════════════════════════════════════════

    private void LoadSettingsToUi()
    {
        DeviceIp = _settings.DeviceIp;
        DevicePort = _settings.DevicePort;
        UseHttps = _settings.UseHttps.GetValueOrDefault(false);
        DeviceUsername = _settings.DeviceUsername;
        DevicePassword = _settings.DevicePassword;
        CloudUrl = _settings.CloudUrl;
        ApiKey = _settings.ApiKey ?? "";
        AutoSyncEnabled = _settings.AutoSyncEnabled;
        SyncIntervalMinutes = _settings.SyncIntervalMinutes;
        SelectedDatePreset = _settings.DateRangePreset;
        CustomFrom = ParseDate(_settings.CustomFromDate) ?? new DateTimeOffset(DateTime.UtcNow.AddDays(-7));
        CustomTo = ParseDate(_settings.CustomToDate) ?? new DateTimeOffset(DateTime.UtcNow);
        // Sync the combo box to the loaded preset (item type, matching the ItemsSource).
        SelectedDatePresetItem = DateRangePresets.FirstOrDefault(p => p.Preset == _settings.DateRangePreset);

        var (from, to) = _settings.GetDateRange();
        DateRangeDisplay = $"{from:yyyy-MM-dd HH:mm}  to  {to:yyyy-MM-dd HH:mm}";
    }

    private void SaveSettingsFromUi()
    {
        _settings.DeviceIp = DeviceIp;
        _settings.DevicePort = DevicePort;
        _settings.UseHttps = UseHttps;
        _settings.DeviceUsername = DeviceUsername;
        _settings.DevicePassword = DevicePassword;
        _settings.CloudUrl = CloudUrl;
        _settings.ApiKey = string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey;
        _settings.AutoSyncEnabled = AutoSyncEnabled;
        _settings.SyncIntervalMinutes = SyncIntervalMinutes;
        _settings.DateRangePreset = SelectedDatePreset;
        if (CustomFrom is not null) _settings.CustomFromDate = CustomFrom.Value.ToString("yyyy-MM-dd");
        if (CustomTo is not null) _settings.CustomToDate = CustomTo.Value.ToString("yyyy-MM-dd");
    }

    private void UpdateSyncResult(SyncResult result)
    {
        LastSyncResult = result;
        LastSyncTime = DateTime.Now;
        TotalRecordsSynced = _syncService.TotalRecordsSynced;
        LastFetched = result.RecordsFetched;
        LastImported = result.RecordsImported;
        LastMatched = result.EmployeesMatched;

        // Update fetched records table
        FetchedRecords.Clear();
        if (result.Records != null)
        {
            foreach (var record in result.Records)
                FetchedRecords.Add(record);
        }
        FetchedRecordCount = result.RecordsFetched;

        if (result.Success)
        {
            StatusMessage = result.RecordsFetched > 0
                ? $"Synced {result.RecordsFetched} records ({result.RecordsImported} imported)"
                : "Sync complete — no records found";
        }
        else
        {
            StatusMessage = $"Sync failed: {result.ErrorMessage}";
        }

        var (from, to) = _settings.GetDateRange();
        DateRangeDisplay = $"{from:yyyy-MM-dd HH:mm}  to  {to:yyyy-MM-dd HH:mm}";
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Lifecycle
    // ═══════════════════════════════════════════════════════════════════════

    public void Shutdown()
    {
        _syncService.StopAutoSync();
        SaveSettingsFromUi();
        _settings.Save(_settingsPath);
        _syncService.Dispose();
    }
}

/// <summary>Display item for date range preset combo box.</summary>
public record DateRangePresetItem(DateRangePreset Preset, string DisplayName);

/// <summary>Sidebar navigation entry.</summary>
public record NavItem(string Label);
