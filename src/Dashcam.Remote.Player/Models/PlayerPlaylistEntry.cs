namespace Dashcam.Remote.Player;

public sealed record PlayerPlaylistEntry(IReadOnlyList<Uri> MediaUris, bool IsFrontBackPair)
{
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
