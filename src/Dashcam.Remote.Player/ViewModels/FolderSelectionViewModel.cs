using System.Windows.Input;

namespace Dashcam.Remote.Player;

public sealed class FolderSelectionViewModel
{
    private string? _selectedFolderPath;

    public FolderSelectionViewModel()
    {
        ChooseFolderCommand = new PlayerAsyncCommand(ChooseFolderAsync, () => ChooseFolderRequested is not null);
        OpenFolderCommand = new PlayerRelayCommand(
            _ => FolderSelected?.Invoke(SelectedFolderPath!),
            _ => !string.IsNullOrWhiteSpace(SelectedFolderPath));
    }

    public Func<Task<string?>>? ChooseFolderRequested { get; set; }

    public event Action<string>? FolderSelected;

    public ICommand ChooseFolderCommand { get; }

    public ICommand OpenFolderCommand { get; }

    public string? SelectedFolderPath
    {
        get => _selectedFolderPath;
        private set
        {
            if (_selectedFolderPath == value)
            {
                return;
            }

            _selectedFolderPath = value;
            ((PlayerRelayCommand)OpenFolderCommand).RaiseCanExecuteChanged();
        }
    }

    private async Task ChooseFolderAsync()
    {
        var chooseFolder = ChooseFolderRequested;
        if (chooseFolder is null)
        {
            return;
        }

        SelectedFolderPath = await chooseFolder();
    }
}
