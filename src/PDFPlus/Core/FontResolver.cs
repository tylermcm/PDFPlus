using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace PDFPlus.Core;

/// <summary>What a PDF font looks like, mapped onto an installed Windows font family.</summary>
public sealed record FontLook(string Family, int Weight, bool Italic, string PdfName, bool IsSubset, bool IsInstalled)
{
    public static readonly FontLook Default = new("Arial", 400, false, "Helvetica", false, true);

    public bool Bold => Weight >= 600;
}

internal sealed record EmbeddableFont(byte[] Data, string Family);

internal static partial class FontResolver
{
    private const int FlagFixedPitch = 1, FlagSerif = 2, FlagItalic = 1 << 6, FlagForceBold = 1 << 18;

    private static readonly object Gate = new();
    private static Dictionary<string, string>? _installed;

    [GeneratedRegex("^[A-Z]{6}\\+")]
    private static partial Regex SubsetPrefix();

    /// <summary>Works out family, weight and slant from a PDF font's name and descriptor.</summary>
    public static FontLook Describe(string baseName, string familyName, int flags, int weight, int italicAngle, bool embedded)
    {
        if (flags < 0) flags = 0;
        var isSubset = SubsetPrefix().IsMatch(baseName);
        var name = SubsetPrefix().Replace(baseName, "");

        // "TimesNewRomanPS-BoldItalicMT" -> family "TimesNewRoman", style "BoldItalicMT"
        var split = name.IndexOfAny(['-', ',']);
        var familyPart = split > 0 ? name[..split] : name;
        var style = (split > 0 ? name[(split + 1)..] : "").ToLowerInvariant();
        foreach (var suffix in new[] { "PSMT", "MT", "PS" })
        {
            if (familyPart.Length > suffix.Length + 2 && familyPart.EndsWith(suffix, StringComparison.Ordinal))
            {
                familyPart = familyPart[..^suffix.Length];
                break;
            }
        }

        var styleWeight =
            style.Contains("semibold") || style.Contains("demi") ? 600 :
            style.Contains("extrabold") || style.Contains("black") || style.Contains("heavy") ? 900 :
            style.Contains("bold") ? 700 :
            style.Contains("medium") ? 500 :
            style.Contains("semilight") ? 350 :
            style.Contains("light") ? 300 :
            style.Contains("thin") ? 100 : 0;
        // PDFium's weight is often guessed from stem widths (regular Calibri reports 225), so only trust it for bold.
        var resolvedWeight = styleWeight > 0 ? styleWeight : weight >= 600 ? 700 : 400;
        if ((flags & FlagForceBold) != 0) resolvedWeight = Math.Max(resolvedWeight, 700);
        var italic = style.Contains("italic") || style.Contains("oblique") || (flags & FlagItalic) != 0 || italicAngle != 0;

        // An embedded font knows its real family; for the rest, PDFium's family name is just its own substitute.
        var candidates = embedded ? new[] { familyName, familyPart } : [familyPart, familyName];
        string? family = null;
        foreach (var candidate in candidates)
            if ((family = FindInstalled(candidate)) != null) break;
        var installed = family != null;
        family ??= (flags & FlagFixedPitch) != 0 ? "Courier New" : (flags & FlagSerif) != 0 ? "Times New Roman" : "Arial";
        return new FontLook(family, resolvedWeight, italic, name, isSubset, installed);
    }

    /// <summary>
    /// A subset of the best installed font for <paramref name="look"/> that covers every character in <paramref name="text"/>,
    /// ready for FPDFText_LoadFont. Falls back to Segoe UI / Arial when the matching family lacks characters.
    /// </summary>
    public static EmbeddableFont? CreateEmbeddableFont(FontLook look, string text)
    {
        var codepoints = text.EnumerateRunes().Select(r => r.Value).Where(cp => cp >= 0x20).Append(' ').Distinct().ToList();
        var candidates = new[] { look.Family, "Segoe UI", "Arial", "Segoe UI Symbol", "Microsoft YaHei", "Nirmala UI" }
            .Distinct(StringComparer.OrdinalIgnoreCase);

        EmbeddableFont? partial = null;
        foreach (var name in candidates)
        {
            var typeface = new Typeface(new FontFamily(name), look.Italic ? FontStyles.Italic : FontStyles.Normal,
                FontWeight.FromOpenTypeWeight(Math.Clamp(look.Weight, 1, 999)), FontStretches.Normal);
            if (!typeface.TryGetGlyphTypeface(out var glyphs) || !glyphs.FontUri.IsFile) continue;
            var covered = codepoints.All(cp => cp == ' ' || glyphs.CharacterToGlyphMap.ContainsKey(cp));
            if (!covered && partial != null) continue;

            byte[] data;
            try
            {
                data = File.ReadAllBytes(glyphs.FontUri.LocalPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            // WPF puts the face index of a font collection (.ttc) in the URI fragment, e.g. CAMBRIA.TTC#1.
            var faceIndex = int.TryParse(glyphs.FontUri.Fragment.TrimStart('#'), out var face) ? face : 0;
            var subset = TrueTypeSubsetter.Subset(data, faceIndex, codepoints);
            if (subset == null) continue;
            var font = new EmbeddableFont(subset, name);
            if (covered) return font;
            partial ??= font;
        }
        return partial;
    }

    private static string? FindInstalled(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var key = Normalize(name) switch
        {
            "helvetica" or "helv" => "arial",
            "times" or "timesroman" or "tiro" => "timesnewroman",
            "courier" or "cour" => "couriernew",
            var other => other,
        };
        return key.Length > 0 && Installed().TryGetValue(key, out var family) ? family : null;
    }

    private static Dictionary<string, string> Installed()
    {
        lock (Gate)
        {
            if (_installed != null) return _installed;
            var map = new Dictionary<string, string>();
            foreach (var family in Fonts.SystemFontFamilies)
            {
                map.TryAdd(Normalize(family.Source), family.Source);
                foreach (var localized in family.FamilyNames.Values) map.TryAdd(Normalize(localized), family.Source);
            }
            return _installed = map;
        }
    }

    private static string Normalize(string value) => new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}
