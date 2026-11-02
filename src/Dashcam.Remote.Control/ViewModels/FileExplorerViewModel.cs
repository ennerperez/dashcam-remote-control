using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Dashcam.Remote
{
    public sealed class FileExplorerViewModel : INotifyPropertyChanged, IDisposable
    {
        private const int PageSize = 100;
        private readonly DeviceApi _api;
        private readonly Dictionary<string, List<DeviceFile>> _filesByFolder = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<DeviceFile> _manifestFiles = [];
        private readonly List<Bitmap> _thumbnails = [];
        private readonly Dictionary<string, DownloadQueueItem> _activeDownloads = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _downloadSlots = new(2, 2);
        private CancellationTokenSource? _fileLoadCancellation;
        private CancellationTokenSource? _thumbnailLoadCancellation;
        private string _selectedFolder = "SDCard";
        private string _fileCountText = "No files";
        private string _selectedFileDetails = "Select a file.";
        private string _selectedDetailsHeader = "File details";
        private string _thumbnailStatusText = "Select a file to preview.";
        private string _searchText = string.Empty;
        private string _searchStatus = string.Empty;
        private string _downloadButtonText = "Download";
        private string _downloadButtonTooltip = "Download selected files";
        private bool _isConnected;
        private bool _isFilesLoading;
        private bool _isThumbnailLoading;
        private bool _isFrontBackView;
        private int _thumbnailIndex;
        private string? _loadedManifestHost;
        private string? _downloadDirectory;

        public FileExplorerViewModel(DeviceApi api)
        {
            _api = api;
            foreach (var name in new[] { "loop", "emr", "event", "park" })
            {
                var folder = new StorageFolderItem(name, DownloadFolderAsync, CancelFolderDownloads, FolderHasActiveDownloads);
                ((AsyncCommand)folder.DownloadAllCommand).Failed += exception => CommandFailed?.Invoke(exception);
                FileFolders.Add(folder);
            }

            EnterCommand = new AsyncCommand(EnterAsync, () => IsConnected);
            RefreshCommand = new AsyncCommand(RefreshFromDeviceAsync, () => IsConnected);
            SelectSdCardCommand = new RelayCommand(_ => SelectedFolder = "SDCard", _ => IsConnected);
            DownloadOrCancelSelectedCommand = new AsyncCommand(DownloadOrCancelSelectedAsync, () => IsConnected && SelectedEntries.Count > 0);
            CancelSelectedDownloadsCommand = new RelayCommand(_ => CancelSelectedDownloads(), _ => HasSelectedActiveDownloads);
            DeleteSelectedCommand = new AsyncCommand(DeleteSelectedAsync, () => IsConnected && SelectedEntries.Count > 0);
            PlaySelectedCommand = new AsyncCommand(PlaySelectedAsync, () => IsConnected && SelectedEntries.Count == 1);
            SearchCommand = new RelayCommand(_ => SearchManifest(), _ => IsConnected && !string.IsNullOrWhiteSpace(SearchText));
            ToggleFrontBackCommand = new RelayCommand(_ => IsFrontBackView = !IsFrontBackView, _ => IsConnected);
            PreviousThumbnailCommand = new RelayCommand(_ => SetThumbnailIndex(ThumbnailIndex - 1), _ => ThumbnailCount > 1);
            NextThumbnailCommand = new RelayCommand(_ => SetThumbnailIndex(ThumbnailIndex + 1), _ => ThumbnailCount > 1);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public ObservableCollection<ExplorerFileItem> Files { get; } = [];
        public ObservableCollection<ExplorerFileItem> SelectedEntries { get; } = [];
        public ObservableCollection<StorageFolderItem> FileFolders { get; } = [];
        public ObservableCollection<DownloadQueueItem> DownloadQueue { get; } = [];
        public ICommand EnterCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand SelectSdCardCommand { get; }
        public ICommand DownloadOrCancelSelectedCommand { get; }
        public ICommand CancelSelectedDownloadsCommand { get; }
        public ICommand DeleteSelectedCommand { get; }
        public ICommand PlaySelectedCommand { get; }
        public ICommand SearchCommand { get; }
        public ICommand ToggleFrontBackCommand { get; }
        public ICommand PreviousThumbnailCommand { get; }
        public ICommand NextThumbnailCommand { get; }
        public Func<Task<string?>>? ChooseDownloadDirectoryRequested { get; set; }
        public Func<string, string, Task<bool>>? ConfirmRequested { get; set; }
        public Action<IReadOnlyList<Uri>>? PlayVideosRequested { get; set; }
        public Action<IReadOnlyList<PreviewImageItem>>? PreviewImagesRequested { get; set; }
        public Action<Exception>? CommandFailed { get; set; }
        public Action<string>? Log { get; set; }
        public Action<ExplorerFileItem>? SelectionRequested { get; set; }
        public IEnumerable<ICommand> AsyncCommands => [EnterCommand, RefreshCommand, DownloadOrCancelSelectedCommand, DeleteSelectedCommand, PlaySelectedCommand];

        public bool IsConnected
        {
            get => _isConnected;
            set
            {
                if (!Set(ref _isConnected, value))
                {
                    return;
                }

                if (!value)
                {
                    CancelViewLoads();
                    foreach (var download in _activeDownloads.Values.ToArray())
                    {
                        download.Cancellation.Cancel();
                    }

                    _manifestFiles.Clear();
                    _filesByFolder.Clear();
                    _loadedManifestHost = null;
                    Files.Clear();
                    SelectedEntries.Clear();
                    RebuildFolderCounts();
                    IsFilesLoading = false;
                    IsThumbnailLoading = false;
                    SelectedFileDetails = "Connect to view files.";
                    FileCountText = "No files";
                    OnPropertyChanged(nameof(SdCardSummary));
                    UpdateSelection();
                }
                RaiseCommandStates();
            }
        }
        public bool IsFilesLoading { get => _isFilesLoading; private set => Set(ref _isFilesLoading, value); }
        public bool IsThumbnailLoading { get => _isThumbnailLoading; private set => Set(ref _isThumbnailLoading, value); }
        public string FileCountText { get => _fileCountText; private set => Set(ref _fileCountText, value); }
        public string SelectedFileDetails { get => _selectedFileDetails; private set => Set(ref _selectedFileDetails, value); }
        public string SelectedDetailsHeader { get => _selectedDetailsHeader; private set => Set(ref _selectedDetailsHeader, value); }
        public string ThumbnailStatusText { get => _thumbnailStatusText; private set => Set(ref _thumbnailStatusText, value); }
        public string SearchText
        {
            get => _searchText; set
            {
                if (Set(ref _searchText, value))
                {
                    ((RelayCommand)SearchCommand).RaiseCanExecuteChanged();
                }
            }
        }
        public string SearchStatus { get => _searchStatus; private set => Set(ref _searchStatus, value); }
        public string DownloadButtonText { get => _downloadButtonText; private set => Set(ref _downloadButtonText, value); }
        public string DownloadButtonTooltip { get => _downloadButtonTooltip; private set => Set(ref _downloadButtonTooltip, value); }
        public string DownloadButtonIconPath => HasSelectedActiveDownloads ? "/Assets/Icons/cancel.svg" : "/Assets/Icons/download.svg";
        public bool IsFrontBackView
        {
            get => _isFrontBackView; set
            {
                if (Set(ref _isFrontBackView, value))
                {
                    RebuildEntries();
                }
            }
        }
        public string SdCardSummary => $"{_manifestFiles.Count.ToString("N0", CultureInfo.CurrentCulture)} files";
        public bool IsSdCardSelected => string.Equals(SelectedFolder, "SDCard", StringComparison.OrdinalIgnoreCase);
        public string SelectedFolderTitle => IsSdCardSelected ? "All files" : SelectedFolder;
        public string SdCardIconPath => "/Assets/Icons/sd_card.svg";
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
                OnPropertyChanged(nameof(SelectedFolderTitle));
                OnPropertyChanged(nameof(SelectedFolderItem));
                foreach (var folder in FileFolders)
                {
                    folder.IsOpen = string.Equals(folder.Name, value, StringComparison.OrdinalIgnoreCase);
                }

                RebuildEntries();
            }
        }
        public StorageFolderItem? SelectedFolderItem
        {
            get => FileFolders.FirstOrDefault(folder => string.Equals(folder.Name, SelectedFolder, StringComparison.OrdinalIgnoreCase));
            set => SelectedFolder = value?.Name ?? "SDCard";
        }
        public bool HasSelection => SelectedEntries.Count > 0;
        public bool CanPreviewOrPlay => SelectedEntries.Count == 1;
        public bool HasSelectedActiveDownloads => SelectedEntries.SelectMany(entry => entry.Files).Any(IsFileDownloading);
        public string PlayButtonLabel => SelectedEntries.Count == 1 && SelectedEntries[0].IsPhoto ? "Preview" : "Play";
        public Bitmap? Thumbnail => _thumbnails.Count == 0 ? null : _thumbnails[Math.Clamp(ThumbnailIndex, 0, _thumbnails.Count - 1)];
        public string SelectedFilePathText => SelectedEntries.Count == 1 && SelectedEntries[0].Files.Count > 0
            ? SelectedEntries[0].Files[Math.Clamp(ThumbnailIndex, 0, SelectedEntries[0].Files.Count - 1)].Name
            : string.Empty;
        public int ThumbnailIndex
        {
            get => _thumbnailIndex; private set
            {
                if (!Set(ref _thumbnailIndex, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(Thumbnail)); OnPropertyChanged(nameof(ThumbnailIndicatorText)); OnPropertyChanged(nameof(SelectedFilePathText));
            }
        }
        public int ThumbnailCount => _thumbnails.Count;
        public string ThumbnailIndicatorText => ThumbnailCount == 0 ? string.Empty : $"{ThumbnailIndex + 1} / {ThumbnailCount}";
        public bool HasMultipleThumbnails => ThumbnailCount > 1;

        public void OnEntered()
        {
            if (IsConnected)
            {
                ((AsyncCommand)EnterCommand).Execute(null);
            }
        }

        public void OnLeaving() => CancelViewLoads();

        public void SetSelectedEntries(IEnumerable<ExplorerFileItem> entries)
        {
            var selected = entries.Where(Files.Contains).Distinct().ToArray();
            if (SelectedEntries.SequenceEqual(selected))
            {
                return;
            }

            SelectedEntries.Clear();
            foreach (var entry in selected)
            {
                SelectedEntries.Add(entry);
            }

            UpdateSelection();
        }

        public void Dispose()
        {
            CancelViewLoads();
            foreach (var download in _activeDownloads.Values)
            {
                download.Cancellation.Cancel();
            }

            foreach (var thumbnail in _thumbnails)
            {
                thumbnail.Dispose();
            }
        }

        private async Task EnterAsync()
        {
            var host = _api.Host;
            if (string.Equals(_loadedManifestHost, host, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (await TryLoadManifestAsync(host))
            {
                return;
            }

            await RefreshFromDeviceAsync();
        }

        private async Task RefreshFromDeviceAsync()
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
                var loaded = new List<DeviceFile>();
                foreach (var folderName in new[] { "loop", "emr", "event", "park" })
                {
                    for (var start = 0; ; start += 100)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        var end = start + PageSize - 1;
                        var response = await _api.GetInfoAsync($"getfilelist?folder={DeviceApi.QueryValue(folderName)}&start={start}&end={end}", cancellation.Token);
                        if (response.ValueKind != JsonValueKind.Array)
                        {
                            throw new InvalidOperationException($"The device returned an unexpected file list for '{folderName}'.");
                        }

                        var count = 0;
                        foreach (var folderEntry in response.EnumerateArray())
                        {
                            if (!folderEntry.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                            {
                                continue;
                            }

                            foreach (var file in files.EnumerateArray())
                            {
                                loaded.Add(new DeviceFile(folderName, TextValue(file, "name", string.Empty), LongValue(file, "size", 0), IntegerValue(file, "duration", -1), TextValue(file, "createtime", TextValue(file, "createtimestr", string.Empty)), IntegerValue(file, "type", -1)));
                                count++;
                            }
                        }
                        if (count < PageSize)
                        {
                            break;
                        }
                    }
                }

                _manifestFiles.Clear();
                _manifestFiles.AddRange(loaded);
                _loadedManifestHost = _api.Host;
                RebuildFolderCounts();
                RebuildEntries();
                OnPropertyChanged(nameof(SdCardSummary));
                await SaveManifestAsync(cancellation.Token);
                Log?.Invoke($"Loaded {_manifestFiles.Count.ToString("N0", CultureInfo.CurrentCulture)} SD files.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            finally
            {
                if (ReferenceEquals(_fileLoadCancellation, cancellation))
                {
                    _fileLoadCancellation = null;
                    IsFilesLoading = false;
                }
            }
        }

        private async Task<bool> TryLoadManifestAsync(string host)
        {
            try
            {
                var path = GetManifestPath(host);
                if (!File.Exists(path))
                {
                    return false;
                }

                await using var input = File.OpenRead(path);
                var manifest = await JsonSerializer.DeserializeAsync<FileManifest>(input);
                if (manifest is null || !string.Equals(manifest.Host, host, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                _manifestFiles.Clear();
                _manifestFiles.AddRange(manifest.Files);
                _loadedManifestHost = host;
                RebuildFolderCounts();
                RebuildEntries();
                OnPropertyChanged(nameof(SdCardSummary));
                FileCountText = $"Loaded cache · {_manifestFiles.Count.ToString("N0", CultureInfo.CurrentCulture)} files";
                Log?.Invoke("Loaded the temporary SD card manifest.");
                return true;
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                Log?.Invoke($"Could not read the SD manifest cache: {exception.Message}");
                return false;
            }
        }

        private async Task SaveManifestAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var path = GetManifestPath(_api.Host);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporaryPath = $"{path}.tmp";
                await using (var output = File.Create(temporaryPath))
                {
                    await JsonSerializer.SerializeAsync(output, new FileManifest(_api.Host, DateTimeOffset.Now, _manifestFiles), cancellationToken: cancellationToken);
                }

                File.Move(temporaryPath, path, true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                if (exception is not OperationCanceledException)
                {
                    Log?.Invoke($"Could not save the temporary SD manifest: {exception.Message}");
                }
            }
        }

        private static string GetManifestPath(string host)
        {
            var safeHost = string.Concat(host.Select(character => char.IsLetterOrDigit(character) ? character : '_'));
            return Path.Combine(Path.GetTempPath(), "DashcamRemoteControl", "file-manifests", $"{safeHost}.json");
        }

        private void RebuildFolderCounts()
        {
            _filesByFolder.Clear();
            foreach (var file in _manifestFiles)
            {
                if (!_filesByFolder.TryGetValue(file.Folder, out var list))
                {
                    _filesByFolder[file.Folder] = list = [];
                }

                list.Add(file);
            }
            foreach (var folder in FileFolders)
            {
                folder.FileCount = _filesByFolder.GetValueOrDefault(folder.Name)?.Count ?? 0;
            }
        }

        private void RebuildEntries()
        {
            var rawFiles = IsSdCardSelected
                ? _manifestFiles
                : _filesByFolder.GetValueOrDefault(SelectedFolder) ?? [];
            var next = IsFrontBackView ? BuildGroupedEntries(rawFiles) : rawFiles.Select(CreateEntry).ToList();
            Files.Clear();
            foreach (var entry in next.OrderByDescending(entry => entry.SortDate, StringComparer.Ordinal))
            {
                Files.Add(entry);
            }

            SetSelectedEntries([]);
            FileCountText = IsFilesLoading ? "Loading SD card…" : $"{Files.Count.ToString("N0", CultureInfo.CurrentCulture)} items";
            foreach (var entry in Files)
            {
                entry.RefreshDownloadState();
            }

            foreach (var folder in FileFolders)
            {
                folder.RefreshDownloadState();
            }

            OnPropertyChanged(nameof(SdCardSummary));
        }

        private List<ExplorerFileItem> BuildGroupedEntries(IReadOnlyList<DeviceFile> files)
        {
            var groups = new Dictionary<string, List<(DeviceFile File, CameraSide Side)>>(StringComparer.OrdinalIgnoreCase);
            var singles = new List<ExplorerFileItem>();
            foreach (var file in files)
            {
                var side = ExplorerFileItem.CameraMarker($"{file.Folder}/{file.Name}");
                if (!side.IsFront && !side.IsBack)
                {
                    singles.Add(CreateEntry(file));
                    continue;
                }
                var key = ExplorerFileItem.PairKey(file);
                if (!groups.TryGetValue(key, out var pair))
                {
                    groups[key] = pair = [];
                }

                pair.Add((file, side));
            }

            foreach (var pair in groups.Values)
            {
                var fronts = new Queue<DeviceFile>(pair.Where(item => item.Side.IsFront).Select(item => item.File));
                var backs = new Queue<DeviceFile>(pair.Where(item => item.Side.IsBack).Select(item => item.File));
                while (fronts.Count > 0 && backs.Count > 0)
                {
                    var front = fronts.Dequeue();
                    var back = backs.Dequeue();
                    var primary = ExplorerFileItem.RemoveCameraMarkers(front.Name);
                    singles.Add(CreateEntry([front, back], true, primary));
                }
                while (fronts.Count > 0)
                {
                    singles.Add(CreateEntry(fronts.Dequeue()));
                }

                while (backs.Count > 0)
                {
                    singles.Add(CreateEntry(backs.Dequeue()));
                }
            }
            return singles;
        }

        private ExplorerFileItem CreateEntry(DeviceFile file) => CreateEntry(
            [file],
            name: IsFrontBackView ? ExplorerFileItem.RemoveCameraMarkers($"{file.Folder}/{file.Name}") : Path.GetFileName(file.Name));

        private ExplorerFileItem CreateEntry(IReadOnlyList<DeviceFile> files, bool grouped = false, string? name = null)
        {
            var item = new ExplorerFileItem(files, grouped, name ?? Path.GetFileName(files[0].Name), ContextDownloadAsync, ContextDeleteAsync, ContextPreviewAsync, CancelContextDownloads, CanCancelContextDownloads, RefreshEntryDownloadState);
            ((AsyncCommand)item.DownloadCommand).Failed += exception => CommandFailed?.Invoke(exception);
            ((AsyncCommand)item.DeleteCommand).Failed += exception => CommandFailed?.Invoke(exception);
            ((AsyncCommand)item.PreviewCommand).Failed += exception => CommandFailed?.Invoke(exception);
            return item;
        }

        private void UpdateSelection()
        {
            CancelThumbnails();
            DisposeThumbnails();
            ThumbnailIndex = 0;
            IsThumbnailLoading = false;
            if (SelectedEntries.Count == 1)
            {
                UpdateDetails(SelectedEntries[0]);
                _ = LoadThumbnailsAsync(SelectedEntries[0]);
            }
            else
            {
                SelectedDetailsHeader = "File details";
                SelectedFileDetails = SelectedEntries.Count == 0 ? "Select a file." : $"{SelectedEntries.Count} files selected.";
                ThumbnailStatusText = SelectedEntries.Count == 0 ? "Select a file to preview." : "Preview is available for one selected item.";
            }
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(CanPreviewOrPlay));
            OnPropertyChanged(nameof(PlayButtonLabel));
            OnPropertyChanged(nameof(Thumbnail));
            OnPropertyChanged(nameof(ThumbnailCount));
            OnPropertyChanged(nameof(ThumbnailIndicatorText));
            OnPropertyChanged(nameof(HasMultipleThumbnails));
            OnPropertyChanged(nameof(SelectedFilePathText));
            OnPropertyChanged(nameof(HasSelectedActiveDownloads));
            OnPropertyChanged(nameof(DownloadButtonIconPath));
            foreach (var entry in Files)
            {
                entry.RefreshDownloadState();
            }

            DownloadButtonText = HasSelectedActiveDownloads ? "Cancel" : "Download";
            DownloadButtonTooltip = HasSelectedActiveDownloads ? "Cancel selected downloads" : "Queue selected files for download";
            RaiseCommandStates();
        }

        private void UpdateDetails(ExplorerFileItem entry)
        {
            SelectedDetailsHeader = entry.IsGrouped ? "Files details" : "File details";
            var fields = new List<string>
            {
                entry.DisplayName,
                $"Files: {entry.Files.Count}",
                $"Size: {entry.SizeText}",
                $"Created: {entry.CreatedAtText}"
            };
            if (!string.IsNullOrWhiteSpace(entry.CameraLabel))
            {
                fields.Add($"Camera: {entry.CameraLabel}");
            }

            if (entry.HasDuration)
            {
                fields.Add($"Duration: {entry.DurationText}");
            }

            SelectedFileDetails = string.Join(Environment.NewLine, fields);
            ThumbnailStatusText = "Loading preview…";
        }

        private async Task LoadThumbnailsAsync(ExplorerFileItem entry)
        {
            if (!IsConnected || SelectedEntries.Count != 1)
            {
                return;
            }

            _thumbnailLoadCancellation?.Cancel();
            using var cancellation = new CancellationTokenSource();
            _thumbnailLoadCancellation = cancellation;
            IsThumbnailLoading = true;
            ThumbnailStatusText = "Loading preview…";
            try
            {
                foreach (var file in entry.Files)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    var path = DeviceApi.EscapeFilePath(file.Name, true);
                    var bytes = await _api.GetBytesAsync($"app/getthumbnail?file={path}", cancellation.Token);
                    await using var stream = new MemoryStream(bytes);
                    var bitmap = new Bitmap(stream);
                    if (cancellation.IsCancellationRequested || !SelectedEntries.Contains(entry))
                    {
                        bitmap.Dispose();
                        return;
                    }
                    _thumbnails.Add(bitmap);
                }
                ThumbnailStatusText = _thumbnails.Count > 1 ? "Use arrows to view each camera." : "Preview loaded.";
                OnPropertyChanged(nameof(Thumbnail));
                OnPropertyChanged(nameof(ThumbnailCount));
                OnPropertyChanged(nameof(ThumbnailIndicatorText));
                OnPropertyChanged(nameof(HasMultipleThumbnails));
                OnPropertyChanged(nameof(SelectedFilePathText));
                ((RelayCommand)PreviousThumbnailCommand).RaiseCanExecuteChanged();
                ((RelayCommand)NextThumbnailCommand).RaiseCanExecuteChanged();
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                ThumbnailStatusText = "Preview unavailable.";
                Log?.Invoke($"Could not load preview for {entry.DisplayName}: {exception.Message}");
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

        private void DisposeThumbnails()
        {
            foreach (var thumbnail in _thumbnails)
            {
                thumbnail.Dispose();
            }

            _thumbnails.Clear();
            OnPropertyChanged(nameof(Thumbnail));
            OnPropertyChanged(nameof(ThumbnailCount));
            OnPropertyChanged(nameof(ThumbnailIndicatorText));
            OnPropertyChanged(nameof(HasMultipleThumbnails));
            OnPropertyChanged(nameof(SelectedFilePathText));
            ((RelayCommand)PreviousThumbnailCommand).RaiseCanExecuteChanged();
            ((RelayCommand)NextThumbnailCommand).RaiseCanExecuteChanged();
        }

        private void CancelThumbnails() => _thumbnailLoadCancellation?.Cancel();

        private void SetThumbnailIndex(int index)
        {
            if (_thumbnails.Count == 0)
            {
                return;
            }

            ThumbnailIndex = (index + _thumbnails.Count) % _thumbnails.Count;
            ((RelayCommand)PreviousThumbnailCommand).RaiseCanExecuteChanged();
            ((RelayCommand)NextThumbnailCommand).RaiseCanExecuteChanged();
        }

        private async Task PreviewSelectedAsync()
        {
            if (SelectedEntries.Count != 1)
            {
                return;
            }

            await PreviewEntryAsync(SelectedEntries[0]);
        }

        private async Task PreviewEntryAsync(ExplorerFileItem entry)
        {
            if (PreviewImagesRequested is null)
            {
                return;
            }

            var images = new List<PreviewImageItem>();
            try
            {
                foreach (var file in entry.Files)
                {
                    var path = DeviceApi.EscapeFilePath(file.Name, false);
                    var bytes = await _api.GetBytesAsync(path);
                    await using var stream = new MemoryStream(bytes);
                    images.Add(new PreviewImageItem(Path.GetFileName(file.Name), new Bitmap(stream)));
                }
                PreviewImagesRequested(images);
            }
            catch
            {
                foreach (var image in images)
                {
                    image.Image.Dispose();
                }

                throw;
            }
        }

        private async Task PlaySelectedAsync()
        {
            if (SelectedEntries.Count != 1)
            {
                return;
            }

            var entry = SelectedEntries[0];
            if (entry.IsPhoto)
            {
                await PreviewEntryAsync(entry);
                return;
            }
            if (PlayVideosRequested is null)
            {
                return;
            }

            PlaybackMediaCacheResult? cache = null;
            try
            {
                cache = await PlaybackMediaCache.DownloadAsync(_api, entry.Files, (message, progress) =>
                    Dispatcher.UIThread.Post(() =>
                    {
                        UpdatePlaybackDownloadVisuals(entry, true, progress, message);
                        Log?.Invoke(message);
                    }));
                PlayVideosRequested(cache.MediaUris);
            }
            catch
            {
                if (cache is not null)
                {
                    PlaybackMediaCache.DeleteForMediaUris(cache.MediaUris);
                }

                throw;
            }
            finally
            {
                Dispatcher.UIThread.Post(() => UpdatePlaybackDownloadVisuals(entry, false, 0, string.Empty));
            }
        }

        private void UpdatePlaybackDownloadVisuals(ExplorerFileItem entry, bool preparing, double progress, string status)
        {
            if (entry.Files.Any(IsFileDownloading))
            {
                entry.RefreshDownloadState();
                return;
            }
            entry.SetDownloadState(preparing, progress, status);
        }

        private Task SearchManifest()
        {
            var query = SearchText.Trim();
            if (query.Length == 0)
            {
                return Task.CompletedTask;
            }

            var currentFiles = IsSdCardSelected ? _manifestFiles : _filesByFolder.GetValueOrDefault(SelectedFolder) ?? [];
            var matchingFile = currentFiles.FirstOrDefault(file => Path.GetFileName(file.Name).Contains(query, StringComparison.OrdinalIgnoreCase));
            if (matchingFile is null)
            {
                SearchStatus = "No match in this location.";
                return Task.CompletedTask;
            }
            var entry = Files.FirstOrDefault(candidate => candidate.Files.Contains(matchingFile));
            if (entry is null)
            {
                SearchStatus = "No visible match in this location.";
                return Task.CompletedTask;
            }
            SearchStatus = $"Selected {entry.DisplayName}.";
            SelectionRequested?.Invoke(entry);
            SetSelectedEntries([entry]);
            return Task.CompletedTask;
        }

        private async Task DownloadOrCancelSelectedAsync()
        {
            if (HasSelectedActiveDownloads)
            {
                CancelSelectedDownloads();
                return;
            }
            await EnqueueEntriesAsync(SelectedEntries.ToArray());
        }

        private Task ContextDownloadAsync(ExplorerFileItem clicked) => EnqueueEntriesAsync(GetContextSelection(clicked));

        private Task ContextDeleteAsync(ExplorerFileItem clicked) => DeleteEntriesAsync(GetContextSelection(clicked));

        private Task ContextPreviewAsync(ExplorerFileItem clicked) => clicked.IsPhoto ? PreviewEntryAsync(clicked) : Task.CompletedTask;

        private ExplorerFileItem[] GetContextSelection(ExplorerFileItem clicked) => SelectedEntries.Contains(clicked) ? SelectedEntries.ToArray() : [clicked];

        private bool CanCancelContextDownloads(ExplorerFileItem clicked) =>
            GetContextSelection(clicked).SelectMany(entry => entry.Files).Any(IsFileDownloading);

        private async Task EnqueueEntriesAsync(IEnumerable<ExplorerFileItem> entries)
        {
            var files = entries.SelectMany(entry => entry.Files).DistinctBy(GetFileKey).Where(file => !IsFileDownloading(file)).ToArray();
            if (files.Length == 0 || !IsConnected)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_downloadDirectory))
            {
                if (ChooseDownloadDirectoryRequested is null)
                {
                    throw new InvalidOperationException("Choose a download folder first.");
                }

                _downloadDirectory = await ChooseDownloadDirectoryRequested();
                if (string.IsNullOrWhiteSpace(_downloadDirectory))
                {
                    return;
                }
            }

            foreach (var file in files)
            {
                var task = new DownloadQueueItem(file);
                DownloadQueue.Add(task);
                _activeDownloads[GetFileKey(file)] = task;
                _ = RunDownloadAsync(task);
            }
            RefreshAllDownloadVisuals();
            Log?.Invoke($"Queued {files.Length.ToString("N0", CultureInfo.CurrentCulture)} file(s) for download.");
        }

        private async Task DownloadFolderAsync(string folderName)
        {
            var files = _filesByFolder.GetValueOrDefault(folderName) ?? [];
            var entries = files.Select(CreateEntry).ToArray();
            await EnqueueEntriesAsync(entries);
        }

        private void CancelSelectedDownloads()
        {
            foreach (var file in SelectedEntries.SelectMany(entry => entry.Files).DistinctBy(GetFileKey))
            {
                CancelDownload(file);
            }
        }

        private void CancelFolderDownloads(string folderName)
        {
            foreach (var task in _activeDownloads.Values.Where(task => string.Equals(task.File.Folder, folderName, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                CancelDownload(task.File);
            }
        }

        private void CancelContextDownloads(ExplorerFileItem entry)
        {
            foreach (var file in GetContextSelection(entry).SelectMany(item => item.Files).DistinctBy(GetFileKey))
            {
                CancelDownload(file);
            }
        }

        private void CancelDownload(DeviceFile file)
        {
            if (!_activeDownloads.TryGetValue(GetFileKey(file), out var task))
            {
                return;
            }

            task.State = DownloadState.Cancelled;
            task.Cancellation.Cancel();
            RefreshAllDownloadVisuals();
        }

        private bool IsFileDownloading(DeviceFile file) => _activeDownloads.ContainsKey(GetFileKey(file));

        private bool FolderHasActiveDownloads(string folderName) => _activeDownloads.Values.Any(task =>
            string.Equals(task.File.Folder, folderName, StringComparison.OrdinalIgnoreCase));

        private async Task RunDownloadAsync(DownloadQueueItem task)
        {
            var slotTaken = false;
            string? outputPath = null;
            string? partialPath = null;
            try
            {
                await _downloadSlots.WaitAsync(task.Cancellation.Token);
                slotTaken = true;
                if (task.Cancellation.IsCancellationRequested)
                {
                    return;
                }

                task.State = DownloadState.Downloading;
                RefreshAllDownloadVisuals();
                var relative = DeviceApi.EscapeFilePath(task.File.Name, false);
                var subfolder = Path.GetFileName(task.File.Folder);
                var outputDirectory = Path.Combine(_downloadDirectory!, subfolder);
                Directory.CreateDirectory(outputDirectory);
                outputPath = Path.Combine(outputDirectory, Path.GetFileName(task.File.Name));
                partialPath = $"{outputPath}.partial";
                await using (var output = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true))
                {
                    await _api.DownloadToAsync(relative, output, (read, total) =>
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            task.BytesTransferred = read;
                            task.TotalBytes = total;
                            RefreshAllDownloadVisuals();
                        });
                    }, task.Cancellation.Token);
                }
                File.Move(partialPath, outputPath, true);
                task.State = DownloadState.Completed;
            }
            catch (OperationCanceledException) when (task.Cancellation.IsCancellationRequested)
            {
                task.State = DownloadState.Cancelled;
                if (partialPath is not null && File.Exists(partialPath))
                {
                    File.Delete(partialPath);
                }
            }
            catch (Exception exception)
            {
                task.State = DownloadState.Failed;
                if (partialPath is not null && File.Exists(partialPath))
                {
                    File.Delete(partialPath);
                }

                Log?.Invoke($"Download failed for {Path.GetFileName(task.File.Name)}: {exception.Message}");
            }
            finally
            {
                if (slotTaken)
                {
                    _downloadSlots.Release();
                }

                Dispatcher.UIThread.Post(() =>
                {
                    _activeDownloads.Remove(GetFileKey(task.File));
                    RefreshAllDownloadVisuals();
                    OnPropertyChanged(nameof(HasSelectedActiveDownloads));
                });
                task.Cancellation.Dispose();
            }
        }

        private async Task DeleteSelectedAsync() => await DeleteEntriesAsync(SelectedEntries.ToArray());

        private async Task DeleteEntriesAsync(IEnumerable<ExplorerFileItem> entries)
        {
            var targetEntries = entries.Distinct().ToArray();
            var files = targetEntries.SelectMany(entry => entry.Files).DistinctBy(GetFileKey).ToArray();
            if (files.Length == 0)
            {
                return;
            }

            var description = files.Length == 1 ? Path.GetFileName(files[0].Name) : $"{files.Length.ToString("N0", CultureInfo.CurrentCulture)} files";
            if (ConfirmRequested is null || !await ConfirmRequested("Delete files", $"Delete {description} from the camera?"))
            {
                return;
            }

            foreach (var file in files)
            {
                CancelDownload(file);
                var path = DeviceApi.EscapeFilePath(file.Name, true);
                await _api.GetInfoAsync($"deletefile?file={path}");
                _manifestFiles.RemoveAll(item => string.Equals(GetFileKey(item), GetFileKey(file), StringComparison.OrdinalIgnoreCase));
            }
            RebuildFolderCounts();
            RebuildEntries();
            OnPropertyChanged(nameof(SdCardSummary));
            await SaveManifestAsync();
            Log?.Invoke($"Deleted {description}.");
        }

        private bool IsFileDownloadingByEntry(ExplorerFileItem entry) => entry.Files.Any(IsFileDownloading);

        private void RefreshEntryDownloadState(ExplorerFileItem entry)
        {
            var tasks = entry.Files.Select(file => _activeDownloads.GetValueOrDefault(GetFileKey(file))).Where(task => task is not null).ToArray();
            var active = tasks.Length > 0;
            var total = tasks.Sum(task => task!.TotalBytes.GetValueOrDefault());
            var transferred = tasks.Sum(task => task!.BytesTransferred);
            var percent = total > 0 ? transferred * 100d / total : 0d;
            var queued = tasks.Any(task => task!.State == DownloadState.Queued);
            var progressText = total > 0 ? $"{percent:0}%" : "Downloading";
            entry.SetDownloadState(active, percent, active ? queued ? "Queued" : progressText : string.Empty);
        }

        private void RefreshAllDownloadVisuals()
        {
            foreach (var entry in Files)
            {
                entry.RefreshDownloadState();
            }

            foreach (var folder in FileFolders)
            {
                folder.RefreshDownloadState();
            }

            OnPropertyChanged(nameof(HasSelectedActiveDownloads));
            DownloadButtonText = HasSelectedActiveDownloads ? "Cancel" : "Download";
            DownloadButtonTooltip = HasSelectedActiveDownloads ? "Cancel selected downloads" : "Queue selected files for download";
            OnPropertyChanged(nameof(DownloadButtonIconPath));
            ((RelayCommand)CancelSelectedDownloadsCommand).RaiseCanExecuteChanged();
        }

        private static string GetFileKey(DeviceFile file) => $"{file.Folder}/{file.Name}";

        private void CancelViewLoads()
        {
            _fileLoadCancellation?.Cancel();
            _thumbnailLoadCancellation?.Cancel();
        }

        private void RaiseCommandStates()
        {
            foreach (var command in new[] { EnterCommand, RefreshCommand, DownloadOrCancelSelectedCommand, DeleteSelectedCommand, PlaySelectedCommand })
            {
                ((AsyncCommand)command).RaiseCanExecuteChanged();
            }

            foreach (var command in new[] { SelectSdCardCommand, CancelSelectedDownloadsCommand, SearchCommand, ToggleFrontBackCommand })
            {
                ((RelayCommand)command).RaiseCanExecuteChanged();
            }

            foreach (var folder in FileFolders)
            {
                ((AsyncCommand)folder.DownloadAllCommand).RaiseCanExecuteChanged();
                ((RelayCommand)folder.CancelDownloadsCommand).RaiseCanExecuteChanged();
            }
        }

        private static string TextValue(JsonElement element, string property, string fallback) => element.TryGetProperty(property, out var value) ? value.ToString() : fallback;
        private static int IntegerValue(JsonElement element, string property, int fallback) => element.TryGetProperty(property, out var value) && value.TryGetInt32(out var parsed) ? parsed : fallback;
        private static long LongValue(JsonElement element, string property, long fallback) => element.TryGetProperty(property, out var value) && value.TryGetInt64(out var parsed) ? parsed : fallback;

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        private sealed record FileManifest(string Host, DateTimeOffset SavedAt, List<DeviceFile> Files);
    }
}
