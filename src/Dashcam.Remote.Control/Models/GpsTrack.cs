using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Dashcam.Remote
{
    public sealed record GpsPoint(DateTimeOffset Time, double Latitude, double Longitude, double ElevationMeters, double SpeedMetersPerSecond);

    public static class GpsTrack
    {
        private static readonly XNamespace s_gpxNamespace = "http://www.topografix.com/GPX/1/1";
        private static readonly byte[] s_ligoGpsMarker = Encoding.ASCII.GetBytes("SKIPLIGOGPSINFO");
        private const int TsPacketSize = 188;
        private const int TrailerHeaderSize = 28;
        private const int TrailerRecordSize = 132;
        private const int MaximumTrailerSize = 64 * 1024 * 1024;
        private static readonly System.Text.RegularExpressions.Regex s_ligoGpsRecord = new(
            @"^(?<time>\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2}) (?<latHem>[NS]):(?<lat>[+-]?[\d.]+) (?<lonHem>[EW]):(?<lon>[+-]?[\d.]+) (?<speed>[\d.]+) km/h x:(?<x>[+-]?[\d.]+) y:(?<y>[+-]?[\d.]+) z:(?<z>[+-]?[\d.]+) A:(?<heading>[+-]?[\d.]+) H:(?<altitude>[+-]?[\d.]+) M:(?<variation>[+-]?[\d.]+)$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

        public static IReadOnlyList<GpsPoint> ReadFromTs(string path)
        {
            using var stream = File.OpenRead(path);
            var scanLength = (int)Math.Min(stream.Length, MaximumTrailerSize);
            if (scanLength < TrailerHeaderSize)
            {
                return [];
            }

            var tail = new byte[scanLength];
            stream.Position = stream.Length - scanLength;
            stream.ReadExactly(tail);

            var markerOffset = tail.AsSpan().LastIndexOf(s_ligoGpsMarker);
            if (markerOffset < 4)
            {
                return [];
            }

            var trailerOffset = markerOffset - 4;
            var trailer = tail.AsSpan(trailerOffset);
            var declaredLength = ReadUInt32BigEndian(trailer);
            if (declaredLength != trailer.Length || !trailer.Slice(4, 4).SequenceEqual("SKIP"u8))
            {
                return [];
            }

            if (!trailer.Slice(8, 11).SequenceEqual("LIGOGPSINFO"u8))
            {
                return [];
            }

            var count = ReadUInt32BigEndian(trailer.Slice(24, 4));
            var recordsLength = checked((long)count * TrailerRecordSize);
            var endOfRecords = checked(TrailerHeaderSize + recordsLength);
            if (endOfRecords + 8 != trailer.Length ||
                !trailer.Slice((int)endOfRecords, 4).SequenceEqual("####"u8) ||
                ReadUInt32BigEndian(trailer.Slice((int)endOfRecords + 4, 4)) != declaredLength)
            {
                return [];
            }

            var result = new List<GpsPoint>((int)Math.Min(count, int.MaxValue));
            for (var index = 0; index < count; index++)
            {
                var recordOffset = checked(TrailerHeaderSize + (int)index * TrailerRecordSize + 4);
                var row = trailer.Slice(recordOffset, TrailerRecordSize - 4);
                var nullIndex = row.IndexOf((byte)0);
                if (nullIndex >= 0)
                {
                    row = row[..nullIndex];
                }

                var text = Encoding.ASCII.GetString(row).Trim();
                var match = s_ligoGpsRecord.Match(text);
                if (!match.Success)
                {
                    continue;
                }

                var latitude = ParseNumber(match.Groups["lat"].Value);
                var longitude = ParseNumber(match.Groups["lon"].Value);
                if (match.Groups["latHem"].Value == "S")
                {
                    latitude = -latitude;
                }

                if (match.Groups["lonHem"].Value == "W")
                {
                    longitude = -longitude;
                }

                var speedKmh = ParseNumber(match.Groups["speed"].Value);
                var altitude = ParseNumber(match.Groups["altitude"].Value);

                // Zeroed telemetry rows mean the GPS receiver has no fix.
                if (latitude == 0 && longitude == 0 && speedKmh == 0 && altitude == 0)
                {
                    continue;
                }

                if (!DateTimeOffset.TryParseExact(match.Groups["time"].Value, "yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeLocal, out var time))
                {
                    continue;
                }

                result.Add(new GpsPoint(time, latitude, longitude, altitude, speedKmh / 3.6));
            }

            return result.OrderBy(point => point.Time).ToArray();
        }

        public static IReadOnlyList<GpsPoint> Read(string path)
        {
            var xml = File.ReadAllText(path);
            // Some DVPlayer exports contain a whitespace after "<?" in the XML declaration.
            xml = System.Text.RegularExpressions.Regex.Replace(xml, @"^\s*<\?\s*xml[^?]*\?>", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var document = XDocument.Parse(xml, LoadOptions.None);
            var points = document.Descendants().Where(element => element.Name.LocalName == "trkpt");
            var result = new List<GpsPoint>();
            foreach (var point in points)
            {
                if (!double.TryParse((string?)point.Attribute("lat"), NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude) ||
                    !double.TryParse((string?)point.Attribute("lon"), NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude))
                {
                    continue;
                }

                var timeText = ChildValue(point, "time");
                if (!DateTimeOffset.TryParse(timeText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var time))
                {
                    continue;
                }

                var elevation = ParseNumber(ChildValue(point, "ele"));
                var speed = ParseNumber(ChildValue(point, "speed"));
                result.Add(new GpsPoint(time, latitude, longitude, elevation, speed));
            }

            var ordered = result.OrderBy(point => point.Time).ToArray();
            var calculated = new GpsPoint[ordered.Length];
            for (var index = 0; index < ordered.Length; index++)
            {
                var speedMetersPerSecond = 0d;
                if (index > 0)
                {
                    var elapsedSeconds = (ordered[index].Time - ordered[index - 1].Time).TotalSeconds;
                    if (elapsedSeconds > 0)
                    {
                        speedMetersPerSecond = DistanceMeters(ordered[index - 1], ordered[index]) / elapsedSeconds;
                    }
                }
                calculated[index] = ordered[index] with { SpeedMetersPerSecond = speedMetersPerSecond };
            }
            return calculated;
        }

        public static async Task WriteAsync(Stream destination, IReadOnlyList<GpsPoint> points, CancellationToken cancellationToken = default)
        {
            var root = new XElement(s_gpxNamespace + "gpx",
                new XAttribute("version", "1.1"),
                new XAttribute("creator", "Dashcam Remote Control"),
                new XElement(s_gpxNamespace + "trk",
                    new XElement(s_gpxNamespace + "name", "Dashcam GPS track"),
                    new XElement(s_gpxNamespace + "trkseg", points.Select(point =>
                        new XElement(s_gpxNamespace + "trkpt",
                            new XAttribute("lat", point.Latitude.ToString("F6", CultureInfo.InvariantCulture)),
                            new XAttribute("lon", point.Longitude.ToString("F6", CultureInfo.InvariantCulture)),
                            new XElement(s_gpxNamespace + "ele", point.ElevationMeters.ToString("F1", CultureInfo.InvariantCulture)),
                            new XElement(s_gpxNamespace + "time", point.Time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)),
                            new XElement(s_gpxNamespace + "speed", point.SpeedMetersPerSecond.ToString("F2", CultureInfo.InvariantCulture)))))));
            await using var writer = new StreamWriter(destination, new UTF8Encoding(false), leaveOpen: true);
            await writer.WriteAsync(new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root).ToString(SaveOptions.None).AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
        }

        private static string? ChildValue(XElement element, string localName) =>
            element.Elements().FirstOrDefault(child => child.Name.LocalName == localName)?.Value;

        private static double ParseNumber(string? value) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

        private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> value) =>
            (uint)(value[0] << 24 | value[1] << 16 | value[2] << 8 | value[3]);

        private static double DistanceMeters(GpsPoint first, GpsPoint second)
        {
            const double earthRadiusMeters = 6_371_008.8;
            var latitude1 = DegreesToRadians(first.Latitude);
            var latitude2 = DegreesToRadians(second.Latitude);
            var deltaLatitude = latitude2 - latitude1;
            var deltaLongitude = DegreesToRadians(second.Longitude - first.Longitude);
            var value = Math.Pow(Math.Sin(deltaLatitude / 2), 2) + Math.Cos(latitude1) * Math.Cos(latitude2) * Math.Pow(Math.Sin(deltaLongitude / 2), 2);
            return earthRadiusMeters * 2 * Math.Asin(Math.Sqrt(value));
        }

        private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180;
    }
}
