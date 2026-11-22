using System.Collections.ObjectModel;
using CalcLink.App.Helpers;
using CalcLink.Core;

namespace CalcLink.App.ViewModels;

/// <summary>Row in the variables list.</summary>
public sealed class VariableItem(CalcVariable variable)
{
    public CalcVariable Variable { get; } = variable;

    public string Name => Variable.Name;

    /// <summary>Friendly type name ("Program (protected)"); unknown tilibs codes are shown as-is.</summary>
    public string TypeName { get; } = VariableTypeNames.Of(variable);

    /// <summary>Tooltip for the type column: the friendly name plus the raw tilibs code and file extension.</summary>
    public string TypeTip
    {
        get
        {
            var details = new List<string>(2);
            if (Variable.TypeName.Trim() is { Length: > 0 } code && !string.Equals(code, TypeName, StringComparison.OrdinalIgnoreCase))
            {
                details.Add(code);
            }
            if (Variable.FileExtension.Trim().TrimStart('.') is { Length: > 0 } ext)
            {
                details.Add("." + ext);
            }
            return details.Count == 0 ? TypeName : $"{TypeName} ({string.Join(", ", details)})";
        }
    }

    public string SizeText => Format.Bytes((ulong)Variable.Size);

    public bool IsArchived => Variable.Attribute == VariableAttribute.Archived;
    public bool IsLocked => Variable.Attribute is VariableAttribute.Locked or VariableAttribute.Protected;

    /// <summary>Segoe Fluent Icons glyph for the type column.</summary>
    public string TypeGlyph => VariableList.CategoryOf(Variable) switch
    {
        _ when Variable.IsApp => "",          // AllApps
        VariableCategory.Programs => "",      // Code
        VariableCategory.Pictures => "",      // Photo
        _ => "",                              // Document
    };

    /// <summary>Location column: where the variable lives.</summary>
    public string LocationText => IsArchived ? "Archive" : "RAM";

    /// <summary>Lock glyph for locked / protected variables, empty otherwise.</summary>
    public string LockGlyph => IsLocked ? "" : string.Empty;

    public bool HasLock => IsLocked;

    public string LocationTip => Variable.Attribute switch
    {
        VariableAttribute.Archived => "In archive (flash) memory",
        VariableAttribute.Locked => "In RAM, locked",
        VariableAttribute.Protected => "In RAM, protected",
        _ => "In RAM",
    };

    public string AutomationName =>
        $"{Name}, {TypeName}, {SizeText}, {LocationText}"
        + Variable.Attribute switch
        {
            VariableAttribute.Locked => ", locked",
            VariableAttribute.Protected => ", protected",
            _ => string.Empty,
        };
}

/// <summary>A group (folder or type) for the grouped ListView (CollectionViewSource.IsSourceGrouped).</summary>
public sealed class VariableGroup(string title, IEnumerable<VariableItem> items, bool isFolder = true)
    : ObservableCollection<VariableItem>(items)
{
    public string Title { get; } = title;
    public string Header => $"{Title}  ({Count})";

    /// <summary>Folder glyph when grouped by folder, a tag when grouped by type.</summary>
    public string Glyph { get; } = isFolder ? "" : "";
}
