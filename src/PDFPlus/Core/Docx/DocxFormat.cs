using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;

namespace PDFPlus.Core.Docx;

/// <summary>Character formatting, with every field optional so a style and a direct override can be merged.</summary>
internal readonly record struct RunFormat(
    string? Font, double? Size, bool? Bold, bool? Italic, bool? Underline, bool? Strike,
    Color? Colour, Color? Highlight, bool? Caps, bool? SmallCaps)
{
    /// <summary>This format laid over <paramref name="under"/>: anything set here wins.</summary>
    public RunFormat Over(RunFormat under) => new(
        Font ?? under.Font, Size ?? under.Size, Bold ?? under.Bold, Italic ?? under.Italic,
        Underline ?? under.Underline, Strike ?? under.Strike, Colour ?? under.Colour,
        Highlight ?? under.Highlight, Caps ?? under.Caps, SmallCaps ?? under.SmallCaps);
}

/// <summary>Paragraph formatting, likewise optional throughout so styles can be layered.</summary>
internal readonly record struct ParagraphFormat(
    TextAlignment? Alignment, double? SpaceBefore, double? SpaceAfter, double? LineHeight,
    double? IndentLeft, double? IndentRight, double? IndentFirstLine, int? OutlineLevel, bool? KeepTogether)
{
    public ParagraphFormat Over(ParagraphFormat under) => new(
        Alignment ?? under.Alignment, SpaceBefore ?? under.SpaceBefore, SpaceAfter ?? under.SpaceAfter,
        LineHeight ?? under.LineHeight, IndentLeft ?? under.IndentLeft, IndentRight ?? under.IndentRight,
        IndentFirstLine ?? under.IndentFirstLine, OutlineLevel ?? under.OutlineLevel,
        KeepTogether ?? under.KeepTogether);
}

/// <summary>One entry from styles.xml, before its basedOn chain is resolved.</summary>
internal sealed record DocxStyle(string Id, string? Name, string? BasedOn, ParagraphFormat Paragraph, RunFormat Run);

/// <summary>
/// styles.xml, with basedOn chains resolved on demand. Word documents lean on style inheritance heavily:
/// a heading is usually "Heading 2 based on Heading 1 based on Normal", and reading only the leaf loses most
/// of the formatting.
/// </summary>
internal sealed class StyleSheet
{
    private readonly Dictionary<string, DocxStyle> _declared;
    private readonly Dictionary<string, DocxStyle> _resolved = new(StringComparer.Ordinal);

    public StyleSheet(Dictionary<string, DocxStyle> declared, RunFormat documentDefaults, string defaultFont, double defaultSize)
    {
        _declared = declared;
        DocumentDefaults = documentDefaults;
        DefaultFont = defaultFont;
        DefaultSize = defaultSize;
    }

    public RunFormat DocumentDefaults { get; }
    public string DefaultFont { get; }
    public double DefaultSize { get; }

    public DocxStyle? Resolve(string? id)
    {
        if (id == null) return null;
        if (_resolved.TryGetValue(id, out var cached)) return cached;
        if (!_declared.TryGetValue(id, out var style)) return null;

        // Guard against a file whose styles point at each other in a loop.
        _resolved[id] = style;

        var parent = style.BasedOn != null && !string.Equals(style.BasedOn, id, StringComparison.Ordinal)
            ? Resolve(style.BasedOn)
            : null;
        if (parent != null)
            style = style with
            {
                Paragraph = style.Paragraph.Over(parent.Paragraph),
                Run = style.Run.Over(parent.Run),
            };

        _resolved[id] = style;
        return style;
    }
}

/// <summary>Turns WordprocessingML property elements into the records above.</summary>
internal static class DocxFormat
{
    public static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    // WPF measures everything in device independent pixels of 1/96 inch. Word uses points (1/72 inch), twips
    // (1/1440) and, for pictures, EMUs (1/914400). Treating any of them as equal makes a document come out
    // around a quarter too small, so every measurement crosses here.
    public const double HalfPointsToDip = 96.0 / 72 / 2;
    public const double TwipsToDip = 96.0 / 1440;
    public const double EighthPointsToDip = 96.0 / 72 / 8;
    public const double EmusToDip = 96.0 / 914400;

    /// <summary>How far each list level is indented, matching Word's default of half an inch per level.</summary>
    public const double IndentPerLevel = 48;

    /// <summary>Word writes an on/off property as the bare element, or with w:val of 0, false or off to mean off.</summary>
    public static bool? Toggle(XElement? element)
    {
        if (element == null) return null;
        return element.Attribute(W + "val")?.Value is not ("0" or "false" or "off");
    }

    public static RunFormat RunFormatOf(XElement? properties)
    {
        if (properties == null) return default;

        bool? underline = properties.Element(W + "u") is { } u
            ? u.Attribute(W + "val")?.Value is not ("none" or null) || u.Attribute(W + "val") == null
            : null;

        return new RunFormat(
            properties.Element(W + "rFonts")?.Attribute(W + "ascii")?.Value is { Length: > 0 } font ? font : null,
            Points(properties.Element(W + "sz")?.Attribute(W + "val")?.Value),
            Toggle(properties.Element(W + "b")),
            Toggle(properties.Element(W + "i")),
            underline,
            Toggle(properties.Element(W + "strike")),
            Colour(properties.Element(W + "color")?.Attribute(W + "val")?.Value),
            Colour(properties.Element(W + "highlight")?.Attribute(W + "val")?.Value)
            ?? Colour(properties.Element(W + "shd")?.Attribute(W + "fill")?.Value),
            Toggle(properties.Element(W + "caps")),
            Toggle(properties.Element(W + "smallCaps")));
    }

    public static ParagraphFormat ParagraphFormatOf(XElement? properties)
    {
        if (properties == null) return default;

        var spacing = properties.Element(W + "spacing");
        var indent = properties.Element(W + "ind");

        double? lineHeight = null;
        if (spacing?.Attribute(W + "line")?.Value is { } line && double.TryParse(line, out var lineValue) && lineValue > 0)
        {
            // "auto" means a multiple of single spacing, in 240ths; anything else is an exact measure in twips.
            var rule = spacing.Attribute(W + "lineRule")?.Value;
            lineHeight = rule == "auto" ? null : lineValue * TwipsToDip;
        }

        var left = Twips(indent?.Attribute(W + "left")?.Value ?? indent?.Attribute(W + "start")?.Value);
        var hanging = Twips(indent?.Attribute(W + "hanging")?.Value);
        var firstLine = Twips(indent?.Attribute(W + "firstLine")?.Value);

        return new ParagraphFormat(
            properties.Element(W + "jc")?.Attribute(W + "val")?.Value switch
            {
                "center" => TextAlignment.Center,
                "right" or "end" => TextAlignment.Right,
                "both" or "distribute" => TextAlignment.Justify,
                "left" or "start" => TextAlignment.Left,
                _ => null,
            },
            Twips(spacing?.Attribute(W + "before")?.Value),
            Twips(spacing?.Attribute(W + "after")?.Value),
            lineHeight,
            left,
            Twips(indent?.Attribute(W + "right")?.Value ?? indent?.Attribute(W + "end")?.Value),
            hanging is { } indentHanging ? -indentHanging : firstLine,
            int.TryParse(properties.Element(W + "outlineLvl")?.Attribute(W + "val")?.Value, out var outline) ? outline : null,
            Toggle(properties.Element(W + "keepNext")));
    }

    public static double? Points(string? halfPoints) =>
        double.TryParse(halfPoints, out var value) && value > 0 ? Math.Clamp(value * HalfPointsToDip, 1, 500) : null;

    public static double? Twips(string? twips) =>
        double.TryParse(twips, out var value) ? value * TwipsToDip : null;

    public static Color? Colour(string? value)
    {
        if (value is not { Length: 6 }) return null;
        return int.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out var rgb)
            ? Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
            : null;
    }

    public static SolidColorBrush Brush(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }
}
