using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace Dashcam.Remote.Player;

public partial class FolderSelectionWindow : Window
{
    private readonly FolderSelectionViewModel _viewModel;

    public FolderSelectionWindow()
    {
        AvaloniaXamlLoader.Load(this);

        _viewModel = new FolderSelectionViewModel
        {
            ChooseFolderRequested = ChooseFolderAsync
        };
        _viewModel.FolderSelected += path => FolderSelected?.Invoke(path);
        DataContext = _viewModel;
    }

    public event Action<string>? FolderSelected;

    private async Task<string?> ChooseFolderAsync()
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider
            ?? throw new InvalidOperationException("Folder picker is unavailable.");
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose recordings folder",
            AllowMultiple = false
        });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }
}
