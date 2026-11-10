namespace Dashcam.Remote.Player.Models
{
    public sealed record ObjectDetection(
        string Label,
        float Confidence,
        float X,
        float Y,
        float Width,
        float Height,
        int TrackId = 0,
        int ColorIndex = -1);
}
