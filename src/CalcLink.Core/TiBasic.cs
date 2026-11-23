using System.Text;

namespace CalcLink.Core;

/// <summary>
/// TI-BASIC token tables for the TI-83 / TI-83 Plus / TI-84 Plus family, with a detokenizer
/// (stored bytes to the text the calculator's editor shows) and a tokenizer (the reverse).
/// <para>
/// Values were checked against TI's <c>ti83plus.inc</c> (OS 1.x token equates): every one-byte
/// token, the two-byte groups 5C (matrices), 5D (lists), 5E (equations), 60 (pictures), 61 (GDBs),
/// 62 (statistics), 63 (window and finance), 7E (graph formats), AA (strings) and BB 00–CD. The EF
/// group (TI-84 Plus OS 2.x+) includes only the well-known clock and statistics tokens. A token
/// that isn't in the tables is shown as <c>[BB D0]</c> rather than guessed, and the tokenizer reads
/// that form back.
/// </para>
/// </summary>
public static class TiBasic
{
    private static readonly string?[] s_oneByte = BuildOneByte();
    private static readonly Dictionary<byte, Dictionary<byte, string>> s_twoByte = BuildTwoByte();
    private static readonly (string Text, byte[] Bytes)[] s_reverse = BuildReverse();

    /// <summary>The bytes that start a two-byte token.</summary>
    public static bool IsPrefix(byte b) => b is 0x5C or 0x5D or 0x5E or 0x60 or 0x61 or 0x62 or 0x63
        or 0x7E or 0xAA or 0xBB or 0xEF;

    /// <summary>
    /// True for an assembly program: it starts with the "compiled AsmPrgm" token (BB 6D, TI-83 Plus /
    /// TI-84 Plus) or EF 7B (TI-84 Plus CE). Its contents are Z80/eZ80 machine code, not BASIC.
    /// </summary>
    public static bool IsAssembly(ReadOnlySpan<byte> program) =>
        program.Length >= 2 && ((program[0] == 0xBB && program[1] == 0x6D) || (program[0] == 0xEF && program[1] == 0x7B));

    /// <summary>
    /// Turns a program's tokens into text, one line per program line ('\n' separators, no leading ':').
    /// Unknown tokens become <c>[XX]</c> / <c>[XX YY]</c>; a prefix byte at the very end becomes <c>[XX]</c>.
    /// </summary>
    public static string Detokenize(ReadOnlySpan<byte> program)
    {
        var sb = new StringBuilder(program.Length * 2);
        for (int i = 0; i < program.Length; i++)
        {
            byte b = program[i];
            if (IsPrefix(b))
            {
                if (i + 1 >= program.Length)
                {
                    sb.Append($"[{b:X2}]");
                    break;
                }
                byte b2 = program[++i];
                sb.Append(s_twoByte.TryGetValue(b, out var page) && page.TryGetValue(b2, out var text)
                    ? text
                    : $"[{b:X2} {b2:X2}]");
                continue;
            }
            sb.Append(s_oneByte[b] ?? $"[{b:X2}]");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Turns text back into tokens, taking the longest matching token at each position. Lines are
    /// separated by '\n' ('\r' is ignored). Throws <see cref="FormatException"/> for text that no
    /// token produces.
    /// </summary>
    public static byte[] Tokenize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = new List<byte>(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\r')
            {
                i++;
                continue;
            }
            if (TryPlaceholder(text, i, bytes, out int used))
            {
                i += used;
                continue;
            }
            bool matched = false;
            foreach (var (t, b) in s_reverse) // longest first
            {
                if (string.CompareOrdinal(text, i, t, 0, t.Length) == 0)
                {
                    bytes.AddRange(b);
                    i += t.Length;
                    matched = true;
                    break;
                }
            }
            if (!matched)
            {
                throw new FormatException($"No TI-BASIC token for '{text[i]}' at position {i}.");
            }
        }
        return bytes.ToArray();
    }

    // "[BB D0]" or "[26]": a token the tables don't name.
    private static bool TryPlaceholder(string text, int i, List<byte> bytes, out int used)
    {
        used = 0;
        if (text[i] != '[')
        {
            return false;
        }
        int close = text.IndexOf(']', i);
        if (close < 0)
        {
            return false;
        }
        string inner = text[(i + 1)..close];
        string[] parts = inner.Split(' ');
        if (parts.Length is not (1 or 2) || parts.Any(p => p.Length != 2 || !p.All(Uri.IsHexDigit)))
        {
            return false;
        }
        foreach (string p in parts)
        {
            bytes.Add(Convert.ToByte(p, 16));
        }
        used = close - i + 1;
        return true;
    }

    // ------------------------------------------------------------------ tables

    private static string?[] BuildOneByte()
    {
        var t = new string?[256];
        void Set(int start, params string?[] values)
        {
            for (int k = 0; k < values.Length; k++)
            {
                t[start + k] = values[k];
            }
        }

        Set(0x01, "►DMS", "►Dec", "►Frac", "→", "Boxplot", "[", "]", "{", "}", "ʳ", "°", "⁻¹", "²", "ᵀ", "³");
        Set(0x10, "(", ")", "round(", "pxl-Test(", "augment(", "rowSwap(", "row+(", "*row(", "*row+(",
            "max(", "min(", "R►Pr(", "R►Pθ(", "P►Rx(", "P►Ry(", "median(");
        Set(0x20, "randM(", "mean(", "solve(", "seq(", "fnInt(", "nDeriv(", null /* 26: evalF, internal */,
            "fMin(", "fMax(", " ", "\"", ",", "𝑖", "!", "CubicReg ", "QuartReg ");
        for (int d = 0; d < 10; d++)
        {
            t[0x30 + d] = ((char)('0' + d)).ToString();
        }
        Set(0x3A, ".", "ᴇ", " or ", " xor ", ":", "\n", " and ");
        for (int c = 0; c < 26; c++)
        {
            t[0x41 + c] = ((char)('A' + c)).ToString();
        }
        Set(0x5B, "θ");
        Set(0x5F, "prgm"); // 5C–5E and 60–63 are two-byte prefixes
        Set(0x64, "Radian", "Degree", "Normal", "Sci", "Eng", "Float", "=", "<", ">", "≤", "≥", "≠",
            "+", "-", "Ans", "Fix ", "Horiz", "Full", "Func", "Param", "Polar", "Seq",
            "IndpntAuto", "IndpntAsk", "DependAuto", "DependAsk");
        Set(0x7F, "□", "﹢", "·", "*", "/", "Trace", "ClrDraw", "ZStandard", "ZTrig", "ZBox", "Zoom In",
            "Zoom Out", "ZSquare", "ZInteger", "ZPrevious", "ZDecimal", "ZoomStat", "ZoomRcl",
            "PrintScreen", "ZoomSto", "Text(", " nPr ", " nCr ", "FnOn ", "FnOff ", "StorePic ",
            "RecallPic ", "StoreGDB ", "RecallGDB ", "Line(", "Vertical ", "Pt-On(", "Pt-Off(",
            "Pt-Change(", "Pxl-On(", "Pxl-Off(", "Pxl-Change(", "Shade(", "Circle(", "Horizontal ",
            "Tangent(", "DrawInv ", "DrawF ");
        Set(0xAB, "rand", "π", "getKey", "'", "?", "⁻", "int(", "abs(", "det(", "identity(", "dim(",
            "sum(", "prod(", "not(", "iPart(", "fPart(");
        Set(0xBC, "√(", "³√(", "ln(", "ℯ^(", "log(", "₁₀^(", "sin(", "sin⁻¹(", "cos(", "cos⁻¹(", "tan(",
            "tan⁻¹(", "sinh(", "sinh⁻¹(", "cosh(", "cosh⁻¹(", "tanh(", "tanh⁻¹(",
            "If ", "Then", "Else", "While ", "Repeat ", "For(", "End", "Return", "Lbl ", "Goto ",
            "Pause ", "Stop", "IS>(", "DS<(", "Input ", "Prompt ", "Disp ", "DispGraph", "Output(",
            "ClrHome", "Fill(", "SortA(", "SortD(", "DispTable", "Menu(", "Send(", "Get(",
            "PlotsOn ", "PlotsOff ", "ʟ", "Plot1(", "Plot2(", "Plot3(");
        Set(0xF0, "^", "×√", "1-Var Stats ", "2-Var Stats ", "LinReg(a+bx) ", "ExpReg ", "LnReg ",
            "PwrReg ", "Med-Med ", "QuadReg ", "ClrList ", "ClrTable", "Histogram", "xyLine",
            "Scatter", "LinReg(ax+b) ");
        return t;
    }

    private static Dictionary<byte, Dictionary<byte, string>> BuildTwoByte()
    {
        var pages = new Dictionary<byte, Dictionary<byte, string>>();
        Dictionary<byte, string> Page(byte prefix)
        {
            if (!pages.TryGetValue(prefix, out var p))
            {
                pages[prefix] = p = [];
            }
            return p;
        }
        void Set(byte prefix, int start, params string[] values)
        {
            var p = Page(prefix);
            for (int k = 0; k < values.Length; k++)
            {
                p[(byte)(start + k)] = values[k];
            }
        }
        string[] Numbered(string stem) =>
            [.. Enumerable.Range(1, 9).Select(n => stem + n), stem + "0"];
        const string Sub = "₀₁₂₃₄₅₆₇₈₉";

        // Variables.
        Set(0x5C, 0x00, [.. Enumerable.Range(0, 10).Select(n => $"[{(char)('A' + n)}]")]);
        Set(0x5D, 0x00, [.. Enumerable.Range(1, 6).Select(n => $"L{Sub[n]}")]);
        Set(0x5E, 0x10, [.. Enumerable.Range(1, 9).Select(n => $"Y{Sub[n]}"), "Y₀"]);
        Set(0x5E, 0x20, [.. Enumerable.Range(1, 6).SelectMany(n => new[] { $"X{Sub[n]}ᴛ", $"Y{Sub[n]}ᴛ" })]);
        Set(0x5E, 0x40, [.. Enumerable.Range(1, 6).Select(n => $"r{Sub[n]}")]);
        Set(0x5E, 0x80, "u", "v", "w");
        Set(0x60, 0x00, Numbered("Pic"));
        Set(0x61, 0x00, Numbered("GDB"));
        Set(0xAA, 0x00, Numbered("Str"));

        // Statistics results.
        Set(0x62, 0x01, "RegEQ", "n", "x̄", "Σx", "Σx²", "Sx", "σx", "minX", "maxX", "minY", "maxY", "ȳ",
            "Σy", "Σy²", "Sy", "σy", "Σxy", "r", "Med", "Q₁", "Q₃", "a", "b", "c", "d", "e",
            "x₁", "x₂", "x₃", "y₁", "y₂", "y₃", "𝑛", "p", "z", "t", "χ²", "𝐅", "df", "p̂", "p̂₁", "p̂₂",
            "x̄₁", "Sx₁", "n₁", "x̄₂", "Sx₂", "n₂", "Sxp", "lower", "upper", "s", "r²", "R²",
            "Factor df", "Factor SS", "Factor MS", "Error df", "Error SS", "Error MS");

        // Window, table and finance variables.
        Set(0x63, 0x00, "ZXscl", "ZYscl", "Xscl", "Yscl", "u(𝑛Min)", "v(𝑛Min)", "u(𝑛-1)", "v(𝑛-1)",
            "Zu(𝑛Min)", "Zv(𝑛Min)", "Xmin", "Xmax", "Ymin", "Ymax", "Tmin", "Tmax", "θmin", "θmax",
            "ZXmin", "ZXmax", "ZYmin", "ZYmax", "Zθmin", "Zθmax", "ZTmin", "ZTmax", "TblStart",
            "PlotStart", "ZPlotStart", "𝑛Max", "Z𝑛Max", "𝑛Min", "Z𝑛Min", "ΔTbl", "Tstep", "θstep",
            "ZTstep", "Zθstep", "ΔX", "ΔY", "XFact", "YFact", "TblInput", "𝗡", "I%", "PV", "PMT", "FV",
            "P/Y", "C/Y", "w(𝑛Min)", "Zw(𝑛Min)", "PlotStep", "ZPlotStep", "Xres", "ZXres");

        // Graph formats.
        Set(0x7E, 0x00, "Sequential", "Simul", "PolarGC", "RectGC", "CoordOn", "CoordOff", "Connected",
            "Dot", "AxesOn", "AxesOff", "GridOn", "GridOff", "LabelOn", "LabelOff", "Web", "Time",
            "uvAxes", "vwAxes", "uwAxes");

        // Second page (BB).
        Set(0xBB, 0x00, "npv(", "irr(", "bal(", "ΣPrn(", "ΣInt(", "►Nom(", "►Eff(", "dbd(", "lcm(", "gcd(",
            "randInt(", "randBin(", "sub(", "stdDev(", "variance(", "inString(", "normalcdf(", "invNorm(",
            "tcdf(", "χ²cdf(", "𝐅cdf(", "binompdf(", "binomcdf(", "poissonpdf(", "poissoncdf(",
            "geometpdf(", "geometcdf(", "normalpdf(", "tpdf(", "χ²pdf(", "𝐅pdf(", "randNorm(",
            "tvm_Pmt", "tvm_I%", "tvm_PV", "tvm_𝗡", "tvm_FV", "conj(", "real(", "imag(", "angle(",
            "cumSum(", "expr(", "length(", "ΔList(", "ref(", "rref(", "►Rect", "►Polar", "ℯ",
            "SinReg ", "Logistic ", "LinRegTTest ", "ShadeNorm(", "Shade_t(", "Shadeχ²(", "Shade𝐅(",
            "Matr►list(", "List►matr(", "Z-Test(", "T-Test ", "2-SampZTest(", "1-PropZTest(",
            "2-PropZTest(", "χ²-Test(", "ZInterval ", "2-SampZInt(", "1-PropZInt(", "2-PropZInt(",
            "GraphStyle(", "2-SampTTest ", "2-Samp𝐅Test ", "TInterval ", "2-SampTInt ", "SetUpEditor ",
            "Pmt_End", "Pmt_Bgn", "Real", "re^θ𝑖", "a+b𝑖", "ExprOn", "ExprOff", "ClrAllLists",
            "GetCalc(", "DelVar ", "Equ►String(", "String►Equ(", "Clear Entries", "Select(", "ANOVA(",
            "ModBoxplot", "NormProbPlot");
        Set(0xBB, 0x64, "G-T", "ZoomFit", "DiagnosticOn", "DiagnosticOff", "Archive ", "UnArchive ",
            "Asm(", "AsmComp(", "AsmPrgm");
        Set(0xBB, 0x6E, "Á", "À", "Â", "Ä", "á", "à", "â", "ä", "É", "È", "Ê", "Ë", "é", "è", "ê", "ë");
        Set(0xBB, 0x7F, "Ì", "Î", "Ï", "í", "ì", "î", "ï", "Ó", "Ò", "Ô", "Ö", "ó", "ò", "ô", "ö",
            "Ú", "Ù", "Û", "Ü", "ú", "ù", "û", "ü", "Ç", "ç", "Ñ", "ñ", "´", "`", "¨", "¿", "¡",
            "α", "β", "γ", "Δ", "δ", "ε", "λ", "μ", "π", "ρ", "Σ");
        Set(0xBB, 0xAB, "φ", "Ω", "p̂", "χ", "𝐅");
        Set(0xBB, 0xB0, [.. "abcdefghijk".Select(c => c.ToString())]);
        Set(0xBB, 0xBC, [.. "lmnopqrstuvwxyz".Select(c => c.ToString())]);
        Set(0xBB, 0xCB, "σ", "τ", "Í");

        // TI-84 Plus OS 2.x+ (EF): clock and statistics additions.
        Set(0xEF, 0x00, "setDate(", "setTime(", "checkTmr(", "setDtFmt(", "setTmFmt(", "timeCnv(",
            "dayOfWk(", "getDtStr(", "getTmStr(", "getDate", "getTime", "startTmr", "getDtFmt",
            "getTmFmt", "isClockOn", "ClockOff", "ClockOn", "OpenLib(", "ExecLib", "invT(",
            "χ²GOF-Test(", "LinRegTInt ", "Manual-Fit ");
        return pages;
    }

    // Text → bytes, longest text first. Where two tokens show the same text, the one added later
    // wins: one-byte tokens over two-byte ones (π), lowercase letters over statistics variables (a).
    private static (string, byte[])[] BuildReverse()
    {
        var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var oneByteTexts = s_oneByte.Where(t => t is not null).ToHashSet(StringComparer.Ordinal);
        foreach (var (prefix, page) in s_twoByte.OrderBy(p => p.Key == 0xBB ? 1 : 0))
        {
            foreach (var (code, text) in page.OrderBy(p => p.Key))
            {
                // A statistics result whose text is also a run of one-byte tokens (the statistic "R²"
                // is also R then ²) is never what typed text means. Other groups keep theirs: [A],
                // GDB1 and PV are typed as such.
                if (prefix == 0x62 && text.Length > 1
                    && text.EnumerateRunes().All(r => oneByteTexts.Contains(r.ToString())))
                {
                    continue;
                }
                map[text] = [prefix, code];
            }
        }
        for (int b = 0; b < 256; b++)
        {
            if (s_oneByte[b] is { } text)
            {
                map[text] = [(byte)b];
            }
        }
        return map.OrderByDescending(kv => kv.Key.Length).Select(kv => (kv.Key, kv.Value)).ToArray();
    }
}
