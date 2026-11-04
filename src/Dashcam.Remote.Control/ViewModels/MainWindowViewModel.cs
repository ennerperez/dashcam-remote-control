using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Humanizer;

namespace Dashcam.Remote
{
    public sealed class MainWindowViewModel : INotifyPropertyChanged
    {
        private readonly DeviceApi _api;
        private readonly DispatcherTimer _connectionTimer = new() { Interval = TimeSpan.FromSeconds(10) };
        private string _deviceIp = "192.168.169.1";
        private string _connectionStatus = "Disconnected";
        private string _activityText = string.Empty;
        private string _wifiSsid = string.Empty;
        private string _wifiPassword = string.Empty;
        private string _timezoneOffset = "-5";
        private string _rawEndpoint = string.Empty;
        private string _selectedFolder = "SDCard";
        private string _fileCountText = "SDCard";
        private string _selectedFileDetails = "Select a file.";
        private string _thumbnailStatusText = "Select a file to preview.";
        private DeviceFile? _selectedFile;
        private Bitmap? _thumbnail;
        private readonly Dictionary<string, List<DeviceFile>> _filesByFolder = new(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _fileLoadCancellation;
        private CancellationTokenSource? _thumbnailLoadCancellation;
        private bool _isFilesLoading;
        private bool _isThumbnailLoading;
        private bool _reloadFilesWhenCurrentLoadFinishes;
        private bool _connectionCheckRunning;
        private double _storageUsagePercent;
        private bool _isStorageAvailable;
        private IBrush _storageUsageBrush = Brush.Parse("#43D17D");
        private int _recordingSeconds;
        private bool _isRecording;
        private bool _isLivePreviewActive;
        private string _liveUrlText = "RTSP preview starts the recorder, then refreshes media info.";
        private bool _liveHeartbeatRunning;
        private readonly DispatcherTimer _recordingTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _liveHeartbeatTimer = new() { Interval = TimeSpan.FromSeconds(3) };
        private bool _isConnected;
        private bool _isConnecting;
        private bool _isActivityCollapsed;
        private bool _wifiRebootRequired;
        private int _selectedTabIndex;

        public MainWindowViewModel(DeviceApi? api = null)
        {
            _api = api ?? new DeviceApi();
            FileExplorer = new FileExplorerViewModel(_api);
            ConnectCommand = new AsyncCommand(ConnectOrDisconnectAsync, () => IsConnecting || IsConnected || IsValidIpv4(DeviceIp), allowWhileRunning: true);
            NavigateCommand = new RelayCommand(parameter => SelectedTabIndex = Convert.ToInt32(parameter, CultureInfo.InvariantCulture), parameter => IsNavigationEnabled || (Convert.ToInt32(parameter, CultureInfo.InvariantCulture) == 3 && IsFileExplorerNavigationEnabled));
            RefreshCommand = new AsyncCommand(RefreshDeviceAsync, () => IsConnected);
            ClearActivityCommand = new RelayCommand(_ => ActivityText = string.Empty);
            ToggleActivityCommand = new RelayCommand(_ => IsActivityCollapsed = !IsActivityCollapsed);
            CopyValueCommand = new AsyncParameterCommand(CopyValueAsync, parameter => IsConnected && parameter is string value && !string.IsNullOrWhiteSpace(value) && value != "—");
            DeviceActionCommand = new AsyncParameterCommand(ExecuteDeviceActionAsync, _ => IsConnected);
            LoadFilesCommand = new AsyncCommand(LoadFilesAsync, () => IsConnected);
            SelectSdCardCommand = new RelayCommand(_ => SelectedFolder = "SDCard", _ => IsConnected);
            LoadThumbnailCommand = new AsyncCommand(LoadThumbnailAsync, () => IsConnected && SelectedFile is not null);
            DownloadFileCommand = new AsyncCommand(DownloadSelectedFileAsync, () => IsConnected && SelectedFile is not null);
            DeleteFileCommand = new AsyncCommand(DeleteSelectedFileAsync, () => IsConnected && SelectedFile is not null);
            PlayFileCommand = new AsyncCommand(PlaySelectedFileAsync, () => IsConnected && SelectedFile is not null);
            SetTimezoneCommand = new AsyncCommand(SetTimezoneAsync, () => IsConnected);
            SetWifiCommand = new AsyncCommand(SetWifiAsync, () => IsConnected);
            FormatSdCommand = new AsyncCommand(FormatSdAsync, () => IsConnected);
            FactoryResetCommand = new AsyncCommand(FactoryResetAsync, () => IsConnected);
            RawRequestCommand = new AsyncCommand(RunRawRequestAsync, () => IsConnected);
            LoadSettingsCommand = new AsyncCommand(LoadSettingsAsync, () => IsConnected);
            StartLiveCommand = new AsyncCommand(StartLiveAsync, () => IsConnected && !IsLivePreviewActive);
            StopLiveCommand = new AsyncCommand(StopLivePreviewAsync, () => IsConnected && IsLivePreviewActive);
            ToggleRecordingCommand = new AsyncCommand(ToggleRecordingAsync, () => IsConnected);
            StartRecordingCommand = new AsyncCommand(() => SetRecordingAsync(true), () => IsConnected);
            StopRecordingCommand = new AsyncCommand(() => SetRecordingAsync(false), () => IsConnected);
            _recordingTimer.Tick += (_, _) =>
            {
                if (!IsRecording)
                {
                    return;
                }

                RecordingSeconds++;
                UpdateRecordingText();
            };
            _liveHeartbeatTimer.Tick += async (_, _) => await KeepLiveStreamAliveAsync();
            _connectionTimer.Tick += async (_, _) => await CheckConnectionAsync();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public FileExplorerViewModel FileExplorer { get; }
        public Func<string, Task>? CopyRequested { get; set; }

        public ICommand ConnectCommand { get; }
        public string AppVersion => $"v{typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}";
        public ICommand NavigateCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand ClearActivityCommand { get; }
        public ICommand ToggleActivityCommand { get; }
        public ICommand CopyValueCommand { get; }
        public ICommand DeviceActionCommand { get; }
        public ICommand LoadFilesCommand { get; }
        public ICommand SelectSdCardCommand { get; }
        public ICommand LoadThumbnailCommand { get; }
        public ICommand DownloadFileCommand { get; }
        public ICommand DeleteFileCommand { get; }
        public ICommand PlayFileCommand { get; }
        public ICommand SetTimezoneCommand { get; }
        public ICommand SetWifiCommand { get; }
        public ICommand FormatSdCommand { get; }
        public ICommand FactoryResetCommand { get; }
        public ICommand RawRequestCommand { get; }
        public ICommand LoadSettingsCommand { get; }
        public ICommand StartLiveCommand { get; }
        public ICommand StopLiveCommand { get; }
        public ICommand ToggleRecordingCommand { get; }
        public ICommand StartRecordingCommand { get; }
        public ICommand StopRecordingCommand { get; }

        public string DeviceIp
        {
            get => _deviceIp; set
            {
                if (Set(ref _deviceIp, value))
                {
                    NotifyCommands();
                }
            }
        }
        public string ConnectionStatus { get => _connectionStatus; private set => Set(ref _connectionStatus, value); }
        public string ActivityText { get => _activityText; set => Set(ref _activityText, value); }
        public string WifiSsid { get => _wifiSsid; set => Set(ref _wifiSsid, value); }
        public string WifiPassword { get => _wifiPassword; set => Set(ref _wifiPassword, value); }
        public string TimezoneOffset { get => _timezoneOffset; set => Set(ref _timezoneOffset, value); }
        public string RawEndpoint { get => _rawEndpoint; set => Set(ref _rawEndpoint, value); }
        public bool IsConnected
        {
            get => _isConnected; private set
            {
                if (!Set(ref _isConnected, value))
                {
                    return;
                }

                FileExplorer.IsConnected = value; if (value)
                {
                    _connectionTimer.Start();
                }
                else
                {
                    _connectionTimer.Stop();
                }

                OnPropertyChanged(nameof(IsNavigationEnabled)); OnPropertyChanged(nameof(IsFileExplorerNavigationEnabled)); OnPropertyChanged(nameof(IsHostEnabled)); NotifyConnectionPresentation(); NotifyCommands();
            }
        }
        public bool IsConnecting
        {
            get => _isConnecting; private set
            {
                if (!Set(ref _isConnecting, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(IsHostEnabled)); NotifyConnectionPresentation(); NotifyCommands();
            }
        }
        public bool IsNavigationEnabled => IsConnected && !IsLivePreviewActive;
        public bool IsFileExplorerNavigationEnabled => !IsLivePreviewActive;
        public bool IsHostEnabled => !IsConnected && !IsConnecting;
        public bool IsActivityCollapsed
        {
            get => _isActivityCollapsed; set
            {
                if (Set(ref _isActivityCollapsed, value))
                {
                    OnPropertyChanged(nameof(IsActivityExpanded));
                }
            }
        }
        public bool IsActivityExpanded => !IsActivityCollapsed;
        public int SelectedTabIndex
        {
            get => _selectedTabIndex;
            set
            {
                if (IsLivePreviewActive && value != _selectedTabIndex)
                {
                    OnPropertyChanged();
                    return;
                }
                if (_selectedTabIndex == 3 && value != 3)
                {
                    FileExplorer.OnLeaving();
                }

                if (!Set(ref _selectedTabIndex, value) || !IsConnected)
                {
                    return;
                }

                if (value == 2)
                {
                    LoadSettingsCommand.Execute(null);
                }

                if (value == 3)
                {
                    FileExplorer.OnEntered();
                }
            }
        }

        public string ConnectTooltip => IsConnecting ? "Cancel" : IsConnected ? "Close" : "Connect";
        public bool ShowConnectIcon => !IsConnecting && !IsConnected;
        public bool ShowCancelIcon => IsConnecting;
        public bool ShowDisconnectIcon => IsConnected;
        public bool IsConnectEnabled => IsConnecting || IsConnected || IsValidIpv4(DeviceIp);
        public bool WifiRebootRequired => _wifiRebootRequired;
        public double StorageUsagePercent { get => _storageUsagePercent; private set => Set(ref _storageUsagePercent, value); }
        public bool IsStorageAvailable { get => _isStorageAvailable; private set => Set(ref _isStorageAvailable, value); }
        public IBrush StorageUsageBrush { get => _storageUsageBrush; private set => Set(ref _storageUsageBrush, value); }
        public ObservableCollection<DeviceFile> Files { get; } = [];
        public ObservableCollection<FileFolderItem> FileFolders { get; } = [new("loop"), new("emr"), new("event"), new("park")];
        public ObservableCollection<DeviceSettingItem> Settings { get; } = [];
        public ObservableCollection<CameraSwitchItem> CameraActions { get; } = [];
        public string SelectedFolder
        {
            get => _selectedFolder;
            private set
            {
                if (!Set(ref _selectedFolder, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(IsSdCardSelected));
                OnPropertyChanged(nameof(IsSdCardOpen));
                OnPropertyChanged(nameof(SdCardIconPath));
                OnPropertyChanged(nameof(SelectedFolderItem));
                UpdateFolderIcons();
                ShowSelectedFolderFiles();
            }
        }
        public FileFolderItem? SelectedFolderItem
        {
            get => FileFolders.FirstOrDefault(folder => string.Equals(folder.Name, SelectedFolder, StringComparison.OrdinalIgnoreCase));
            set => SelectedFolder = value?.Name ?? "SDCard";
        }
        public bool IsSdCardSelected => string.Equals(SelectedFolder, "SDCard", StringComparison.OrdinalIgnoreCase);
        public bool IsSdCardOpen => IsSdCardSelected;
        public string SdCardIconPath => IsSdCardOpen ? "/Assets/Icons/folder_open.svg" : "/Assets/Icons/folder.svg";
        public string SdCardSummary => $"SDCard · {_filesByFolder.Values.Sum(files => files.Count):N0} files";
        public bool IsFilesLoading { get => _isFilesLoading; private set => Set(ref _isFilesLoading, value); }
        public bool IsThumbnailLoading { get => _isThumbnailLoading; private set => Set(ref _isThumbnailLoading, value); }
        public string ThumbnailStatusText { get => _thumbnailStatusText; private set => Set(ref _thumbnailStatusText, value); }
        public string FileCountText { get => _fileCountText; private set => Set(ref _fileCountText, value); }
        public string SelectedFileDetails { get => _selectedFileDetails; private set => Set(ref _selectedFileDetails, value); }
        public DeviceFile? SelectedFile
        {
            get => _selectedFile;
            set
            {
                if (!Set(ref _selectedFile, value))
                {
                    return;
                }

                UpdateSelectedFileDetails();
                foreach (var command in new[] { LoadThumbnailCommand, DownloadFileCommand, DeleteFileCommand })
                {
                    ((AsyncCommand)command).RaiseCanExecuteChanged();
                }

                ((AsyncCommand)PlayFileCommand).RaiseCanExecuteChanged();
                _thumbnailLoadCancellation?.Cancel();
                if (value is not null && IsConnected)
                {
                    _ = LoadSelectedFileThumbnailAsync(value);
                }
                else
                {
                    IsThumbnailLoading = false;
                    ThumbnailStatusText = "Select a file to preview.";
                }
            }
        }
        public Bitmap? Thumbnail { get => _thumbnail; private set => Set(ref _thumbnail, value); }
        public Func<string, string, Task<bool>>? ConfirmRequested { get; set; }
        public Func<DeviceFile, Task<Stream?>>? SaveFileStreamRequested { get; set; }
        public Action<IReadOnlyList<Uri>>? PlayRequested { get; set; }
        public Func<Uri, bool>? LiveStarted { get; set; }
        public Action? LiveStopped { get; set; }

        public string Model { get; private set; } = "—";
        public string Firmware { get; private set; } = "—";
        public string Storage { get; private set; } = "—";
        public string Recording { get; private set; } = "—";
        public int RecordingSeconds { get => _recordingSeconds; private set => Set(ref _recordingSeconds, value); }
        public bool IsRecording { get => _isRecording; private set => Set(ref _isRecording, value); }
        public bool IsLivePreviewActive
        {
            get => _isLivePreviewActive;
            private set
            {
                if (!Set(ref _isLivePreviewActive, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(IsNavigationEnabled));
                OnPropertyChanged(nameof(IsFileExplorerNavigationEnabled));
                NotifyCommands();
                if (value)
                {
                    _liveHeartbeatTimer.Start();
                }
                else
                {
                    _liveHeartbeatTimer.Stop();
                }
            }
        }
        public string LiveUrlText { get => _liveUrlText; private set => Set(ref _liveUrlText, value); }
        public string Company { get; private set; } = "—";
        public string Soc { get; private set; } = "—";
        public string CameraCount { get; private set; } = "—";
        public string Battery { get; private set; } = "—";
        public string Ssid { get; private set; } = "—";
        public string DeviceId { get; private set; } = "—";
        public string Hardware { get; private set; } = "—";

        public void Log(string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            ActivityText = string.IsNullOrEmpty(ActivityText) ? line : $"{ActivityText}{Environment.NewLine}{line}";
        }

        public void HandleCommandFailure(Exception exception)
        {
            Log($"Error: {exception.Message}");
            if (exception is DeviceApiException { ResultCode: null })
            {
                MarkDisconnected("Connection lost");
            }
        }

        public async Task ConnectOrDisconnectAsync()
        {
            if (IsConnecting)
            {
                _connectCancellation?.Cancel();
                return;
            }

            if (IsConnected)
            {
                if (IsLivePreviewActive)
                {
                    await StopLivePreviewAsync();
                }

                IsConnected = false;
                ClearDeviceData();
                ConnectionStatus = "Disconnected";
                Log("Disconnected from the camera.");
                return;
            }

            using var cancellation = new CancellationTokenSource();
            _connectCancellation = cancellation;
            IsConnecting = true;
            ConnectionStatus = "Connecting…";
            IsConnected = false;
            OnPropertyChanged(nameof(ConnectTooltip));
            OnPropertyChanged(nameof(ShowConnectIcon));
            OnPropertyChanged(nameof(ShowCancelIcon));
            OnPropertyChanged(nameof(ShowDisconnectIcon));
            try
            {
                _api.SetHost(DeviceIp);
                await RefreshDeviceAsync(cancellation.Token);
                IsConnected = true;
                ConnectionStatus = "Connected";
                Log($"Connected to {_api.Host}.");
                if (SelectedTabIndex == 3)
                {
                    FileExplorer.OnEntered();
                }

                if (SelectedTabIndex == 2)
                {
                    try { await LoadSettingsAsync(); }
                    catch (Exception exception) { Log($"Could not load device settings: {exception.Message}"); }
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                ConnectionStatus = "Disconnected";
                ClearDeviceData();
                Log("Connection attempt canceled.");
            }
            catch
            {
                ConnectionStatus = "Connection failed";
                ClearDeviceData();
                Log("Connection failed. Check device IP and Wi-Fi connection.");
            }
            finally
            {
                IsConnecting = false;
                _connectCancellation = null;
                NotifyConnectionPresentation();
            }
        }

        private CancellationTokenSource? _connectCancellation;

        public async Task RefreshDeviceAsync() => await RefreshDeviceAsync(CancellationToken.None);

        public async Task LoadFilesAsync()
        {
            if (!IsConnected)
            {
                return;
            }

            _fileLoadCancellation?.Cancel();
            using var cancellation = new CancellationTokenSource();
            _fileLoadCancellation = cancellation;
            IsFilesLoading = true;
            FileCountText = "Loading SD card…";
            try
            {
                var loadedFiles = new Dictionary<string, List<DeviceFile>>(StringComparer.OrdinalIgnoreCase);
                const int pageSize = 100;
                foreach (var folderName in new[] { "loop", "emr", "event", "park" })
                {
                    var folderFiles = new List<DeviceFile>();
                    for (var start = 0; ; start += pageSize)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        var end = start + pageSize - 1;
                        var response = await _api.GetInfoAsync($"getfilelist?folder={DeviceApi.QueryValue(folderName)}&start={start}&end={end}", cancellation.Token);
                        if (response.ValueKind != JsonValueKind.Array)
                        {
                            throw new InvalidOperationException($"The device returned an unexpected file list for '{folderName}'.");
                        }

                        var pageFileCount = 0;
                        foreach (var folderEntry in response.EnumerateArray())
                        {
                            if (!folderEntry.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                            {
                                continue;
                            }

                            foreach (var file in files.EnumerateArray())
                            {
                                folderFiles.Add(new DeviceFile(folderName, TextValue(file, "name", ""), LongValue(file, "size", 0), IntegerValue(file, "duration", -1), TextValue(file, "createtimestr", "")));
                                pageFileCount++;
                            }
                        }

                        if (pageFileCount < pageSize)
                        {
                            break;
                        }
                    }
                    loadedFiles[folderName] = folderFiles;
                }

                _filesByFolder.Clear();
                foreach (var (folder, files) in loadedFiles)
                {
                    _filesByFolder[folder] = files;
                }

                foreach (var folder in FileFolders)
                {
                    folder.FileCount = _filesByFolder.GetValueOrDefault(folder.Name)?.Count ?? 0;
                }

                ShowSelectedFolderFiles();
                var count = _filesByFolder.Values.Sum(files => files.Count);
                OnPropertyChanged(nameof(SdCardSummary));
                FileCountText = $"{SelectedFolder} · {Files.Count:N0} files";
                Log($"Loaded {count:N0} files from the SD card.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Leaving File Explorer cancels the in-flight device request.
            }
            catch
            {
                FileCountText = "SDCard · load failed";
                throw;
            }
            finally
            {
                if (ReferenceEquals(_fileLoadCancellation, cancellation))
                {
                    _fileLoadCancellation = null;
                    IsFilesLoading = false;
                }
                if (_reloadFilesWhenCurrentLoadFinishes && IsConnected && SelectedTabIndex == 3)
                {
                    _reloadFilesWhenCurrentLoadFinishes = false;
                    Dispatcher.UIThread.Post(RequestFileListLoad);
                }
            }
        }

        public async Task LoadThumbnailAsync()
        {
            var file = SelectedFile ?? throw new InvalidOperationException("Select a file first.");
            await LoadSelectedFileThumbnailAsync(file);
        }

        private async Task LoadSelectedFileThumbnailAsync(DeviceFile file)
        {
            _thumbnailLoadCancellation?.Cancel();
            using var cancellation = new CancellationTokenSource();
            _thumbnailLoadCancellation = cancellation;
            IsThumbnailLoading = true;
            ThumbnailStatusText = "Loading thumbnail…";
            Thumbnail = null;
            try
            {
                var path = DeviceApi.EscapeFilePath(file.Name, true);
                var bytes = await _api.GetBytesAsync($"app/getthumbnail?file={path}", cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                await using var stream = new MemoryStream(bytes);
                var bitmap = new Bitmap(stream);
                if (cancellation.IsCancellationRequested || !ReferenceEquals(SelectedFile, file))
                {
                    bitmap.Dispose();
                    return;
                }
                Thumbnail = bitmap;
                ThumbnailStatusText = "Preview loaded.";
                Log($"Loaded thumbnail for {Path.GetFileName(file.Name)}.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Selection changes and leaving File Explorer cancel the preview request.
            }
            catch (Exception exception)
            {
                if (ReferenceEquals(SelectedFile, file))
                {
                    ThumbnailStatusText = "Preview unavailable.";
                    Log($"Could not load thumbnail for {Path.GetFileName(file.Name)}: {exception.Message}");
                }
            }
            finally
            {
                if (ReferenceEquals(_thumbnailLoadCancellation, cancellation))
                {
                    _thumbnailLoadCancellation = null;
                    IsThumbnailLoading = false;
                }
            }
        }

        private void RequestFileListLoad()
        {
            var command = (AsyncCommand)LoadFilesCommand;
            if (command.IsRunning)
            {
                _reloadFilesWhenCurrentLoadFinishes = true;
                return;
            }
            command.Execute(null);
        }

        private void CancelFileExplorerLoads()
        {
            _reloadFilesWhenCurrentLoadFinishes = false;
            _fileLoadCancellation?.Cancel();
            _thumbnailLoadCancellation?.Cancel();
        }

        private void UpdateFolderIcons()
        {
            foreach (var folder in FileFolders)
            {
                folder.IsOpen = string.Equals(folder.Name, SelectedFolder, StringComparison.OrdinalIgnoreCase);
            }
        }

        private void ShowSelectedFolderFiles()
        {
            Files.Clear();
            SelectedFile = null;
            IEnumerable<DeviceFile> visibleFiles = IsSdCardSelected
                ? _filesByFolder.Values.SelectMany(files => files)
                : _filesByFolder.GetValueOrDefault(SelectedFolder) ?? [];

            foreach (var file in visibleFiles.OrderByDescending(file => file.CreatedAt, StringComparer.Ordinal))
            {
                Files.Add(file);
            }

            SelectedFile = Files.FirstOrDefault();
            if (!IsFilesLoading)
            {
                FileCountText = $"{SelectedFolder} · {Files.Count:N0} files";
            }
        }

        private async Task DownloadSelectedFileAsync()
        {
            var file = SelectedFile ?? throw new InvalidOperationException("Select a file first.");
            if (SaveFileStreamRequested is null)
            {
                throw new InvalidOperationException("File download is unavailable on this window.");
            }

            await using var output = await SaveFileStreamRequested(file);
            if (output is null)
            {
                return;
            }

            var relativePath = DeviceApi.EscapeFilePath(file.Name, false);
            var bytes = await _api.GetBytesAsync(relativePath);
            await output.WriteAsync(bytes);
            Log($"Saved {file.Name} ({bytes.Length:N0} bytes).");
        }

        private async Task DeleteSelectedFileAsync()
        {
            var file = SelectedFile ?? throw new InvalidOperationException("Select a file first.");
            if (ConfirmRequested is null || !await ConfirmRequested("Delete file", $"Delete {Path.GetFileName(file.Name)} from the camera?"))
            {
                return;
            }

            var path = DeviceApi.EscapeFilePath(file.Name, true);
            await _api.GetInfoAsync($"deletefile?file={path}");
            Log($"Deleted {file.Name}.");
            await LoadFilesAsync();
        }

        private async Task PlaySelectedFileAsync()
        {
            var file = SelectedFile ?? throw new InvalidOperationException("Select a file first.");
            if (PlayRequested is null)
            {
                return;
            }

            var cache = await PlaybackMediaCache.DownloadAsync(_api, [file], (message, _) =>
                Dispatcher.UIThread.Post(() => Log(message)));
            try
            {
                PlayRequested(cache.MediaUris);
                Log($"Playing recording {Path.GetFileName(file.Name)}.");
            }
            catch
            {
                PlaybackMediaCache.DeleteForMediaUris(cache.MediaUris);
                throw;
            }
        }

        private void UpdateSelectedFileDetails()
        {
            var file = SelectedFile;
            SelectedFileDetails = file is null
                ? "Select a file."
                : $"{file.Name}\nFolder: {file.Folder}\nSize: {file.Size:N0} bytes\nDuration: {(file.Duration < 0 ? "unknown" : $"{file.Duration}s")}\nCreated: {file.CreatedAt}";
            Thumbnail = null;
            ThumbnailStatusText = file is null ? "Select a file to preview." : "Preview loading…";
        }

        private async Task ExecuteDeviceActionAsync(object? parameter)
        {
            if (parameter is not string action || string.IsNullOrWhiteSpace(action))
            {
                return;
            }

            if (action == "set-device-time")
            {
                var date = DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
                await _api.GetInfoAsync($"setsystime?date={date}");
                Log("Device time updated.");
                return;
            }
            var parts = action.Split("||", 2, StringSplitOptions.None);
            await _api.GetInfoAsync(parts[0]);
            Log(parts.Length > 1 ? parts[1] : $"Completed /app/{parts[0]}.");
            if (parts[0].StartsWith("setparamvalue?param=rec&value=", StringComparison.Ordinal))
            {
                await RefreshRecordingAsync();
            }
        }

        private async Task SetTimezoneAsync()
        {
            if (!int.TryParse(TimezoneOffset, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours))
            {
                throw new InvalidOperationException("Enter timezone offset in whole hours, such as -5 or 8.");
            }

            await _api.GetInfoAsync($"settimezone?timezone={hours.ToString(CultureInfo.InvariantCulture)}");
            Log($"Timezone set to UTC{hours:+#;-#;0}.");
        }

        private async Task SetWifiAsync()
        {
            var ssid = DeviceApi.QueryValue(WifiSsid);
            var password = DeviceApi.QueryValue(WifiPassword);
            await _api.GetInfoAsync($"setwifi?wifissid={ssid}&wifipwd={password}");
            Log("Wi-Fi credentials saved.");
            if (_wifiRebootRequired && ConfirmRequested is not null && await ConfirmRequested("Restart camera Wi-Fi", "The camera reports that Wi-Fi must restart to apply these credentials. Restart now?"))
            {
                await ExecuteDeviceActionAsync("wifireboot||Camera Wi-Fi restart requested.");
            }
        }

        private async Task FormatSdAsync()
        {
            if (ConfirmRequested is null || !await ConfirmRequested("Format SD card", "Formatting permanently erases recordings on the camera. Continue?"))
            {
                return;
            }

            await _api.GetInfoAsync("sdformat");
            Log("SD card format requested.");
        }

        private async Task FactoryResetAsync()
        {
            if (ConfirmRequested is null || !await ConfirmRequested("Factory reset", "This resets camera settings and may disconnect Wi-Fi. Continue?"))
            {
                return;
            }

            await _api.GetInfoAsync("reset");
            Log("Factory reset requested.");
            if (_wifiRebootRequired)
            {
                await ExecuteDeviceActionAsync("wifireboot||Camera Wi-Fi restart requested.");
            }
        }

        private async Task RunRawRequestAsync()
        {
            var endpoint = RawEndpoint.Trim().TrimStart('/');
            if (endpoint.StartsWith("app/", StringComparison.OrdinalIgnoreCase))
            {
                endpoint = endpoint[4..];
            }

            if (string.IsNullOrWhiteSpace(endpoint) || endpoint.Contains("..", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Enter an endpoint under /app/.");
            }

            var command = endpoint.Split('?', 2)[0].ToLowerInvariant();
            if (command is "reset" or "sdformat" or "deletefile")
            {
                if (ConfirmRequested is null || !await ConfirmRequested("Confirm device change", $"Run /app/{command}? This may permanently change or erase camera data."))
                {
                    return;
                }
            }
            var result = await _api.GetInfoAsync(endpoint);
            Log($"GET /app/{endpoint}: {result}");
        }

        private async Task LoadSettingsAsync()
        {
            Settings.Clear();
            var response = await _api.GetInfoAsync("getparamitems?param=all");
            if (response.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("The device did not return a settings list.");
            }

            foreach (var entry in response.EnumerateArray())
            {
                var name = TextValue(entry, "name", string.Empty);
                if (string.IsNullOrWhiteSpace(name) || !entry.TryGetProperty("items", out var itemArray) || !entry.TryGetProperty("index", out var indexArray))
                {
                    continue;
                }

                var labels = itemArray.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray();
                var indexes = indexArray.EnumerateArray().Select(x => x.GetInt32()).ToArray();
                if (labels.Length == 0 || labels.Length != indexes.Length)
                {
                    continue;
                }

                var current = await _api.GetInfoAsync($"getparamvalue?param={DeviceApi.QueryValue(name)}");
                var currentIndex = IntegerValue(current, "value", indexes[0]);
                var selected = Math.Max(0, Array.IndexOf(indexes, currentIndex));
                Settings.Add(new DeviceSettingItem(name, labels, indexes, selected, async selectedIndex =>
                {
                    await _api.GetInfoAsync($"setparamvalue?param={DeviceApi.QueryValue(name)}&value={indexes[selectedIndex].ToString(CultureInfo.InvariantCulture)}");
                    Log($"Updated {name}.");
                }, HandleCommandFailure));
            }

            Log($"Loaded {Settings.Count} device settings.");
        }

        private async Task CheckConnectionAsync()
        {
            if (!IsConnected || _connectionCheckRunning)
            {
                return;
            }

            _connectionCheckRunning = true;
            try { await _api.GetInfoAsync("getproductinfo"); }
            catch (DeviceApiException exception) when (exception.ResultCode is null)
            {
                MarkDisconnected("Connection lost");
                Log("Device connection lost. Navigation is disabled until reconnect.");
            }
            catch (Exception exception) { Log($"Connection check failed: {exception.Message}"); }
            finally { _connectionCheckRunning = false; }
        }

        public void Dispose()
        {
            FileExplorer.Dispose();
            CancelFileExplorerLoads();
            _connectionTimer.Stop();
            _recordingTimer.Stop();
            _liveHeartbeatTimer.Stop();
            _api.Dispose();
        }

        private async Task ToggleRecordingAsync()
        {
            await SetRecordingAsync(!IsRecording);
        }

        private async Task SetRecordingAsync(bool recording)
        {
            if (IsRecording == recording)
            {
                return;
            }

            var value = recording ? 1 : 0;
            await _api.GetInfoAsync($"setparamvalue?param=rec&value={value.ToString(CultureInfo.InvariantCulture)}");
            IsRecording = recording;
            RecordingSeconds = 0;
            if (recording)
            {
                _recordingTimer.Start();
            }
            else
            {
                _recordingTimer.Stop();
            }

            UpdateRecordingText();
            Log(recording ? "Recording started." : "Recording stopped.");
        }

        private async Task RefreshRecordingAsync()
        {
            var duration = await _api.GetInfoAsync("getrecduration");
            var seconds = IntegerValue(duration, "duration", -1);
            IsRecording = seconds >= 0;
            RecordingSeconds = Math.Max(0, seconds);
            if (IsRecording)
            {
                _recordingTimer.Start();
            }
            else
            {
                _recordingTimer.Stop();
            }

            UpdateRecordingText();
        }

        private async Task StartLiveAsync()
        {
            if (!IsConnected)
            {
                return;
            }

            var recorderEntered = false;
            try
            {
                await _api.GetInfoAsync("enterrecorder");
                recorderEntered = true;
                var mediaInfo = await _api.GetInfoAsync("getmediainfo");
                await _api.GetInfoAsync("setparamvalue?param=switchcam&value=0");

                var stream = GetLiveStreamUri(mediaInfo);
                LiveUrlText = "Opening RTSP stream…";
                if (LiveStarted?.Invoke(stream) != true)
                {
                    throw new InvalidOperationException("VLC could not start the RTSP stream. See Activity for details.");
                }

                IsLivePreviewActive = true;
                LiveUrlText = $"Connecting to {stream}…";
                Log($"RTSP playback requested: {stream}.");
            }
            catch
            {
                LiveStopped?.Invoke();
                if (recorderEntered)
                {
                    try { await _api.GetInfoAsync("exitrecorder"); }
                    catch (Exception exception) { Log($"Could not exit recorder mode after live start failed: {exception.Message}"); }
                }
                LiveUrlText = "Live view failed. See Activity for details.";
                throw;
            }
        }

        private async Task KeepLiveStreamAliveAsync()
        {
            if (!IsLivePreviewActive || !IsConnected || _liveHeartbeatRunning)
            {
                return;
            }

            _liveHeartbeatRunning = true;
            try
            {
                await _api.GetInfoAsync("getparamvalue?param=rec");
            }
            catch (Exception exception)
            {
                Log($"Live stream heartbeat failed: {exception.Message}");
            }
            finally
            {
                _liveHeartbeatRunning = false;
            }
        }

        private Uri GetLiveStreamUri(JsonElement mediaInfo)
        {
            var rtsp = PropertyString(mediaInfo, "rtsp");
            var port = IntegerValue(mediaInfo, "port", 554);
            if (!string.IsNullOrWhiteSpace(rtsp))
            {
                var candidate = rtsp.Contains("://", StringComparison.Ordinal) ? rtsp : $"rtsp://{rtsp}";
                if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme == "rtsp")
                {
                    return new UriBuilder(uri) { Port = uri.IsDefaultPort ? port : uri.Port }.Uri;
                }
            }

            return new Uri($"rtsp://{_api.Host}:{port}");
        }

        private static string? PropertyString(JsonElement element, string property)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var item in element.EnumerateObject())
            {
                if (string.Equals(item.Name, property, StringComparison.OrdinalIgnoreCase))
                {
                    return item.Value.ValueKind == JsonValueKind.String ? item.Value.GetString() : item.Value.ToString();
                }
            }

            return null;
        }

        public void ReportLivePlayback(string message, bool failed = false)
        {
            LiveUrlText = failed ? "VLC could not play the stream. See Activity for details." : message;
            Log(message);
        }

        public async Task StopLivePreviewAsync()
        {
            var wasActive = IsLivePreviewActive;
            LiveStopped?.Invoke();
            IsLivePreviewActive = false;
            _liveHeartbeatRunning = false;
            LiveUrlText = "RTSP preview starts the recorder, then refreshes media info.";
            if (!wasActive || !IsConnected)
            {
                return;
            }

            try
            {
                await _api.GetInfoAsync("exitrecorder");
                Log("Live preview stopped; recorder mode exited.");
            }
            catch (Exception exception)
            {
                Log($"Live preview stopped; exit recorder failed: {exception.Message}");
            }
        }

        private void UpdateRecordingText()
        {
            Recording = IsRecording ? $"Recording · {RecordingSeconds}s" : "Stopped";
            OnPropertyChanged(nameof(Recording));
        }

        private async Task RefreshDeviceAsync(CancellationToken cancellationToken)
        {
            var product = await _api.GetInfoAsync("getproductinfo", cancellationToken);
            var attributes = await _api.GetInfoAsync("getdeviceattr", cancellationToken);
            var storage = await _api.GetInfoAsync("getsdinfo", cancellationToken);
            var battery = await _api.GetInfoAsync("getbatteryinfo", cancellationToken);
            var duration = await _api.GetInfoAsync("getrecduration", cancellationToken);

            Model = TextValue(product, "model", "Unknown model");
            Firmware = TextValue(attributes, "softver", "Unknown firmware");
            var sdStatus = IntegerValue(storage, "status", -1);
            var free = Math.Max(0, LongValue(storage, "free", 0));
            var total = Math.Max(0, LongValue(storage, "total", 0));
            IsStorageAvailable = sdStatus == 0 && total > 0;
            if (IsStorageAvailable)
            {
                free = Math.Min(free, total);
                var used = total - free;
                Storage = $"{HumanizeMegabytes(used)} Used / {HumanizeMegabytes(free)} Free";
                StorageUsagePercent = used * 100d / total;
                StorageUsageBrush = Brush.Parse(StorageUsagePercent > 90 ? "#F04452" : StorageUsagePercent > 80 ? "#F59E0B" : "#43D17D");
            }
            else
            {
                Storage = StorageStatus(sdStatus);
                StorageUsagePercent = 0;
            }
            var seconds = IntegerValue(duration, "duration", -1);
            IsRecording = seconds >= 0;
            RecordingSeconds = Math.Max(0, seconds);
            if (IsRecording)
            {
                _recordingTimer.Start();
            }
            else
            {
                _recordingTimer.Stop();
            }

            UpdateRecordingText();
            Company = TextValue(product, "company", "Pelsee");
            Soc = TextValue(product, "soc", "—");
            Ssid = TextValue(attributes, "ssid", "—");
            WifiSsid = Ssid == "—" ? string.Empty : Ssid;
            Hardware = TextValue(attributes, "hwver", "—");
            DeviceId = TextValue(attributes, "uuid", "—");
            CameraCount = IntegerValue(attributes, "camnum", 0).ToString(CultureInfo.InvariantCulture);
            _wifiRebootRequired = IntegerValue(attributes, "wifireboot", 0) == 1;
            Battery = TextValue(battery, "capacity", "—");
            NotifyDeviceData();
            await LoadCameraActionsAsync(cancellationToken);
            Log($"Status refreshed. SD: {Storage}; cameras: {CameraCount}.");
        }

        private async Task LoadCameraActionsAsync(CancellationToken cancellationToken = default)
        {
            CameraActions.Clear();
            var settings = await _api.GetInfoAsync("getparamitems?param=all", cancellationToken);
            if (settings.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var camera = settings.EnumerateArray().FirstOrDefault(x => TextValue(x, "name", "") == "switchcam");
            if (camera.ValueKind != JsonValueKind.Object || !camera.TryGetProperty("items", out var items) || !camera.TryGetProperty("index", out var indexes))
            {
                return;
            }

            var labels = items.EnumerateArray().Select(x => x.GetString() ?? "Camera").ToArray();
            var values = indexes.EnumerateArray().Select(x => x.GetInt32()).ToArray();
            for (var i = 0; i < Math.Min(labels.Length, values.Length); i++)
            {
                var label = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(labels[i]);
                var value = values[i];
                var command = new AsyncCommand(async () =>
                {
                    await _api.GetInfoAsync($"setparamvalue?param=switchcam&value={value.ToString(CultureInfo.InvariantCulture)}");
                    Log($"Camera set to {labels[i]}.");
                }, () => IsConnected);
                command.Failed += HandleCommandFailure;
                CameraActions.Add(new CameraSwitchItem($"Switch to {label}", command));
            }
        }

        public void MarkDisconnected(string status)
        {
            CancelFileExplorerLoads();
            if (IsLivePreviewActive)
            {
                LiveStopped?.Invoke();
                IsLivePreviewActive = false;
                LiveUrlText = "RTSP preview starts the recorder, then refreshes media info.";
            }
            IsConnected = false;
            IsConnecting = false;
            ConnectionStatus = status;
            ClearDeviceData();
            NotifyConnectionPresentation();
        }

        private void ClearDeviceData()
        {
            Model = Firmware = Storage = Recording = Company = Soc = CameraCount = Battery = Ssid = DeviceId = Hardware = "—";
            WifiSsid = string.Empty;
            WifiPassword = string.Empty;
            IsRecording = false;
            RecordingSeconds = 0;
            _recordingTimer.Stop();
            IsStorageAvailable = false;
            StorageUsagePercent = 0;
            StorageUsageBrush = Brush.Parse("#43D17D");
            Files.Clear();
            _filesByFolder.Clear();
            foreach (var folder in FileFolders)
            {
                folder.FileCount = 0;
            }

            OnPropertyChanged(nameof(SdCardSummary));
            SelectedFile = null;
            Thumbnail = null;
            ThumbnailStatusText = "Select a file to preview.";
            FileCountText = "SDCard · no files";
            _wifiRebootRequired = false;
            NotifyDeviceData();
        }

        private async Task CopyValueAsync(object? parameter)
        {
            if (parameter is string value && !string.IsNullOrWhiteSpace(value) && value != "—")
            {
                if (CopyRequested is null)
                {
                    throw new InvalidOperationException("Clipboard is unavailable on this window.");
                }

                await CopyRequested(value);
                Log("Device data copied to clipboard.");
            }
        }

        private void NotifyDeviceData()
        {
            foreach (var property in new[] { nameof(Model), nameof(Firmware), nameof(Storage), nameof(Recording), nameof(Company), nameof(Soc), nameof(CameraCount), nameof(Battery), nameof(Ssid), nameof(DeviceId), nameof(Hardware) })
            {
                OnPropertyChanged(property);
            }

            ((AsyncParameterCommand)CopyValueCommand).RaiseCanExecuteChanged();
            foreach (var command in new[] { LoadFilesCommand, LoadThumbnailCommand, DownloadFileCommand, DeleteFileCommand })
            {
                ((AsyncCommand)command).RaiseCanExecuteChanged();
            }

            ((RelayCommand)SelectSdCardCommand).RaiseCanExecuteChanged();
            ((AsyncCommand)PlayFileCommand).RaiseCanExecuteChanged();
            foreach (var command in new[] { SetTimezoneCommand, SetWifiCommand, FormatSdCommand, FactoryResetCommand, RawRequestCommand })
            {
                ((AsyncCommand)command).RaiseCanExecuteChanged();
            }

            ((AsyncCommand)LoadSettingsCommand).RaiseCanExecuteChanged();
            ((AsyncCommand)StartLiveCommand).RaiseCanExecuteChanged();
            ((AsyncCommand)StopLiveCommand).RaiseCanExecuteChanged();
            foreach (var cameraAction in CameraActions)
            {
                ((AsyncCommand)cameraAction.Command).RaiseCanExecuteChanged();
            }

            ((AsyncCommand)ToggleRecordingCommand).RaiseCanExecuteChanged();
            ((AsyncCommand)StartRecordingCommand).RaiseCanExecuteChanged();
            ((AsyncCommand)StopRecordingCommand).RaiseCanExecuteChanged();
        }

        private void NotifyConnectionPresentation()
        {
            OnPropertyChanged(nameof(ConnectTooltip));
            OnPropertyChanged(nameof(ShowConnectIcon));
            OnPropertyChanged(nameof(ShowCancelIcon));
            OnPropertyChanged(nameof(ShowDisconnectIcon));
            OnPropertyChanged(nameof(IsConnectEnabled));
            OnPropertyChanged(nameof(IsHostEnabled));
            NotifyCommands();
        }

        private void NotifyCommands()
        {
            ((AsyncCommand)ConnectCommand).RaiseCanExecuteChanged();
            ((AsyncCommand)RefreshCommand).RaiseCanExecuteChanged();
            ((RelayCommand)NavigateCommand).RaiseCanExecuteChanged();
            ((AsyncParameterCommand)CopyValueCommand).RaiseCanExecuteChanged();
            ((AsyncParameterCommand)DeviceActionCommand).RaiseCanExecuteChanged();
            foreach (var command in new[]
                     {
                         LoadFilesCommand, LoadThumbnailCommand, DownloadFileCommand, DeleteFileCommand,
                         SetTimezoneCommand, SetWifiCommand, FormatSdCommand, FactoryResetCommand, RawRequestCommand,
                         LoadSettingsCommand, StartLiveCommand, StopLiveCommand, ToggleRecordingCommand,
                         StartRecordingCommand, StopRecordingCommand
                     })
            {
                ((AsyncCommand)command).RaiseCanExecuteChanged();
            }

            ((RelayCommand)SelectSdCardCommand).RaiseCanExecuteChanged();
            ((AsyncCommand)PlayFileCommand).RaiseCanExecuteChanged();
            foreach (var cameraAction in CameraActions)
            {
                ((AsyncCommand)cameraAction.Command).RaiseCanExecuteChanged();
            }

            OnPropertyChanged(nameof(IsConnectEnabled));
        }

        private static bool IsValidIpv4(string? value) => IPAddress.TryParse(value, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
        private static int IntegerValue(JsonElement element, string property, int fallback) => element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : fallback;
        private static long LongValue(JsonElement element, string property, long fallback) => element.TryGetProperty(property, out var value) && value.TryGetInt64(out var result) ? result : fallback;
        private static string HumanizeMegabytes(long value) => value.Megabytes().ToString();
        private static string TextValue(JsonElement element, string property, string fallback) => element.TryGetProperty(property, out var value) ? value.ToString() : fallback;
        private static string StorageStatus(int status) => status switch { 1 => "Needs format", 2 => "Not inserted", 3 => "Damaged", 10 => "Locked", 11 => "Slow card", 12 => "Format required", 13 => "Formatting", _ => $"Status {status}" };

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public sealed record CameraSwitchItem(string Label, ICommand Command);

    public sealed class FileFolderItem(string name) : INotifyPropertyChanged
    {
        private int _fileCount;
        private bool _isOpen;

        public event PropertyChangedEventHandler? PropertyChanged;
        public string Name { get; } = name;
        public int FileCount
        {
            get => _fileCount;
            set
            {
                if (_fileCount == value)
                {
                    return;
                }

                _fileCount = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileCount)));
            }
        }
        public bool IsOpen
        {
            get => _isOpen;
            set
            {
                if (_isOpen == value)
                {
                    return;
                }

                _isOpen = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOpen)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconPath)));
            }
        }
        public string IconPath => IsOpen ? "/Assets/Icons/folder_open.svg" : "/Assets/Icons/folder.svg";
    }

    public sealed class DeviceSettingItem : INotifyPropertyChanged
    {
        private readonly Func<int, Task> _apply;
        private readonly Action<Exception> _failed;
        private int _selectedIndex;

        public DeviceSettingItem(string name, string[] labels, int[] indexes, int selectedIndex, Func<int, Task> apply, Action<Exception> failed)
        {
            Name = name;
            Labels = labels;
            Indexes = indexes;
            _selectedIndex = selectedIndex;
            _apply = apply;
            _failed = failed;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public event Action? Finished;
        public string Name { get; }
        public string[] Labels { get; }
        public int[] Indexes { get; }
        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                if (_selectedIndex == value || value < 0 || value >= Indexes.Length)
                {
                    return;
                }

                _selectedIndex = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedIndex)));
                _ = ApplySelectionAsync(value);
            }
        }

        private async Task ApplySelectionAsync(int index)
        {
            try
            {
                await _apply(index);
            }
            catch (Exception exception) { _failed(exception); }
            finally { Finished?.Invoke(); }
        }
    }

    public sealed class RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
        public void Execute(object? parameter) => execute(parameter);
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    public sealed class AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null, bool allowWhileRunning = false) : ICommand
    {
        private bool _isRunning;
        public bool IsRunning => _isRunning;
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => (allowWhileRunning || !_isRunning) && (canExecute?.Invoke() ?? true);
        public async void Execute(object? parameter)
        {
            if (_isRunning && !allowWhileRunning || !CanExecute(parameter))
            {
                return;
            }

            if (_isRunning && allowWhileRunning)
            {
                try { await execute(); }
                finally { Finished?.Invoke(); }
                return;
            }
            _isRunning = true;
            RaiseCanExecuteChanged();
            try
            {
                await execute();
            }
            catch (Exception exception) { Failed?.Invoke(exception); }
            finally
            {
                _isRunning = false;
                RaiseCanExecuteChanged();
                Finished?.Invoke();
            }
        }
        public event Action<Exception>? Failed;
        public event Action? Finished;
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    public sealed class AsyncParameterCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null) : ICommand
    {
        private bool _isRunning;
        public event EventHandler? CanExecuteChanged;
        public event Action<Exception>? Failed;
        public bool CanExecute(object? parameter) => !_isRunning && (canExecute?.Invoke(parameter) ?? true);
        public async void Execute(object? parameter)
        {
            if (!CanExecute(parameter))
            {
                return;
            }

            _isRunning = true;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            try
            {
                await execute(parameter);
            }
            catch (Exception exception) { Failed?.Invoke(exception); }
            finally
            {
                _isRunning = false;
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
                Finished?.Invoke();
            }
        }
        public event Action? Finished;
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
