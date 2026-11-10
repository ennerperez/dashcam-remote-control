using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Dashcam.Remote.Player.Models;

namespace Dashcam.Remote.Player.Controls
{
    public sealed class ObjectDetectionOverlay : Control
    {
        public static readonly StyledProperty<IReadOnlyList<ObjectDetection>> FrontDetectionsProperty =
            AvaloniaProperty.Register<ObjectDetectionOverlay, IReadOnlyList<ObjectDetection>>(nameof(FrontDetections), []);
        public static readonly StyledProperty<IReadOnlyList<ObjectDetection>> RearDetectionsProperty =
            AvaloniaProperty.Register<ObjectDetectionOverlay, IReadOnlyList<ObjectDetection>>(nameof(RearDetections), []);
        public static readonly StyledProperty<bool> IsSideBySideProperty =
            AvaloniaProperty.Register<ObjectDetectionOverlay, bool>(nameof(IsSideBySide));
        public static readonly StyledProperty<int> ActiveCameraIndexProperty =
            AvaloniaProperty.Register<ObjectDetectionOverlay, int>(nameof(ActiveCameraIndex));

        private static readonly Color[] s_objectColors =
        [
            Color.Parse("#00E5FF"), Color.Parse("#FFD600"), Color.Parse("#FF4081"), Color.Parse("#76FF03"),
            Color.Parse("#FF9100"), Color.Parse("#D500F9"), Color.Parse("#448AFF"), Color.Parse("#FF1744"),
            Color.Parse("#1DE9B6"), Color.Parse("#B388FF"), Color.Parse("#00E676"), Color.Parse("#FFAB40"),
            Color.Parse("#18FFFF"), Color.Parse("#F50057"), Color.Parse("#64FFDA"), Color.Parse("#EA80FC"),
            Color.Parse("#C6FF00"), Color.Parse("#40C4FF"), Color.Parse("#FF6E40"), Color.Parse("#69F0AE"),
            Color.Parse("#536DFE"), Color.Parse("#F4FF81"), Color.Parse("#FF80AB"), Color.Parse("#84FFFF"),
            Color.Parse("#82B1FF"), Color.Parse("#B9F6CA"), Color.Parse("#FF9E80"), Color.Parse("#A7FFEB"),
            Color.Parse("#FFFF00"), Color.Parse("#CCFF90"), Color.Parse("#80D8FF"), Color.Parse("#7C4DFF"),
            Color.Parse("#FF5252"), Color.Parse("#E1BEE7"), Color.Parse("#B2FF59"), Color.Parse("#FFCCBC"),
            Color.Parse("#80CBC4"), Color.Parse("#CE93D8"), Color.Parse("#9FA8DA"), Color.Parse("#F48FB1"),
            Color.Parse("#DCE775"), Color.Parse("#4DD0E1"), Color.Parse("#FFB74D"), Color.Parse("#AED581"),
            Color.Parse("#BA68C8"), Color.Parse("#4FC3F7"), Color.Parse("#FF8A65"), Color.Parse("#A1887F")
        ];

        public IReadOnlyList<ObjectDetection> FrontDetections
        {
            get => GetValue(FrontDetectionsProperty);
            set => SetValue(FrontDetectionsProperty, value);
        }

        public IReadOnlyList<ObjectDetection> RearDetections
        {
            get => GetValue(RearDetectionsProperty);
            set => SetValue(RearDetectionsProperty, value);
        }

        public bool IsSideBySide
        {
            get => GetValue(IsSideBySideProperty);
            set => SetValue(IsSideBySideProperty, value);
        }

        public int ActiveCameraIndex
        {
            get => GetValue(ActiveCameraIndexProperty);
            set => SetValue(ActiveCameraIndexProperty, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == FrontDetectionsProperty || change.Property == RearDetectionsProperty || change.Property == IsSideBySideProperty || change.Property == ActiveCameraIndexProperty)
            {
                InvalidateVisual();
            }
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (IsSideBySide)
            {
                DrawDetections(context, FrontDetections, 0, Bounds.Width / 2, Bounds.Height);
                DrawDetections(context, RearDetections, Bounds.Width / 2, Bounds.Width / 2, Bounds.Height);
            }
            else
            {
                DrawDetections(context, ActiveCameraIndex == 1 ? RearDetections : FrontDetections, 0, Bounds.Width, Bounds.Height);
            }
        }

        private static void DrawDetections(DrawingContext context, IReadOnlyList<ObjectDetection> detections, double offsetX, double surfaceWidth, double surfaceHeight)
        {
            if (surfaceWidth <= 0)
            {
                return;
            }

            var objectNumber = 0;
            foreach (var detection in detections)
            {
                if (!IsSupportedObject(detection.Label))
                {
                    continue;
                }
                objectNumber++;
                var colorIndex = detection.ColorIndex >= 0
                    ? detection.ColorIndex % s_objectColors.Length
                    : (objectNumber - 1) % s_objectColors.Length;
                var color = s_objectColors[colorIndex];
                var brush = new SolidColorBrush(color);

                var rect = new Rect(
                    offsetX + detection.X * surfaceWidth,
                    detection.Y * surfaceHeight,
                    detection.Width * surfaceWidth,
                    detection.Height * surfaceHeight);
                context.DrawRectangle(null, new Pen(brush, 2.5), rect);

                var vehicleType = detection.Label switch
                {
                    "car" => "Vehiculo",
                    "bus" => "Bus",
                    "motorbike" => "Moto",
                    "bicycle" => "Bicicleta",
                    "person" => "Persona",
                    _ => detection.Label
                };
                var trackNumber = detection.TrackId > 0 ? detection.TrackId : objectNumber;
                var textBrush = color.R * 0.299 + color.G * 0.587 + color.B * 0.114 > 145
                    ? Brushes.Black
                    : Brushes.White;
                var text = new FormattedText(
                    $"{vehicleType}{trackNumber}",
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"),
                    11,
                    textBrush);
                var tagHeight = text.Height + 4;
                var tagY = Math.Max(0, rect.Y - tagHeight);
                var tagRect = new Rect(rect.X, tagY, text.Width + 8, tagHeight);
                context.DrawRectangle(brush, null, tagRect);
                context.DrawText(text, new Point(tagRect.X + 4, tagRect.Y + 2));
            }
        }

        private static bool IsSupportedObject(string label) =>
            label is "bicycle" or "bus" or "car" or "motorbike" or "person";
    }
}
