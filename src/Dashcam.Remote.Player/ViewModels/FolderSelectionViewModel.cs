using System.Windows.Input;
using System.ComponentModel;

namespace Dashcam.Remote.Player
{
    public sealed class FolderSelectionViewModel : INotifyPropertyChanged
    {
        private string? _selectedFolderPath;

        public FolderSelectionViewModel()
        {
            ChooseFolderCommand = new PlayerAsyncCommand(ChooseFolderAsync, () => ChooseFolderRequested is not null);
            OpenFolderCommand = new PlayerRelayCommand(
                _ => FolderSelected?.Invoke(SelectedFolderPath!),
                _ => !string.IsNullOrWhiteSpace(SelectedFolderPath));
            CancelCommand = new PlayerRelayCommand(_ => CancelRequested?.Invoke());
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public Func<Task<string?>>? ChooseFolderRequested { get; set; }

        public event Action<string>? FolderSelected;

        public event Action? CancelRequested;

        public ICommand ChooseFolderCommand { get; }

        public ICommand OpenFolderCommand { get; }

        public ICommand CancelCommand { get; }

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
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedFolderPath)));
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
}
