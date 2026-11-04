using Avalonia.Controls;

namespace Dashcam.Remote
{
    public partial class ImagePreviewWindow : Window
    {
        private readonly IReadOnlyList<PreviewImageItem> _images;

        public ImagePreviewWindow()
        {
            InitializeComponent();
            _images = [];
            Closed += (_, _) => DisposeImages();
        }

        public ImagePreviewWindow(Window owner, IReadOnlyList<PreviewImageItem> images) : this()
        {
            _images = images;
            Owner = owner;
            Width = Math.Clamp(owner.Bounds.Width * 0.9, 500, owner.Bounds.Width);
            Height = Math.Clamp(owner.Bounds.Height * 0.9, 360, owner.Bounds.Height);
            DataContext = new ImagePreviewViewModel(images);
        }

        private void DisposeImages()
        {
            foreach (var image in _images)
            {
                image.Image.Dispose();
            }
        }
    }
}
