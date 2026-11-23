using CalcLink.App.Helpers;
using CalcLink.App.Services;
using CalcLink.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;

namespace CalcLink.App.ViewModels;

/// <summary>
/// The program editor (F1, editing): write or change a TI-BASIC program on the PC, checked as you type,
/// then save it as .8xp or send it to the calculator.
/// </summary>
public sealed partial class EditorViewModel : ObservableObject
{
    private readonly DialogService _dialogs;
    private readonly PickerService _pickers;
    private readonly NotificationService _notifications;
    private readonly CalculatorViewModel _calculator;
    private byte[] _tokens = [];
    private bool _loading;

    public EditorViewModel(DialogService dialogs, PickerService pickers, NotificationService notifications,
        ConnectionService connection, OperationService operations, CalculatorViewModel calculator)
    {
        _dialogs = dialogs;
        _pickers = pickers;
        _notifications = notifications;
        _calculator = calculator;
        Connection = connection;
        connection.SessionChanged += (_, _) => UpdateCommands();
        operations.BusyChanged += (_, _) => UpdateCommands();
        // Text before Name: changing the name re-checks the program text.
        Text = string.Empty;
        Name = "NEWPROG";
        Source = "New program";
        IsDirty = false;
        Validate();
    }

    public ConnectionService Connection { get; }

    [ObservableProperty]
    public partial string Name { get; set; }

    /// <summary>The program text as the TextBox holds it (WinUI uses '\r' line breaks).</summary>
    [ObservableProperty]
    public partial string Text { get; set; }

    [ObservableProperty]
    public partial bool IsProtected { get; set; }

    /// <summary>Where the program came from, for the header ("From the calculator", a file path, ...).</summary>
    [ObservableProperty]
    public partial string Source { get; private set; }

    [ObservableProperty]
    public partial bool IsDirty { get; private set; }

    [ObservableProperty]
    public partial string ErrorText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string SizeText { get; private set; } = string.Empty;

    /// <summary>The loaded program has tokens that look like others (e.g. the statistic "e" and the letter e).</summary>
    [ObservableProperty]
    public partial bool ShowLookalikeWarning { get; set; } // the InfoBar's close button clears it

    public bool HasError => ErrorText.Length > 0;

    /// <summary>Index in <see cref="Text"/> where tokenizing failed, or -1.</summary>
    public int ErrorIndex { get; private set; } = -1;

    public bool NameIsValid => TiBasic.IsValidProgramName(Name);

    public bool CanSave => !HasError && NameIsValid;

    public bool CanSend => CanSave && _calculator.CanSend;

    public string SendTip => Connection.IsConnected
        ? $"Send {Name} to the {Connection.ModelName}"
        : "Connect a calculator on the Devices page to send the program";

    partial void OnNameChanged(string value)
    {
        if (!_loading)
        {
            IsDirty = true;
        }
        Validate();
    }

    partial void OnTextChanged(string value)
    {
        if (!_loading)
        {
            IsDirty = true;
        }
        Validate();
    }

    partial void OnIsProtectedChanged(bool value)
    {
        if (!_loading)
        {
            IsDirty = true;
        }
    }

    /// <summary>The text with '\n' line breaks, as the tokenizer and files use.</summary>
    private string Normalized => (Text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');

    private void Validate()
    {
        string text = Normalized;
        if (!NameIsValid)
        {
            ErrorText = "The name must be 1 to 8 characters, A–Z, 0–9 and θ, starting with a letter or θ.";
            ErrorIndex = -1;
            _tokens = [];
        }
        else if (TiBasic.TryTokenize(text, typingShortcuts: true, out byte[] tokens, out int at))
        {
            ErrorText = string.Empty;
            ErrorIndex = -1;
            _tokens = tokens;
        }
        else
        {
            var (line, column) = Position(text, at);
            ErrorText = $"Line {line}, column {column}: \"{text[at]}\" isn't part of any TI-BASIC command or character.";
            ErrorIndex = at;
            _tokens = [];
        }
        int lines = text.Length == 0 ? 0 : text.Count(c => c == '\n') + 1;
        SizeText = HasError ? $"{lines} line{(lines == 1 ? "" : "s")}" : $"{lines} line{(lines == 1 ? "" : "s")} · {Format.Bytes(_tokens.Length + 2)}";
        UpdateCommands();
    }

    /// <summary>1-based line and column of <paramref name="index"/>.</summary>
    public static (int Line, int Column) Position(string text, int index)
    {
        int line = 1, start = 0;
        for (int i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] is '\n' or '\r')
            {
                line++;
                start = i + 1;
            }
        }
        return (line, index - start + 1);
    }

    private void UpdateCommands()
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(NameIsValid));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(SendTip));
        SaveAsCommand.NotifyCanExecuteChanged();
        SendCommand.NotifyCanExecuteChanged();
    }

    // --- Loading ---------------------------------------------------------------------------------

    /// <summary>
    /// Opens a program in the editor (from the viewer, a file, ...). Asks first when there are unsaved
    /// changes. Returns false if the user kept the current program, or it's assembly.
    /// </summary>
    public async Task<bool> LoadAsync(string name, byte[] tokens, bool isProtected, string source)
    {
        if (TiBasic.IsAssembly(tokens))
        {
            await _dialogs.ShowMessageAsync("Can't edit assembly",
                $"{name} is an assembly program (machine code), so it can't be edited as text.");
            return false;
        }
        if (!await ConfirmDiscardAsync())
        {
            return false;
        }
        string text = TiBasic.Detokenize(tokens);
        _loading = true;
        Name = TiBasic.IsValidProgramName(name) ? name : "NEWPROG";
        Text = text.Replace('\n', '\r');
        IsProtected = isProtected;
        Source = source;
        _loading = false;
        IsDirty = false;
        // Detokenize then tokenize must give the same bytes; if not, saving would change the program.
        ShowLookalikeWarning = !TiBasic.Tokenize(text).AsSpan().SequenceEqual(tokens);
        Validate();
        return true;
    }

    private async Task<bool> ConfirmDiscardAsync() =>
        !IsDirty || await _dialogs.ConfirmAsync("Discard your changes?",
            $"{Name} has changes that aren't saved or sent. Discard them?", "Discard", destructive: true);

    [RelayCommand]
    private async Task NewAsync()
    {
        if (!await ConfirmDiscardAsync())
        {
            return;
        }
        _loading = true;
        Name = "NEWPROG";
        Text = string.Empty;
        IsProtected = false;
        Source = "New program";
        _loading = false;
        IsDirty = false;
        ShowLookalikeWarning = false;
        Validate();
    }

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        string? path;
        try
        {
            path = await _pickers.PickFileAsync([".8xp", ".83p"]);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Could not open the file picker", ex);
            return;
        }
        if (path is null)
        {
            return;
        }
        TiVarEntry? program;
        try
        {
            program = TiVarFile.Read(path).FirstOrDefault(e => e.IsProgram);
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            await _dialogs.ShowMessageAsync("Can't open this file", $"{Path.GetFileName(path)}: {ex.Message}");
            return;
        }
        if (program?.ProgramTokens is not { } tokens)
        {
            await _dialogs.ShowMessageAsync("No program in this file", $"{Path.GetFileName(path)} doesn't hold a program.");
            return;
        }
        await LoadAsync(program.Name, tokens, program.TypeId == 0x06, path);
    }

    // --- Saving and sending --------------------------------------------------------------------------

    private byte[] FileBytes() => TiVarFile.WriteProgram(Name, _tokens, IsProtected);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsAsync()
    {
        string? path;
        try
        {
            path = await _pickers.PickSaveFileAsync(Name, [new("TI-83 Plus / TI-84 Plus program (.8xp)", [".8xp"])]);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Could not open the save dialog", ex);
            return;
        }
        if (path is null)
        {
            return;
        }
        try
        {
            await File.WriteAllBytesAsync(path, FileBytes());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _dialogs.ShowErrorAsync("Couldn't save the program", ex);
            return;
        }
        Source = path;
        IsDirty = false;
        _notifications.Post("Saved", $"Saved {Name} to {Path.GetFileName(path)}.", InfoBarSeverity.Success);
    }

    /// <summary>Sends the program through the Calculator page's send path, so a name clash asks first.</summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        string folder = Path.Combine(Path.GetTempPath(), "CalcLink", "editor", Guid.NewGuid().ToString("N"));
        string file = Path.Combine(folder, Name.Replace('θ', '_') + ".8xp");
        try
        {
            Directory.CreateDirectory(folder);
            await File.WriteAllBytesAsync(file, FileBytes());
            await _calculator.SendPathsAsync([file]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _dialogs.ShowErrorAsync("Couldn't prepare the program", ex);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }
}
