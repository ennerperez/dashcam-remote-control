using System.Globalization;

namespace Dashcam.Remote.Player;

public sealed record PlayerPlaylistEntry(IReadOnlyList<Uri> MediaUris, bool IsFrontBackPair)
{
    public DateTime? CreatedAt => MediaUris
        .Where(uri => uri.IsFile)
        .Select(uri => (DateTime?)File.GetCreationTime(uri.LocalPath))
        .Min();

    public string CreatedAtText => CreatedAt?.ToString("g", CultureInfo.CurrentCulture) ?? string.Empty;

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
