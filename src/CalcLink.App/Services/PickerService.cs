using Windows.Storage.Pickers;

namespace CalcLink.App.Services;

/// <summary>WinRT file/folder pickers, initialized with the main window handle (required when unpackaged).</summary>
public sealed class PickerService(WindowService window)
{
    public async Task<IReadOnlyList<string>> PickFilesAsync(IEnumerable<string>? extensions = null)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        AddFilters(picker.FileTypeFilter, extensions);
        window.InitializeWithWindow(picker);
        var files = await picker.PickMultipleFilesAsync();
        return files is null ? [] : files.Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
    }

    public async Task<string?> PickFileAsync(IEnumerable<string>? extensions = null)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.Downloads,
        };
        AddFilters(picker.FileTypeFilter, extensions);
        window.InitializeWithWindow(picker);
        var file = await picker.PickSingleFileAsync();
        return string.IsNullOrEmpty(file?.Path) ? null : file.Path;
    }

    public async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        window.InitializeWithWindow(picker);
        var folder = await picker.PickSingleFolderAsync();
        return string.IsNullOrEmpty(folder?.Path) ? null : folder.Path;
    }

    /// <param name="choices">Display name → extensions (with leading dot), in display order.</param>
    public async Task<string?> PickSaveFileAsync(string suggestedName, IEnumerable<KeyValuePair<string, IList<string>>> choices)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = suggestedName,
        };
        foreach (var (label, exts) in choices)
        {
            picker.FileTypeChoices.Add(label, exts);
        }
        window.InitializeWithWindow(picker);
        var file = await picker.PickSaveFileAsync();
        return string.IsNullOrEmpty(file?.Path) ? null : file.Path;
    }

    private static void AddFilters(IList<string> filter, IEnumerable<string>? extensions)
    {
        var list = extensions?.ToList();
        if (list is null || list.Count == 0)
        {
            filter.Add("*");
            return;
        }
        foreach (var ext in list)
        {
            filter.Add(ext);
        }
    }
}
