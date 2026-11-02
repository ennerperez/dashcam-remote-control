using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Avalonia.Media.Imaging;

namespace Dashcam.Remote
{
    public sealed partial class ExplorerFileItem : INotifyPropertyChanged
    {
        private readonly Action<ExplorerFileItem> _refreshDownloadState;
        private readonly Func<ExplorerFileItem, bool> _canCancel;
        private bool _isDownloading;
        private double _downloadProgress;
        private string _downloadStatus = string.Empty;

        public ExplorerFileItem(
            IReadOnlyList<DeviceFile> files,
            bool isGrouped,
            string displayName,
            Func<ExplorerFileItem, Task> download,
            Func<ExplorerFileItem, Task> delete,
            Func<ExplorerFileItem, Task> preview,
            Action<ExplorerFileItem> cancel,
            Func<ExplorerFileItem, bool> canCancel,
            Action<ExplorerFileItem> refreshDownloadState)
        {
            Files = files;
            IsGrouped = isGrouped;
            DisplayName = displayName;
            _refreshDownloadState = refreshDownloadState;
            _canCancel = canCancel;
            DownloadCommand = new AsyncCommand(() => download(this));
            DeleteCommand = new AsyncCommand(() => delete(this));
            PreviewCommand = new AsyncCommand(() => preview(this));
            CancelDownloadCommand = new RelayCommand(_ => cancel(this), _ => canCancel(this));
            var date = ParseDeviceDate(files[0].CreatedAt);
            CreatedAtText = date?.ToString("g", CultureInfo.CurrentCulture) ?? string.Empty;
            SortDate = date?.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) ?? string.Empty;
            Size = files.Sum(file => Math.Max(0, file.Size));
            Duration = files.Where(file => file.Duration >= 0).Sum(file => file.Duration);
            HasDuration = files.Any(file => file.Duration >= 0);
            CameraLabel = isGrouped ? "Front + Back" : CameraMarker($"{files[0].Folder}/{files[0].Name}").Label;
            IsPhoto = files.All(IsImageFile);
            IconPath = IsPhoto ? "/Assets/Icons/photo_camera.svg" : "/Assets/Icons/videocam.svg";
            FileNames = files.Select(file => Path.GetFileName(file.Name)).ToArray();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public IReadOnlyList<DeviceFile> Files { get; }
        public bool IsGrouped { get; }
        public bool IsPhoto { get; }
        public string DisplayName { get; }
        public string IconPath { get; }
        public string CreatedAtText { get; }
        public string SortDate { get; }
        public string CameraLabel { get; }
        public IReadOnlyList<string> FileNames { get; }
        public long Size { get; }
        public string SizeText => Humanizer.ByteSize.FromBytes(Size).ToString("0.##", CultureInfo.CurrentCulture);
        public int Duration { get; }
        public bool HasDuration { get; }
        public string DurationText => TimeSpan.FromSeconds(Duration).ToString(Duration >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.CurrentCulture);
        public ICommand DownloadCommand { get; }
        public ICommand DeleteCommand { get; }
        public ICommand PreviewCommand { get; }
        public ICommand CancelDownloadCommand { get; }
        public bool IsDownloading { get => _isDownloading; private set => Set(ref _isDownloading, value); }
        public bool CanCancelDownload => _canCancel(this);
        public double DownloadProgress { get => _downloadProgress; private set => Set(ref _downloadProgress, value); }
        public string DownloadStatus { get => _downloadStatus; private set => Set(ref _downloadStatus, value); }

        public void RefreshDownloadState()
        {
            _refreshDownloadState(this);
            OnPropertyChanged(nameof(CanCancelDownload));
            ((RelayCommand)CancelDownloadCommand).RaiseCanExecuteChanged();
        }

        public void SetDownloadState(bool active, double progress, string status)
        {
            IsDownloading = active;
            DownloadProgress = progress;
            DownloadStatus = status;
            OnPropertyChanged(nameof(CanCancelDownload));
            ((RelayCommand)CancelDownloadCommand).RaiseCanExecuteChanged();
        }

        public void RefreshContextState()
        {
            OnPropertyChanged(nameof(CanCancelDownload));
            ((RelayCommand)CancelDownloadCommand).RaiseCanExecuteChanged();
        }

        public static string PairKey(DeviceFile file)
        {
            var fullPath = $"{file.Folder}/{file.Name}".Replace('\\', '/');
            var hasCameraMarker = HasCameraMarker(fullPath);
            var segments = fullPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < segments.Length; index++)
            {
                var extension = index == segments.Length - 1 ? Path.GetExtension(segments[index]) : string.Empty;
                var stem = extension.Length > 0 ? segments[index][..^extension.Length] : segments[index];
                // Front and back recordings can start one or more seconds apart.
                stem = CameraTimestampSecondsRegex().Replace(stem, "_");
                if (hasCameraMarker && index == segments.Length - 1)
                {
                    stem = CameraTimestampSuffixRegex().Replace(stem, "$1");
                }

                stem = CameraTokenRegex().Replace(stem, "$1");
                stem = Regex.Replace(stem, "[_-]{2,}", "_").Trim('_', '-');
                segments[index] = $"{stem}{extension}";
            }

            return string.Join('/', segments);
        }

        public static CameraSide CameraMarker(string path)
        {
            var normalized = path.Replace('\\', '/');
            var markers = CameraTokenRegex().Matches(normalized).Select(match => match.Groups[2].Value.ToUpperInvariant()).ToArray();
            var front = markers.Any(marker => marker is "F" or "FRONT");
            var back = markers.Any(marker => marker is "B" or "BACK" or "REAR");
            if (front && !back)
            {
                return new CameraSide("Front", true, false);
            }

            if (back && !front)
            {
                return new CameraSide("Back", false, true);
            }

            return new CameraSide(string.Empty, false, false);
        }

        public static string RemoveCameraMarkers(string path)
        {
            var normalizedPath = path.Replace('\\', '/');
            var hasCameraMarker = HasCameraMarker(normalizedPath);
            var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < segments.Length; index++)
            {
                var extension = index == segments.Length - 1 ? Path.GetExtension(segments[index]) : string.Empty;
                var stem = extension.Length > 0 ? segments[index][..^extension.Length] : segments[index];
                stem = CameraTimestampSecondsRegex().Replace(stem, "_");
                if (hasCameraMarker && index == segments.Length - 1)
                {
                    stem = CameraTimestampSuffixRegex().Replace(stem, "$1");
                }

                stem = CameraTokenRegex().Replace(stem, "$1");
                stem = Regex.Replace(stem, "[_-]{2,}", "_").Trim('_', '-');
                segments[index] = $"{stem}{extension}";
            }

            return Path.GetFileName(string.Join('/', segments));
        }

        private static bool HasCameraMarker(string path)
        {
            var markers = CameraTokenRegex().Matches(path).Select(match => match.Groups[2].Value.ToUpperInvariant());
            return markers.Any(marker => marker is "F" or "B" or "FRONT" or "BACK" or "REAR");
        }

        public static bool IsImageFile(DeviceFile file) =>
            Path.GetExtension(file.Name).Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(file.Name).Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(file.Name).Equals(".png", StringComparison.OrdinalIgnoreCase);

        private static DateTime? ParseDeviceDate(string value)
        {
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timestamp))
            {
                try
                {
                    return (value.Length > 10
                        ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
                        : DateTimeOffset.FromUnixTimeSeconds(timestamp)).LocalDateTime;
                }
                catch (ArgumentOutOfRangeException)
                {
                }
            }

            if (DateTime.TryParseExact(value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
            {
                return parsed;
            }

            return DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out parsed) ? parsed : null;
        }

        [GeneratedRegex("(^|[_\\-/])(front|back|rear|f|b)(?=[_.\\-/]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex CameraTokenRegex();

        [GeneratedRegex("_[0-9]{2}_(?=(front|back|rear|f|b)(?=[_.\\-/]|$))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex CameraTimestampSecondsRegex();

        [GeneratedRegex("^(\\d{4}[-_]\\d{2}[-_]\\d{2}[_T]\\d{2}[_:]\\d{2})[_:]\\d{2}$", RegexOptions.CultureInvariant)]
        private static partial Regex CameraTimestampSuffixRegex();

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

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public readonly record struct CameraSide(string Label, bool IsFront, bool IsBack);

    public sealed class StorageFolderItem : INotifyPropertyChanged
    {
        private readonly Func<string, bool> _hasActiveDownloads;
        private int _fileCount;
        private bool _isOpen;

        public StorageFolderItem(string name, Func<string, Task> downloadAll, Action<string> cancelDownloads, Func<string, bool> hasActiveDownloads)
        {
            Name = name;
            _hasActiveDownloads = hasActiveDownloads;
            DownloadAllCommand = new AsyncCommand(() => downloadAll(Name));
            CancelDownloadsCommand = new RelayCommand(_ => cancelDownloads(Name));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public string Name { get; }
        public bool HasActiveDownloads => _hasActiveDownloads(Name);
        public int FileCount
        {
            get => _fileCount; set
            {
                if (_fileCount == value)
                {
                    return;
                }

                _fileCount = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileCount)));
            }
        }
        public bool IsOpen
        {
            get => _isOpen; set
            {
                if (_isOpen == value)
                {
                    return;
                }

                _isOpen = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOpen))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconPath)));
            }
        }
        public string IconPath => IsOpen ? "/Assets/Icons/folder_open.svg" : "/Assets/Icons/folder.svg";
        public ICommand DownloadAllCommand { get; }
        public ICommand CancelDownloadsCommand { get; }
        public void RefreshDownloadState() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasActiveDownloads)));
    }

    public sealed class DownloadQueueItem(DeviceFile file)
    {
        public DeviceFile File { get; } = file;
        public CancellationTokenSource Cancellation { get; } = new();
        public DownloadState State { get; set; } = DownloadState.Queued;
        public long BytesTransferred { get; set; }
        public long? TotalBytes { get; set; }
        public string StatusText => State switch
        {
            DownloadState.Queued => "Queued",
            DownloadState.Downloading when TotalBytes is > 0 => $"{BytesTransferred.ToString("N0", CultureInfo.CurrentCulture)} / {TotalBytes.Value.ToString("N0", CultureInfo.CurrentCulture)} B",
            DownloadState.Downloading => $"{BytesTransferred.ToString("N0", CultureInfo.CurrentCulture)} B",
            DownloadState.Completed => "Complete",
            DownloadState.Cancelled => "Cancelled",
            DownloadState.Failed => "Failed",
            _ => string.Empty
        };
        public double Progress => TotalBytes is > 0 ? Math.Clamp(BytesTransferred * 100d / TotalBytes.Value, 0d, 100d) : 0d;
    }

    public enum DownloadState { Queued, Downloading, Completed, Cancelled, Failed }

    public sealed record PreviewImageItem(string Label, Bitmap Image);

    public sealed record ImagePreviewViewModel(IReadOnlyList<PreviewImageItem> Images)
    {
        public int ColumnCount => Math.Max(1, Images.Count);
    }
}
