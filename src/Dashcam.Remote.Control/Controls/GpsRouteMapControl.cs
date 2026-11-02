using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Dashcam.Remote
{
    public sealed class GpsRouteMapControl : Control
    {
        private const int TileSize = 256;
        private static readonly HttpClient s_http = CreateHttpClient();
        private readonly Dictionary<TileKey, Bitmap> _tiles = [];
        private readonly HashSet<TileKey> _requestedTiles = [];
        private readonly Dictionary<TileKey, DateTimeOffset> _retryAfter = [];

        public static readonly StyledProperty<IReadOnlyList<GpsPoint>> PointsProperty =
            AvaloniaProperty.Register<GpsRouteMapControl, IReadOnlyList<GpsPoint>>(nameof(Points), Array.Empty<GpsPoint>());
        public static readonly StyledProperty<GpsPoint?> CurrentPointProperty =
            AvaloniaProperty.Register<GpsRouteMapControl, GpsPoint?>(nameof(CurrentPoint));
        public static readonly StyledProperty<double> MeanSpeedKmhProperty =
            AvaloniaProperty.Register<GpsRouteMapControl, double>(nameof(MeanSpeedKmh));
        public static readonly StyledProperty<double> SpeedDeviationKmhProperty =
            AvaloniaProperty.Register<GpsRouteMapControl, double>(nameof(SpeedDeviationKmh));

        public IReadOnlyList<GpsPoint> Points { get => GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
        public GpsPoint? CurrentPoint { get => GetValue(CurrentPointProperty); set => SetValue(CurrentPointProperty, value); }
        public double MeanSpeedKmh { get => GetValue(MeanSpeedKmhProperty); set => SetValue(MeanSpeedKmhProperty, value); }
        public double SpeedDeviationKmh { get => GetValue(SpeedDeviationKmhProperty); set => SetValue(SpeedDeviationKmhProperty, value); }

        static GpsRouteMapControl()
        {
            AffectsRender<GpsRouteMapControl>(PointsProperty, CurrentPointProperty, MeanSpeedKmhProperty, SpeedDeviationKmhProperty);
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            var viewport = GetViewport();
            var left = viewport.CenterX - Bounds.Width / 2;
            var top = viewport.CenterY - Bounds.Height / 2;
            var tileCount = 1 << viewport.Zoom;
            var firstX = (int)Math.Floor(left / TileSize);
            var lastX = (int)Math.Floor((left + Bounds.Width) / TileSize);
            var firstY = Math.Max(0, (int)Math.Floor(top / TileSize));
            var lastY = Math.Min(tileCount - 1, (int)Math.Floor((top + Bounds.Height) / TileSize));

            context.FillRectangle(Brushes.WhiteSmoke, new Rect(Bounds.Size));
            for (var tileY = firstY; tileY <= lastY; tileY++)
            {
                for (var tileX = firstX; tileX <= lastX; tileX++)
                {
                    var wrappedX = ((tileX % tileCount) + tileCount) % tileCount;
                    var key = new TileKey(viewport.Zoom, wrappedX, tileY);
                    var target = new Rect(tileX * TileSize - left, tileY * TileSize - top, TileSize, TileSize);
                    if (_tiles.TryGetValue(key, out var bitmap))
                    {
                        context.DrawImage(bitmap, new Rect(bitmap.Size), target);
                    }
                    else
                    {
                        RequestTile(key);
                    }
                }
            }

            if (_tiles.Count == 0)
            {
                var message = _retryAfter.Count == 0 ? "Loading map tiles…" : "Map tiles unavailable. Check internet access.";
                var text = new FormattedText(message, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 12, Brushes.DimGray);
                context.DrawText(text, new Point(Math.Max(8, (Bounds.Width - text.Width) / 2), Math.Max(8, (Bounds.Height - text.Height) / 2)));
            }

            DrawRoute(context, viewport, left, top);
        }

        private void DrawRoute(DrawingContext context, Viewport viewport, double left, double top)
        {
            var points = Points;
            if (points.Count == 0)
            {
                return;
            }

            Point ToScreen(GpsPoint point)
            {
                var projected = Project(point.Latitude, point.Longitude, viewport.Zoom);
                return new Point(projected.X - left, projected.Y - top);
            }

            for (var index = 1; index < points.Count; index++)
            {
                var first = points[index - 1];
                var second = points[index];
                var speed = Math.Max(0, (first.SpeedMetersPerSecond + second.SpeedMetersPerSecond) * 1.8);
                var color = speed < MeanSpeedKmh - SpeedDeviationKmh
                    ? Color.Parse("#F5C451")
                    : speed > MeanSpeedKmh + SpeedDeviationKmh
                        ? Color.Parse("#E53935")
                        : Color.Parse("#21A36B");
                context.DrawLine(new Pen(new SolidColorBrush(color), 4), ToScreen(first), ToScreen(second));
            }

            context.DrawEllipse(Brushes.LimeGreen, new Pen(Brushes.White, 2), ToScreen(points[0]), 7, 7);
            if (points.Count > 1)
            {
                context.DrawEllipse(Brushes.Red, new Pen(Brushes.White, 2), ToScreen(points[^1]), 7, 7);
            }

            if (CurrentPoint is not { } current)
            {
                return;
            }

            var center = ToScreen(current);
            context.DrawEllipse(Brushes.DarkBlue, new Pen(Brushes.White, 2), center, 10, 10);
            context.DrawEllipse(Brushes.White, null, center, 3, 3);
        }

        private Viewport GetViewport()
        {
            if (Bounds.Width <= 0 || Bounds.Height <= 0)
            {
                return new Viewport(3, 1024, 1024);
            }

            if (Points.Count == 0)
            {
                return new Viewport(2, 512, 512);
            }

            var projectedAtMax = Points.Select(point => Project(point.Latitude, point.Longitude, 18)).ToArray();
            var minX = projectedAtMax.Min(point => point.X);
            var maxX = projectedAtMax.Max(point => point.X);
            var minY = projectedAtMax.Min(point => point.Y);
            var maxY = projectedAtMax.Max(point => point.Y);
            var width = Math.Max(1, maxX - minX);
            var height = Math.Max(1, maxY - minY);
            var scale = Math.Min((Bounds.Width - 48) / width, (Bounds.Height - 48) / height);
            var zoom = Math.Clamp(18 + (int)Math.Floor(Math.Log2(Math.Max(.0001, scale))), 1, 18);
            var projectedCenter = Project((Points.Min(point => point.Latitude) + Points.Max(point => point.Latitude)) / 2,
                (Points.Min(point => point.Longitude) + Points.Max(point => point.Longitude)) / 2, zoom);
            return new Viewport(zoom, projectedCenter.X, projectedCenter.Y);
        }

        private static Point Project(double latitude, double longitude, int zoom)
        {
            var scale = TileSize * (1 << zoom);
            var x = (longitude + 180) / 360 * scale;
            var sinLatitude = Math.Sin(Math.Clamp(latitude, -85.05112878, 85.05112878) * Math.PI / 180);
            var y = (.5 - Math.Log((1 + sinLatitude) / (1 - sinLatitude)) / (4 * Math.PI)) * scale;
            return new Point(x, y);
        }

        private void RequestTile(TileKey key)
        {
            if (_retryAfter.TryGetValue(key, out var retryAfter) && retryAfter > DateTimeOffset.UtcNow)
            {
                return;
            }

            if (!_requestedTiles.Add(key))
            {
                return;
            }

            _ = LoadTileAsync(key);
        }

        private async Task LoadTileAsync(TileKey key)
        {
            try
            {
                var path = GetTilePath(key);
                var metadataPath = path + ".json";
                var metadata = await ReadTileMetadataAsync(metadataPath);
                if (File.Exists(path) && metadata?.ExpiresAt > DateTimeOffset.UtcNow)
                {
                    await PublishTileAsync(key, await File.ReadAllBytesAsync(path));
                    return;
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://tile.openstreetmap.org/{key.Zoom}/{key.X}/{key.Y}.png");
                if (metadata?.ETag is { Length: > 0 } etag && EntityTagHeaderValue.TryParse(etag, out var tag))
                {
                    request.Headers.IfNoneMatch.Add(tag);
                }

                if (metadata?.LastModified is { } lastModified)
                {
                    request.Headers.IfModifiedSince = lastModified;
                }

                using var response = await s_http.SendAsync(request);
                if (response.StatusCode == System.Net.HttpStatusCode.NotModified && File.Exists(path))
                {
                    await WriteTileMetadataAsync(metadataPath, CreateMetadata(response, metadata?.ETag, metadata?.LastModified));
                    await PublishTileAsync(key, await File.ReadAllBytesAsync(path));
                    return;
                }

                response.EnsureSuccessStatusCode();
                var data = await response.Content.ReadAsByteArrayAsync();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, data);
                await WriteTileMetadataAsync(metadataPath, CreateMetadata(response,
                    response.Headers.ETag?.ToString(), response.Content.Headers.LastModified));
                await PublishTileAsync(key, data);
            }
            catch
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _requestedTiles.Remove(key);
                    _retryAfter[key] = DateTimeOffset.UtcNow.AddSeconds(30);
                    InvalidateVisual();
                });
            }
        }

        private async Task PublishTileAsync(TileKey key, byte[] data)
        {
            using var stream = new MemoryStream(data);
            var bitmap = new Bitmap(stream);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_tiles.Remove(key, out var previous))
                {
                    previous.Dispose();
                }

                _tiles[key] = bitmap;
                _retryAfter.Remove(key);
                InvalidateVisual();
            });
        }

        private static async Task<TileMetadata?> ReadTileMetadataAsync(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            try { return JsonSerializer.Deserialize<TileMetadata>(await File.ReadAllTextAsync(path)); }
            catch (JsonException) { return null; }
        }

        private static Task WriteTileMetadataAsync(string path, TileMetadata metadata) =>
            File.WriteAllTextAsync(path, JsonSerializer.Serialize(metadata));

        private static TileMetadata CreateMetadata(HttpResponseMessage response, string? etag, DateTimeOffset? lastModified)
        {
            var expires = response.Headers.CacheControl?.MaxAge is { } maxAge
                ? DateTimeOffset.UtcNow + maxAge
                : response.Content.Headers.Expires ?? DateTimeOffset.UtcNow.AddDays(7);
            return new TileMetadata(expires, etag, lastModified);
        }

        private static string GetTilePath(TileKey key) => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DashcamRemoteControl", "MapTiles", key.Zoom.ToString(), key.X.ToString(), $"{key.Y}.png");

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DashcamRemoteControl", "1.0"));
            return client;
        }

        private readonly record struct TileKey(int Zoom, int X, int Y);
        private readonly record struct Viewport(int Zoom, double CenterX, double CenterY);
        private sealed record TileMetadata(DateTimeOffset ExpiresAt, string? ETag, DateTimeOffset? LastModified);
    }
}
