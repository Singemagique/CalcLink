using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CalcLink.App.Services;

/// <summary>
/// Files passed on the command line ("Open with CalcLink" from Explorer). They are sent once a
/// calculator is connected; until then DevicesPage/CalculatorPage show a banner.
/// </summary>
public sealed partial class PendingFilesService : ObservableObject
{
    private List<string> _files = [];

    public IReadOnlyList<string> Files => _files;

    public int Count => _files.Count;

    public bool HasPending => _files.Count > 0;

    public string Title => $"Pending files to send: {Count}";

    public string Summary => _files.Count switch
    {
        0 => string.Empty,
        1 => $"{Path.GetFileName(_files[0])} — connect to a calculator to send it.",
        _ => $"{string.Join(", ", _files.Take(3).Select(Path.GetFileName))}{(_files.Count > 3 ? ", …" : string.Empty)} — connect to a calculator to send them.",
    };

    /// <summary>Keeps the arguments that are existing files (switches and junk are ignored).</summary>
    public void SetFromCommandLine(IEnumerable<string> args)
    {
        var files = new List<string>();
        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg) || arg.StartsWith('-') || arg.StartsWith('/'))
            {
                continue;
            }
            try
            {
                string full = Path.GetFullPath(arg.Trim('"'));
                if (File.Exists(full) && !files.Contains(full, StringComparer.OrdinalIgnoreCase))
                {
                    files.Add(full);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
            {
                // Not a usable path.
            }
        }
        Replace(files);
    }

    /// <summary>Returns the pending files and clears the list.</summary>
    public IReadOnlyList<string> Take()
    {
        var files = _files;
        Replace([]);
        return files;
    }

    [RelayCommand]
    private void Discard() => Replace([]);

    private void Replace(List<string> files)
    {
        _files = files;
        OnPropertyChanged(string.Empty);
    }
}
