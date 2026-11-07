using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Dashcam.Remote;

namespace Dashcam.Remote.Player
{
    public sealed class SpeedDistributionChart : Control
    {
        public static readonly StyledProperty<IReadOnlyList<GpsPoint>> PointsProperty =
            AvaloniaProperty.Register<SpeedDistributionChart, IReadOnlyList<GpsPoint>>(nameof(Points), Array.Empty<GpsPoint>());

        public IReadOnlyList<GpsPoint> Points
        {
            get => GetValue(PointsProperty);
            set => SetValue(PointsProperty, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == PointsProperty)
            {
                InvalidateVisual();
            }
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            var width = Bounds.Width;
            var height = Bounds.Height;
            if (width <= 24 || height <= 24)
            {
                return;
            }

            var background = new SolidColorBrush(Color.Parse("#071A3A"));
            context.DrawRectangle(background, null, new Rect(0, 0, width, height));
            var left = 42d;
            var right = width - 10;
            var top = 8d;
            var bottom = height - 28;
            var plotWidth = right - left;
            var plotHeight = bottom - top;
            var gridBrush = new SolidColorBrush(Color.Parse("#315079"));
            var gridPen = new Pen(gridBrush, 1);
            var axisPen = new Pen(new SolidColorBrush(Color.Parse("#A9C9FF")), 1);
            var speeds = Points.Select(point => Math.Max(0, point.SpeedMetersPerSecond * 3.6)).ToArray();
            var maximumSpeed = speeds.Length == 0 ? 5 : Math.Max(5, Math.Ceiling(speeds.Max() / 5) * 5);

            FormattedText Label(string text, IBrush brush) => new(
                text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                9,
                brush);

            for (var line = 0; line <= 4; line++)
            {
                var ratio = line / 4d;
                var y = bottom - plotHeight * ratio;
                context.DrawLine(gridPen, new Point(left, y), new Point(right, y));
                var value = Label($"{ratio * 100:0}%", new SolidColorBrush(Color.Parse("#A9C9FF")));
                context.DrawText(value, new Point(left - value.Width - 5, y - value.Height / 2));
            }

            context.DrawLine(axisPen, new Point(left, top), new Point(left, bottom));
            context.DrawLine(axisPen, new Point(left, bottom), new Point(right, bottom));
            for (var tick = 0; tick <= 4; tick++)
            {
                var ratio = tick / 4d;
                var x = left + plotWidth * ratio;
                context.DrawLine(axisPen, new Point(x, bottom), new Point(x, bottom + 3));
                var text = tick == 4 ? $"{maximumSpeed:0} km/h" : $"{maximumSpeed * ratio:0}";
                var label = Label(text, new SolidColorBrush(Color.Parse("#A9C9FF")));
                var labelX = tick == 4 ? right - label.Width : x - label.Width / 2;
                context.DrawText(label, new Point(labelX, bottom + 5));
            }

            if (speeds.Length == 0)
            {
                return;
            }

            const int bins = 36;
            var mean = speeds.Average();
            var deviation = Math.Sqrt(speeds.Average(speed => Math.Pow(speed - mean, 2)));
            var binWidth = maximumSpeed / bins;
            var threshold = Math.Max(deviation, binWidth);
            var counts = new int[bins];
            foreach (var speed in speeds)
            {
                counts[Math.Clamp((int)(speed / maximumSpeed * bins), 0, bins - 1)]++;
            }

            var maximumCount = Math.Max(1, counts.Max());
            var barWidth = plotWidth / bins;
            for (var index = 0; index < bins; index++)
            {
                var centerSpeed = (index + .5) * binWidth;
                var fill = centerSpeed < mean - threshold
                    ? Color.Parse("#F5C451")
                    : centerSpeed > mean + threshold
                        ? Color.Parse("#F15B64")
                        : Color.Parse("#43D49A");
                var barHeight = counts[index] / (double)maximumCount * plotHeight;
                var rect = new Rect(left + index * barWidth + 1, bottom - barHeight, Math.Max(1, barWidth - 2), barHeight);
                context.DrawRectangle(new SolidColorBrush(fill), null, rect, 2, 2);
            }

            var sigma = threshold;
            double NormalCount(double speed) =>
                speeds.Length * binWidth / (sigma * Math.Sqrt(2 * Math.PI)) * Math.Exp(-Math.Pow(speed - mean, 2) / (2 * sigma * sigma));
            var curveScale = Math.Max(1, Enumerable.Range(0, bins).Max(index => NormalCount((index + .5) * binWidth)));
            var geometry = new StreamGeometry();
            using (var builder = geometry.Open())
            {
                for (var index = 0; index <= bins; index++)
                {
                    var speed = index * binWidth;
                    var x = left + speed / maximumSpeed * plotWidth;
                    var y = bottom - NormalCount(speed) / curveScale * plotHeight;
                    if (index == 0)
                    {
                        builder.BeginFigure(new Point(x, y), false);
                    }
                    else
                    {
                        builder.LineTo(new Point(x, y));
                    }
                }
                builder.EndFigure(false);
            }
            context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.Parse("#EAF2FF")), 2), geometry);

            var thresholdPen = new Pen(new SolidColorBrush(Color.Parse("#A9C9FF")), 1, dashStyle: new DashStyle([3, 3], 0));
            foreach (var speed in new[] { Math.Max(0, mean - threshold), mean, Math.Min(maximumSpeed, mean + threshold) })
            {
                var x = left + speed / maximumSpeed * plotWidth;
                context.DrawLine(thresholdPen, new Point(x, top), new Point(x, bottom));
            }
        }
    }
}
