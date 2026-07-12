using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Daqifi.Desktop.DialogService;

namespace Daqifi.Desktop.Services;

/// <summary>
/// Win32 SaveFileDialog / OpenFileDialog / WinForms FolderBrowserDialog →
/// TopLevel.StorageProvider async pickers (hal: file_pickers). Owner windows
/// resolve through the ONE DialogService registry (dialog_service mechanism)
/// so headless/UI-automation suites can drive pickers through the service.
/// </summary>
public interface IFilePickerService
{
    Task<string?> SaveFileAsync(
        object ownerViewModel,
        string title,
        string? suggestedFileName = null,
        string? defaultExtension = null,
        IReadOnlyList<FilePickerFileType>? fileTypes = null);

    Task<string?> OpenFileAsync(
        object ownerViewModel,
        string title,
        IReadOnlyList<FilePickerFileType>? fileTypes = null);

    Task<string?> OpenFolderAsync(object ownerViewModel, string title);
}

public class FilePickerService : IFilePickerService
{
    private readonly IDialogService _dialogService;

    public FilePickerService(IDialogService dialogService)
    {
        _dialogService = dialogService;
    }

    public async Task<string?> SaveFileAsync(
        object ownerViewModel,
        string title,
        string? suggestedFileName = null,
        string? defaultExtension = null,
        IReadOnlyList<FilePickerFileType>? fileTypes = null)
    {
        var owner = _dialogService.FindOwnerWindow(ownerViewModel);
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = defaultExtension,
            FileTypeChoices = fileTypes,
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> OpenFileAsync(
        object ownerViewModel,
        string title,
        IReadOnlyList<FilePickerFileType>? fileTypes = null)
    {
        var owner = _dialogService.FindOwnerWindow(ownerViewModel);
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = fileTypes,
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> OpenFolderAsync(object ownerViewModel, string title)
    {
        var owner = _dialogService.FindOwnerWindow(ownerViewModel);
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
