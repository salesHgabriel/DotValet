using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using DotValet.ViewModels;

namespace DotValet.Views;

public partial class MainWindow : Window, IUiService
{
    public MainWindow()
    {
        InitializeComponent();

        // Mantém o log rolado até o fim enquanto o processo escreve.
        LogBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
                LogBox.CaretIndex = LogBox.Text?.Length ?? 0;
        };
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        return result.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> PickProjectFileAsync(string title)
    {
        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Projeto .NET") { Patterns = ["*.csproj", "*.fsproj", "*.vbproj"] },
            ],
        });
        return result.FirstOrDefault()?.TryGetLocalPath();
    }

    public Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var ok = new Button { Content = "Confirmar", IsDefault = true };
        var cancel = new Button { Content = "Cancelar", IsCancel = true };
        ok.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, ok },
                },
            },
        };
        return dialog.ShowDialog<bool>(this);
    }

    public async Task CopyToClipboardAsync(string text)
    {
        if (Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }
}
