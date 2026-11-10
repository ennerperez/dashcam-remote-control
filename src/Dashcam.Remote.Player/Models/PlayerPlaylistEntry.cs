using System.Globalization;
using System.ComponentModel;

namespace Dashcam.Remote.Player
{
    public sealed record PlayerPlaylistEntry(IReadOnlyList<Uri> MediaUris, bool IsFrontBackPair) : INotifyPropertyChanged
    {
        private long? _durationMilliseconds;

        public event PropertyChangedEventHandler? PropertyChanged;

        public DateTime? CreatedAt => MediaUris
            .Where(uri => uri.IsFile)
            .Select(uri => (DateTime?)File.GetCreationTime(uri.LocalPath))
            .Min();

        public string CreatedAtText => CreatedAt?.ToString("g", CultureInfo.CurrentCulture) ?? string.Empty;

        public string DurationText
        {
            get
            {
                if (_durationMilliseconds is not long duration)
                {
                    return "—";
                }

                var time = TimeSpan.FromMilliseconds(duration);
                return duration >= 3_600_000
                    ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
                    : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
            }
        }

        public string SizeText
        {
            get
            {
                var size = MediaUris.Where(uri => uri.IsFile && File.Exists(uri.LocalPath))
                    .Sum(uri => new FileInfo(uri.LocalPath).Length);
                return size < 1024 * 1024
                    ? $"{size / 1024d:0} KB"
                    : $"{size / (1024d * 1024d):0.0} MB";
            }
        }

        public string CameraText
        {
            get
            {
                if (IsFrontBackPair)
                {
                    return "Front + Rear";
                }

                var name = Path.GetFileNameWithoutExtension(MediaUris[0].LocalPath);
                if (name.EndsWith("_B", StringComparison.OrdinalIgnoreCase) || name.EndsWith("_REAR", StringComparison.OrdinalIgnoreCase))
                {
                    return "Rear";
                }

                return "Front";
            }
        }

        public string SummaryText => $"{CreatedAtText} · {DurationText} · {SizeText} · {CameraText}";

        public void SetDuration(long durationMilliseconds)
        {
            if (durationMilliseconds <= 0 || _durationMilliseconds == durationMilliseconds)
            {
                return;
            }

            _durationMilliseconds = durationMilliseconds;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DurationText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SummaryText)));
        }

        public string DisplayName
        {
            get
            {
                var name = Path.GetFileName(MediaUris[0].LocalPath);
                return IsFrontBackPair ? PlayerPlaylistGrouping.RemoveCameraMarkers(name) : name;
            }
        }

        public override string ToString() => DisplayName;
    }
}
