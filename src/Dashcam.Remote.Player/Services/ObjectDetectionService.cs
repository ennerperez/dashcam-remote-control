using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Transforms.Onnx;
using Dashcam.Remote.Player.Models;
using SkiaSharp;

namespace Dashcam.Remote.Player.Services
{
    public sealed class ObjectDetectionService : IDisposable
    {
        private const int ImageSize = 416;
        private const int GridSize = 13;
        private const int BoxCount = 5;
        private const int ClassCount = 20;
        private const int ValuesPerBox = ClassCount + 5;
        private const float ConfidenceThreshold = 0.3f;
        private const float OverlapThreshold = 0.45f;
        private static readonly float[] s_anchors = [1.08f, 1.19f, 3.42f, 4.41f, 6.63f, 11.38f, 9.42f, 5.11f, 16.62f, 10.52f];
        private static readonly string[] s_labels = ["aeroplane", "bicycle", "bird", "boat", "bottle", "bus", "car", "cat", "chair", "cow", "dining table", "dog", "horse", "motorbike", "person", "potted plant", "sheep", "sofa", "train", "tv monitor"];

        private readonly MLContext _mlContext = new();
        private readonly SemaphoreSlim _inferenceLock = new(1, 1);
        private readonly Lazy<ITransformer> _model;
        private bool _disposed;

        public ObjectDetectionService()
        {
            _model = new Lazy<ITransformer>(LoadModel, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public async Task<IReadOnlyList<ObjectDetection>> DetectAsync(string imagePath, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _inferenceLock.WaitAsync(cancellationToken);
            try
            {
                return await Task.Run(() => Detect(imagePath, cancellationToken), cancellationToken);
            }
            finally
            {
                _inferenceLock.Release();
            }
        }

        private IReadOnlyList<ObjectDetection> Detect(string imagePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = _mlContext.Data.LoadFromEnumerable([new ImageInput { Image = LoadImagePixels(imagePath, cancellationToken) }]);
            var scored = _model.Value.Transform(input);
            var output = scored.GetColumn<float[]>("grid").FirstOrDefault();
            return output is null ? [] : ParseOutput(output);
        }

        private static float[] LoadImagePixels(string imagePath, CancellationToken cancellationToken)
        {
            using var source = SKBitmap.Decode(imagePath)
                               ?? throw new InvalidDataException($"The video frame at '{imagePath}' could not be decoded.");
            using var resized = new SKBitmap(ImageSize, ImageSize, SKColorType.Rgba8888, SKAlphaType.Opaque);
            using var canvas = new SKCanvas(resized);
            canvas.DrawBitmap(source, new SKRect(0, 0, ImageSize, ImageSize));
            canvas.Flush();

            var planeSize = ImageSize * ImageSize;
            var pixels = new float[planeSize * 3];
            for (var y = 0; y < ImageSize; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var x = 0; x < ImageSize; x++)
                {
                    var color = resized.GetPixel(x, y);
                    var offset = y * ImageSize + x;
                    pixels[offset] = color.Red;
                    pixels[planeSize + offset] = color.Green;
                    pixels[planeSize * 2 + offset] = color.Blue;
                }
            }

            return pixels;
        }

        private OnnxTransformer LoadModel()
        {
            var modelPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Models", "tinyyolov2-7.onnx");
            if (!File.Exists(modelPath))
            {
                modelPath = Path.Combine(AppContext.BaseDirectory, "tinyyolov2-7.onnx");
            }

            if (!File.Exists(modelPath))
            {
                throw new FileNotFoundException("The Tiny YOLOv2 object detection model was not found.", modelPath);
            }

            var pipeline = _mlContext.Transforms.ApplyOnnxModel(modelFile: modelPath, outputColumnNames: ["grid"], inputColumnNames: ["image"]);
            var emptyData = _mlContext.Data.LoadFromEnumerable(Array.Empty<ImageInput>());
            return pipeline.Fit(emptyData);
        }

        internal static IReadOnlyList<ObjectDetection> ParseOutput(ReadOnlySpan<float> output)
        {
            var expectedLength = BoxCount * ValuesPerBox * GridSize * GridSize;
            if (output.Length != expectedLength)
            {
                throw new ArgumentException($"Expected {expectedLength} model values, received {output.Length}.", nameof(output));
            }

            var boxes = new List<ObjectDetection>();
            for (var row = 0; row < GridSize; row++)
            {
                for (var column = 0; column < GridSize; column++)
                {
                    for (var box = 0; box < BoxCount; box++)
                    {
                        var channel = box * ValuesPerBox;
                        var x = Sigmoid(output[GetOffset(row, column, channel)]);
                        var y = Sigmoid(output[GetOffset(row, column, channel + 1)]);
                        var width = MathF.Exp(output[GetOffset(row, column, channel + 2)]) * s_anchors[box * 2];
                        var height = MathF.Exp(output[GetOffset(row, column, channel + 3)]) * s_anchors[box * 2 + 1];
                        var objectConfidence = Sigmoid(output[GetOffset(row, column, channel + 4)]);
                        if (objectConfidence < ConfidenceThreshold)
                        {
                            continue;
                        }

                        var classScores = new float[ClassCount];
                        var maxClassScore = float.NegativeInfinity;
                        var maxClassIndex = 0;
                        for (var classIndex = 0; classIndex < ClassCount; classIndex++)
                        {
                            classScores[classIndex] = output[GetOffset(row, column, channel + 5 + classIndex)];
                            maxClassScore = Math.Max(maxClassScore, classScores[classIndex]);
                            if (classScores[classIndex] >= maxClassScore)
                            {
                                maxClassIndex = classIndex;
                            }
                        }

                        var expTotal = classScores.Sum(score => MathF.Exp(score - maxClassScore));
                        var classProbability = MathF.Exp(classScores[maxClassIndex] - maxClassScore) / expTotal;
                        var confidence = objectConfidence * classProbability;
                        if (confidence < ConfidenceThreshold)
                        {
                            continue;
                        }

                        var centerX = (column + x) / GridSize;
                        var centerY = (row + y) / GridSize;
                        var normalizedWidth = width / GridSize;
                        var normalizedHeight = height / GridSize;
                        var left = Math.Clamp(centerX - normalizedWidth / 2, 0, 1);
                        var top = Math.Clamp(centerY - normalizedHeight / 2, 0, 1);
                        var right = Math.Clamp(centerX + normalizedWidth / 2, 0, 1);
                        var bottom = Math.Clamp(centerY + normalizedHeight / 2, 0, 1);
                        boxes.Add(new ObjectDetection(s_labels[maxClassIndex], confidence, left, top, right - left, bottom - top));
                    }
                }
            }

            var selected = new List<ObjectDetection>();
            foreach (var candidate in boxes.OrderByDescending(item => item.Confidence))
            {
                if (selected.Count >= 12)
                {
                    break;
                }

                if (selected.Any(item => item.Label == candidate.Label && IntersectionOverUnion(item, candidate) > OverlapThreshold))
                {
                    continue;
                }

                selected.Add(candidate);
            }

            return selected;
        }

        private static int GetOffset(int row, int column, int channel) => channel * GridSize * GridSize + row * GridSize + column;

        private static float Sigmoid(float value) => 1f / (1f + MathF.Exp(-value));

        private static float IntersectionOverUnion(ObjectDetection first, ObjectDetection second)
        {
            var overlapWidth = Math.Max(0, Math.Min(first.X + first.Width, second.X + second.Width) - Math.Max(first.X, second.X));
            var overlapHeight = Math.Max(0, Math.Min(first.Y + first.Height, second.Y + second.Height) - Math.Max(first.Y, second.Y));
            var intersection = overlapWidth * overlapHeight;
            var union = first.Width * first.Height + second.Width * second.Height - intersection;
            return union <= 0 ? 0 : intersection / union;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        private sealed class ImageInput
        {
            [ColumnName("image")]
            [VectorType(1, 3, ImageSize, ImageSize)]
            public float[] Image { get; set; } = [];
        }
    }
}
