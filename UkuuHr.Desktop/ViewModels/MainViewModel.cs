using System.Collections.ObjectModel;
using System.ComponentModel;
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
        _customFromDate = from.ToString("yyyy-MM-dd");
        _customToDate = to.ToString("yyyy-MM-dd");

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
                LastSyncResult = result;
                LastSyncTime = DateTime.Now;
                TotalRecordsSynced = _syncService.TotalRecordsSynced;
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

    // ── Date Range ───────────────────────────────────────────────────────────
    [ObservableProperty] private DateRangePreset _selectedDatePreset = DateRangePreset.Last7Days;

    partial void OnSelectedDatePresetChanged(DateRangePreset value)
    {
        OnPropertyChanged(nameof(IsCustomDateRange));
        ApplyDateRange();
    }
    [ObservableProperty] private string _customFromDate = "";
    [ObservableProperty] private string _customToDate = "";
    [ObservableProperty] private string _dateRangeDisplay = "";

    // ── Activity Log ─────────────────────────────────────────────────────────
    public ObservableCollection<SyncLogEntry> LogEntries { get; } = new();
    [ObservableProperty] private string _latestLogMessage = "Ready";
    [ObservableProperty] private LogLevel _latestLogLevel = LogLevel.Info;

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
        CustomFromDate = _settings.CustomFromDate ?? DateTime.UtcNow.AddDays(-7).ToString("yyyy-MM-dd");
        CustomToDate = _settings.CustomToDate ?? DateTime.UtcNow.ToString("yyyy-MM-dd");

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
        _settings.CustomFromDate = CustomFromDate;
        _settings.CustomToDate = CustomToDate;
    }

    private void UpdateSyncResult(SyncResult result)
    {
        LastSyncResult = result;
        LastSyncTime = DateTime.Now;
        TotalRecordsSynced = _syncService.TotalRecordsSynced;

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
