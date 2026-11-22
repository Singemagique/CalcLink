using CalcLink.Core;

namespace CalcLink.App.Helpers;

/// <summary>Friendly names for tilibs short variable type codes (tifiles_vartype2string).</summary>
public static class VariableTypeNames
{
    // Codes across Z80 (73/82/83/83+/84+/CE, 85/86), 68k (89/92/V200) and Nspire models.
    // Keep in step with the type table in CalcLink.Core/VariableList.cs.
    private static readonly Dictionary<string, string> s_names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PRGM"] = "Program",
        ["PROG"] = "Program",
        ["PPRGM"] = "Program (protected)",
        ["ASM"] = "Assembly program",
        ["FUNC"] = "Function",
        ["MAC"] = "Macro",
        ["APPV"] = "AppVar",
        ["APPL"] = "App",
        ["APP"] = "App",
        ["FLASH"] = "App",
        ["TNS"] = "Document",
        ["TEXT"] = "Text",
        ["REAL"] = "Real number",
        ["CPLX"] = "Complex number",
        ["EXPR"] = "Expression",
        ["CONS"] = "Constant",
        ["CCONS"] = "Complex constant",
        ["LIST"] = "List",
        ["CLIST"] = "Complex list",
        ["DATA"] = "Data",
        ["VECTR"] = "Vector",
        ["CVECT"] = "Complex vector",
        ["MAT"] = "Matrix",
        ["MATRX"] = "Matrix",
        ["CMATR"] = "Complex matrix",
        ["EQU"] = "Y= equation",
        ["STR"] = "String",
        ["STRNG"] = "String",
        ["PIC"] = "Picture",
        ["IMAGE"] = "Image",
        ["FIG"] = "Figure",
        ["LCD"] = "Screen image",
        ["WINDW"] = "Window settings",
        ["RANGE"] = "Window settings",
        ["ZSTO"] = "Zoom settings",
        ["ZRCL"] = "Zoom settings",
        ["TABLE"] = "Table settings",
        ["GDB"] = "Graph settings",
        ["GRP"] = "Group",
        ["BKUP"] = "Backup",
        ["DIR"] = "Folder",
        ["OTH"] = "Other",
    };

    /// <summary>Friendly name for <paramref name="variable"/>'s type; unknown codes fall back to the raw code.</summary>
    public static string Of(CalcVariable variable)
    {
        ArgumentNullException.ThrowIfNull(variable);
        string code = variable.TypeName.Trim();
        if (s_names.TryGetValue(code, out var name))
        {
            return name;
        }
        if (variable.IsApp)
        {
            return "App";
        }
        return code.Length > 0 ? code : "Unknown";
    }
}
