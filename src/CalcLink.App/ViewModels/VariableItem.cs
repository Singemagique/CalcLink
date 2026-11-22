using System.Collections.ObjectModel;
using CalcLink.App.Helpers;
using CalcLink.Core;

namespace CalcLink.App.ViewModels;

/// <summary>Row in the variables list.</summary>
public sealed class VariableItem(CalcVariable variable)
{
    public CalcVariable Variable { get; } = variable;

    public string Name => Variable.Name;
    public string TypeName => Variable.IsApp ? $"{Variable.TypeName} (app)" : Variable.TypeName;
    public string SizeText => Format.Bytes((ulong)Variable.Size);

    public bool IsArchived => Variable.Attribute == VariableAttribute.Archived;
    public bool IsLocked => Variable.Attribute is VariableAttribute.Locked or VariableAttribute.Protected;

    /// <summary>Segoe Fluent Icons glyph for the type column.</summary>
    public string TypeGlyph =>
        Variable.IsApp ? ""
        : Variable.TypeName.Contains("prgm", StringComparison.OrdinalIgnoreCase)
          || Variable.TypeName.Contains("program", StringComparison.OrdinalIgnoreCase)
          || Variable.TypeName.Contains("asm", StringComparison.OrdinalIgnoreCase) ? ""
        : Variable.TypeName.Contains("pic", StringComparison.OrdinalIgnoreCase)
          || Variable.TypeName.Contains("image", StringComparison.OrdinalIgnoreCase) ? ""
        : "";

    public string AttributeGlyph => Variable.Attribute switch
    {
        VariableAttribute.Archived => "",
        VariableAttribute.Locked or VariableAttribute.Protected => "",
        _ => string.Empty,
    };

    public string AttributeText => Variable.Attribute switch
    {
        VariableAttribute.Archived => "Archived",
        VariableAttribute.Locked => "Locked",
        VariableAttribute.Protected => "Protected",
        _ => string.Empty,
    };

    public string AutomationName =>
        $"{Name}, {TypeName}, {SizeText}{(AttributeText.Length > 0 ? ", " + AttributeText : string.Empty)}";
}

/// <summary>A group (folder or type) for the grouped ListView (CollectionViewSource.IsSourceGrouped).</summary>
public sealed class VariableGroup(string title, IEnumerable<VariableItem> items) : ObservableCollection<VariableItem>(items)
{
    public string Title { get; } = title;
    public string Header => $"{Title}  ({Count})";
}
