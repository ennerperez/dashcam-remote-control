using Dashcam.Remote.Player.Models;

namespace Dashcam.Remote.Player.Services
{
    public sealed class ObjectDetectionTracker
    {
        public const int ColorSlotsPerCamera = 24;

        private const long DetectionRetentionMilliseconds = 750;
        private const long TrackAssociationMilliseconds = 2500;
        private const double MaximumPredictionSeconds = 1.25;
        private const double MaximumObjectSpeed = 0.75;
        private readonly int _cameraIndex;
        private readonly Func<int> _nextTrackId;
        private readonly List<Track> _tracks = [];
        private long _lastCaptureTimeMs = long.MinValue;

        public ObjectDetectionTracker(int cameraIndex, Func<int> nextTrackId)
        {
            _cameraIndex = cameraIndex;
            _nextTrackId = nextTrackId;
        }

        public IReadOnlyList<ObjectDetection> Update(
            IReadOnlyList<ObjectDetection> detections,
            long captureTimeMs,
            long currentPlaybackTimeMs)
        {
            if (_lastCaptureTimeMs != long.MinValue &&
                (captureTimeMs < _lastCaptureTimeMs || captureTimeMs - _lastCaptureTimeMs > 5000))
            {
                Reset();
            }

            _lastCaptureTimeMs = captureTimeMs;
            _tracks.RemoveAll(track => captureTimeMs - track.LastSeenTimeMs > TrackAssociationMilliseconds);

            var matchedTrackIds = new HashSet<int>();
            foreach (var detection in detections.Where(IsTrackable))
            {
                var match = FindMatch(detection, matchedTrackIds);
                if (match is null)
                {
                    var colorIndex = AllocateColorIndex(matchedTrackIds);
                    match = new Track(_nextTrackId(), colorIndex, detection, captureTimeMs);
                    _tracks.Add(match);
                }
                else
                {
                    UpdateTrack(match, detection, captureTimeMs);
                }

                matchedTrackIds.Add(match.Id);
            }

            return _tracks
                .Where(track => currentPlaybackTimeMs - track.LastSeenTimeMs <= DetectionRetentionMilliseconds)
                .Select(track => Predict(track, currentPlaybackTimeMs))
                .ToArray();
        }

        public void Reset()
        {
            _tracks.Clear();
            _lastCaptureTimeMs = long.MinValue;
        }

        public IReadOnlyList<ObjectDetection> GetCurrentDetections(long currentPlaybackTimeMs)
        {
            return _tracks
                .Where(track => currentPlaybackTimeMs >= track.LastSeenTimeMs &&
                                currentPlaybackTimeMs - track.LastSeenTimeMs <= DetectionRetentionMilliseconds)
                .Select(track => Predict(track, currentPlaybackTimeMs))
                .ToArray();
        }

        private Track? FindMatch(ObjectDetection detection, HashSet<int> matchedTrackIds)
        {
            Track? bestMatch = null;
            var bestScore = double.NegativeInfinity;
            foreach (var track in _tracks)
            {
                if (matchedTrackIds.Contains(track.Id) || !string.Equals(track.Detection.Label, detection.Label, StringComparison.Ordinal))
                {
                    continue;
                }

                var overlap = IntersectionOverUnion(track.Detection, detection);
                var distance = CenterDistance(track.Detection, detection);
                if (overlap < 0.05 && distance > 0.12)
                {
                    continue;
                }

                var score = overlap - distance * 0.25;
                if (!(score > bestScore))
                {
                    continue;
                }

                bestScore = score;
                bestMatch = track;
            }

            return bestMatch;
        }

        private static void UpdateTrack(Track track, ObjectDetection detection, long captureTimeMs)
        {
            var elapsedSeconds = (captureTimeMs - track.LastSeenTimeMs) / 1000d;
            if (elapsedSeconds > 0.05 && elapsedSeconds <= 5)
            {
                var velocityX = ((detection.X + detection.Width / 2) - (track.Detection.X + track.Detection.Width / 2)) / elapsedSeconds;
                var velocityY = ((detection.Y + detection.Height / 2) - (track.Detection.Y + track.Detection.Height / 2)) / elapsedSeconds;
                track.VelocityX = BlendVelocity(track.VelocityX, velocityX);
                track.VelocityY = BlendVelocity(track.VelocityY, velocityY);
            }

            track.Detection = detection with { TrackId = track.Id, ColorIndex = track.ColorIndex };
            track.LastSeenTimeMs = captureTimeMs;
        }

        private int AllocateColorIndex(HashSet<int> matchedTrackIds)
        {
            var firstColorIndex = _cameraIndex * ColorSlotsPerCamera;
            for (var slot = 0; slot < ColorSlotsPerCamera; slot++)
            {
                var colorIndex = firstColorIndex + slot;
                if (_tracks.All(track => track.ColorIndex != colorIndex))
                {
                    return colorIndex;
                }
            }

            var expiredTrack = _tracks
                .Where(track => !matchedTrackIds.Contains(track.Id))
                .OrderBy(track => track.LastSeenTimeMs)
                .FirstOrDefault();
            if (expiredTrack is null)
            {
                return firstColorIndex;
            }

            {
                var colorIndex = expiredTrack.ColorIndex;
                _tracks.Remove(expiredTrack);
                return colorIndex;
            }

        }

        private static ObjectDetection Predict(Track track, long currentPlaybackTimeMs)
        {
            var elapsedSeconds = Math.Clamp((currentPlaybackTimeMs - track.LastSeenTimeMs) / 1000d, 0, MaximumPredictionSeconds);
            var detection = track.Detection;
            var centerX = detection.X + detection.Width / 2 + track.VelocityX * elapsedSeconds;
            var centerY = detection.Y + detection.Height / 2 + track.VelocityY * elapsedSeconds;
            var left = Math.Clamp(centerX - detection.Width / 2, 0, 1);
            var top = Math.Clamp(centerY - detection.Height / 2, 0, 1);
            var right = Math.Clamp(centerX + detection.Width / 2, 0, 1);
            var bottom = Math.Clamp(centerY + detection.Height / 2, 0, 1);

            return detection with
            {
                X = (float)left,
                Y = (float)top,
                Width = (float)Math.Max(0, right - left),
                Height = (float)Math.Max(0, bottom - top)
            };
        }

        private static double BlendVelocity(double previous, double measured) =>
            Math.Clamp(previous * 0.35 + Math.Clamp(measured, -MaximumObjectSpeed, MaximumObjectSpeed) * 0.65,
                -MaximumObjectSpeed,
                MaximumObjectSpeed);

        private static bool IsTrackable(ObjectDetection detection) =>
            detection.Label is "bicycle" or "bus" or "car" or "motorbike" or "person";

        private static double CenterDistance(ObjectDetection first, ObjectDetection second)
        {
            var x = first.X + first.Width / 2 - second.X - second.Width / 2;
            var y = first.Y + first.Height / 2 - second.Y - second.Height / 2;
            return Math.Sqrt(x * x + y * y);
        }

        private static double IntersectionOverUnion(ObjectDetection first, ObjectDetection second)
        {
            var overlapWidth = Math.Max(0, Math.Min(first.X + first.Width, second.X + second.Width) - Math.Max(first.X, second.X));
            var overlapHeight = Math.Max(0, Math.Min(first.Y + first.Height, second.Y + second.Height) - Math.Max(first.Y, second.Y));
            var intersection = overlapWidth * overlapHeight;
            var union = first.Width * first.Height + second.Width * second.Height - intersection;
            return union <= 0 ? 0 : intersection / union;
        }

        private sealed class Track(int id, int colorIndex, ObjectDetection detection, long lastSeenTimeMs)
        {
            public int Id { get; } = id;
            public int ColorIndex { get; } = colorIndex;
            public ObjectDetection Detection { get; set; } = detection with { TrackId = id, ColorIndex = colorIndex };
            public long LastSeenTimeMs { get; set; } = lastSeenTimeMs;
            public double VelocityX { get; set; }
            public double VelocityY { get; set; }
        }
    }
}
