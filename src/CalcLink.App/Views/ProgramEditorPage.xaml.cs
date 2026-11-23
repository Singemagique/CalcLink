using CalcLink.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace CalcLink.App.Views;

public sealed partial class ProgramEditorPage : Page
{
    // Text the symbol buttons insert, with a spoken name for screen readers.
    private static readonly (string Text, string Name)[] Symbols =
    [
        ("→", "Store arrow"), ("≤", "Less than or equal"), ("≥", "Greater than or equal"), ("≠", "Not equal"),
        ("²", "Squared"), ("³", "Cubed"), ("⁻¹", "Inverse"), ("√(", "Square root"), ("³√(", "Cube root"),
        ("⁻", "Negative"), ("θ", "Theta"), ("π", "Pi"), ("ℯ", "e constant"), ("ᴇ", "Times ten to the"),
        ("𝑖", "Imaginary i"), ("ʟ", "List name"), ("L₁", "List 1"), ("L₂", "List 2"), ("Y₁", "Y1"),
        ("Str1", "String 1"), ("[A]", "Matrix A"),
    ];

    public ProgramEditorPage()
    {
        ViewModel = App.GetService<EditorViewModel>();
        InitializeComponent();
        foreach (var (text, name) in Symbols)
        {
            var b = new Button { Content = text, Tag = text, Style = (Style)Resources["SymbolButtonStyle"] };
            AutomationProperties.SetName(b, $"Insert {name}");
            ToolTipService.SetToolTip(b, $"Insert {text}");
            b.Click += OnSymbolClick;
            SymbolBar.Children.Add(b);
        }
        UpdateCaret();
    }

    public EditorViewModel ViewModel { get; }

    private void OnSymbolClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string text })
        {
            return;
        }
        int start = CodeBox.SelectionStart;
        string current = CodeBox.Text;
        CodeBox.Text = current[..start] + text + current[(start + CodeBox.SelectionLength)..];
        CodeBox.SelectionStart = start + text.Length;
        CodeBox.SelectionLength = 0;
        CodeBox.Focus(FocusState.Programmatic);
    }

    private void OnGoToErrorClick(object sender, RoutedEventArgs e)
    {
        int at = ViewModel.ErrorIndex;
        if (at < 0 || at > CodeBox.Text.Length)
        {
            return;
        }
        CodeBox.Focus(FocusState.Programmatic);
        CodeBox.SelectionStart = at;
        CodeBox.SelectionLength = Math.Min(1, CodeBox.Text.Length - at);
    }

    private void OnCodeSelectionChanged(object sender, RoutedEventArgs e) => UpdateCaret();

    private void UpdateCaret()
    {
        var (line, column) = EditorViewModel.Position(CodeBox.Text, CodeBox.SelectionStart);
        CaretText.Text = $"Line {line}, column {column}";
    }
}
