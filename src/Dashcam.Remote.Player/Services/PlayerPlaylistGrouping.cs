using System.Text.RegularExpressions;

namespace Dashcam.Remote.Player;

public static partial class PlayerPlaylistGrouping
{
    public static IReadOnlyList<PlayerPlaylistEntry> Group(IReadOnlyList<Uri> mediaUris)
    {
        var groups = new Dictionary<string, List<(Uri Uri, CameraSide Side)>>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<PlayerPlaylistEntry>();

        foreach (var mediaUri in mediaUris)
        {
            var path = mediaUri.IsFile ? mediaUri.LocalPath : mediaUri.AbsolutePath;
            var side = GetCameraSide(path);
            if (!side.IsFront && !side.IsBack)
            {
                entries.Add(new PlayerPlaylistEntry([mediaUri], IsFrontBackPair: false));
                continue;
            }

            var pairKey = GetPairKey(path);
            if (!groups.TryGetValue(pairKey, out var pair))
            {
                groups[pairKey] = pair = [];
            }

            pair.Add((mediaUri, side));
        }

        foreach (var pair in groups.Values)
        {
            var fronts = new Queue<Uri>(pair.Where(item => item.Side.IsFront).Select(item => item.Uri));
            var backs = new Queue<Uri>(pair.Where(item => item.Side.IsBack).Select(item => item.Uri));
            while (fronts.Count > 0 && backs.Count > 0)
            {
                entries.Add(new PlayerPlaylistEntry([fronts.Dequeue(), backs.Dequeue()], IsFrontBackPair: true));
            }

            while (fronts.Count > 0)
            {
                entries.Add(new PlayerPlaylistEntry([fronts.Dequeue()], IsFrontBackPair: false));
            }

            while (backs.Count > 0)
            {
                entries.Add(new PlayerPlaylistEntry([backs.Dequeue()], IsFrontBackPair: false));
            }
        }

        return entries;
    }

    public static string RemoveCameraMarkers(string path)
    {
        var normalizedPath = path.Replace('\\', '/');
        var hasCameraMarker = GetCameraSide(normalizedPath) is { IsFront: true } or { IsBack: true };
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

    private static string GetPairKey(string path)
    {
        var normalizedPath = path.Replace('\\', '/');
        var hasCameraMarker = GetCameraSide(normalizedPath) is { IsFront: true } or { IsBack: true };
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

        return string.Join('/', segments);
    }

    private static CameraSide GetCameraSide(string path)
    {
        var markers = CameraTokenRegex().Matches(path.Replace('\\', '/'))
            .Select(match => match.Groups[2].Value.ToUpperInvariant());
        var front = markers.Any(marker => marker is "F" or "FRONT");
        var back = markers.Any(marker => marker is "B" or "BACK" or "REAR");
        return front == back
            ? new CameraSide(false, false)
            : new CameraSide(front, back);
    }

    private readonly record struct CameraSide(bool IsFront, bool IsBack);

    [GeneratedRegex("(^|[_\\-/])(front|back|rear|f|b)(?=[_.\\-/]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CameraTokenRegex();

    [GeneratedRegex("_[0-9]{2}_(?=(front|back|rear|f|b)(?=[_.\\-/]|$))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CameraTimestampSecondsRegex();

    [GeneratedRegex("^(\\d{4}[-_]\\d{2}[-_]\\d{2}[_T]\\d{2}[_:]\\d{2})[_:]\\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex CameraTimestampSuffixRegex();
}
