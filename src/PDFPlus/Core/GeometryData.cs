using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace PDFPlus.Core;

public static class GeometryData
{
    /// <summary>Flattens any WPF geometry into polylines that PDFium can turn into path objects.</summary>
    public static List<PathFigureData> Flatten(Geometry geometry, double tolerance = 0.1)
    {
        var result = new List<PathFigureData>();
        var flat = geometry.GetFlattenedPathGeometry(tolerance, ToleranceType.Absolute);
        foreach (var figure in flat.Figures)
        {
            var points = new List<Point> { figure.StartPoint };
            foreach (var segment in figure.Segments)
            {
                switch (segment)
                {
                    case LineSegment line: points.Add(line.Point); break;
                    case PolyLineSegment poly: points.AddRange(poly.Points); break;
                }
            }
            if (points.Count >= 2) result.Add(new PathFigureData(points.ToArray(), figure.IsClosed));
        }
        return result;
    }

    public static Rect Bounds(IEnumerable<PathFigureData> figures)
    {
        var bounds = Rect.Empty;
        foreach (var figure in figures)
            foreach (var p in figure.Points)
                bounds.Union(p);
        return bounds;
    }

    public static List<PathFigureData> Translate(IEnumerable<PathFigureData> figures, double dx, double dy) =>
        figures.Select(f => new PathFigureData(f.Points.Select(p => new Point(p.X + dx, p.Y + dy)).ToArray(), f.Closed)).ToList();

    public static string ToMarkup(IEnumerable<PathFigureData> figures)
    {
        var builder = new StringBuilder("F1");
        foreach (var figure in figures)
        {
            builder.Append(" M").Append(Format(figure.Points[0]));
            builder.Append(" L");
            for (var i = 1; i < figure.Points.Length; i++) builder.Append(' ').Append(Format(figure.Points[i]));
            if (figure.Closed) builder.Append(" Z");
        }
        return builder.ToString();
    }

    public static PathGeometry ToGeometry(IEnumerable<PathFigureData> figures)
    {
        var geometry = new PathGeometry { FillRule = FillRule.Nonzero };
        foreach (var figure in figures)
        {
            var pathFigure = new PathFigure { StartPoint = figure.Points[0], IsClosed = figure.Closed, IsFilled = figure.Closed };
            pathFigure.Segments.Add(new PolyLineSegment(figure.Points.Skip(1), true));
            geometry.Figures.Add(pathFigure);
        }
        geometry.Freeze();
        return geometry;
    }

    private static string Format(Point p) =>
        p.X.ToString("0.##", CultureInfo.InvariantCulture) + "," + p.Y.ToString("0.##", CultureInfo.InvariantCulture);
}

public static class PageRanges
{
    /// <summary>Parses "1-3, 5, 9-" (1-based) into sorted 0-based page indices. Returns null if invalid.</summary>
    public static int[]? Parse(string text, int pageCount)
    {
        var pages = new SortedSet<int>();
        foreach (var raw in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = raw.Split('-', StringSplitOptions.TrimEntries);
            if (parts.Length == 1 && int.TryParse(parts[0], out var single))
            {
                if (single < 1 || single > pageCount) return null;
                pages.Add(single - 1);
            }
            else if (parts.Length == 2)
            {
                var start = parts[0].Length == 0 ? 1 : int.TryParse(parts[0], out var s) ? s : -1;
                var end = parts[1].Length == 0 ? pageCount : int.TryParse(parts[1], out var e) ? e : -1;
                if (start < 1 || end < start || end > pageCount) return null;
                for (var i = start; i <= end; i++) pages.Add(i - 1);
            }
            else
            {
                return null;
            }
        }
        return pages.Count == 0 ? null : pages.ToArray();
    }

    /// <summary>Formats 0-based indices as a compact 1-based range string, e.g. "1-3, 5".</summary>
    public static string Format(IEnumerable<int> indices)
    {
        var sorted = indices.Distinct().OrderBy(i => i).ToList();
        var parts = new List<string>();
        for (var i = 0; i < sorted.Count;)
        {
            var j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
            parts.Add(i == j ? $"{sorted[i] + 1}" : $"{sorted[i] + 1}-{sorted[j] + 1}");
            i = j + 1;
        }
        return string.Join(", ", parts);
    }
}
