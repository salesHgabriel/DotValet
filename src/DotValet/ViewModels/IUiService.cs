namespace DotValet.ViewModels;

/// <summary>Operações que dependem da janela (diálogos, área de transferência).</summary>
public interface IUiService
{
    Task<string?> PickFolderAsync(string title);
    Task<string?> PickProjectFileAsync(string title);
    Task CopyToClipboardAsync(string text);
    Task<bool> ConfirmAsync(string title, string message);
}
