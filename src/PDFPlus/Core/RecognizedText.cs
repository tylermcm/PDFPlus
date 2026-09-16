using System.Windows;

namespace PDFPlus.Core;

/// <summary>
/// A page's text recovered by OCR, shaped like PDFium's own text page so the rest of the app doesn't have to
/// care where the characters came from: one flat string plus one box per character, in PDF page space.
/// Separators (the spaces between words and the newlines between lines) carry an empty box and are skipped
/// by hit testing, exactly as PDFium's generated characters are.
/// </summary>
public sealed class RecognizedText
{
    private readonly Rect[] _boxes;
    /// <summary>Which OCR line each character came from, so selection rectangles never merge across lines.</summary>
    private readonly int[] _lines;

    public string Text { get; }
    public int Count => Text.Length;

    public RecognizedText(string text, Rect[] boxes, int[] lines)
    {
        if (boxes.Length != text.Length || lines.Length != text.Length)
            throw new ArgumentException("Every character needs a box and a line number.");
        Text = text;
        _boxes = boxes;
        _lines = lines;
    }

    public string Slice(int start, int count)
    {
        if (count <= 0 || start >= Text.Length) return "";
        start = Math.Max(0, start);
        return Text.Substring(start, Math.Min(count, Text.Length - start));
    }

    /// <summary>The character nearest <paramref name="point"/>, or -1 when the point isn't on any text.</summary>
    public int IndexAt(Point point, double tolerance)
    {
        var best = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < _boxes.Length; i++)
        {
            var box = _boxes[i];
            if (box.IsEmpty || box.Width <= 0 || box.Height <= 0) continue;

            // Reading a line is mostly a horizontal gesture, so allow a little slack above and below it.
            var reach = box;
            reach.Inflate(tolerance, Math.Max(tolerance, box.Height * 0.35));
            if (!reach.Contains(point)) continue;

            var distance = (point - new Point(box.X + box.Width / 2, box.Y + box.Height / 2)).LengthSquared;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = i;
        }
        return best;
    }

    /// <summary>Boxes covering characters start..start+count-1, merged into one rectangle per line.</summary>
    public Rect[] Rects(int start, int count)
    {
        if (count <= 0) return [];
        start = Math.Max(0, start);
        var end = Math.Min(Text.Length, start + count);

        var rects = new List<Rect>();
        var run = Rect.Empty;
        var runLine = -1;
        for (var i = start; i < end; i++)
        {
            var box = _boxes[i];
            if (box.IsEmpty || box.Width <= 0 || box.Height <= 0) continue;
            if (_lines[i] != runLine && !run.IsEmpty)
            {
                rects.Add(run);
                run = Rect.Empty;
            }
            runLine = _lines[i];
            run.Union(box);
        }
        if (!run.IsEmpty) rects.Add(run);
        return rects.ToArray();
    }

    public (int Start, int Count) WordAt(int index)
    {
        if (!IsWord(index)) return (index, 1);
        int start = index, end = index;
        while (IsWord(start - 1)) start--;
        while (IsWord(end + 1)) end++;
        return (start, end - start + 1);
    }

    private bool IsWord(int i) => i >= 0 && i < Text.Length && char.IsLetterOrDigit(Text[i]);

    /// <summary>Every match of <paramref name="query"/>, as (start, length) pairs into <see cref="Text"/>.</summary>
    public List<(int Start, int Count)> Find(string query, bool matchCase, bool wholeWord)
    {
        var matches = new List<(int, int)>();
        if (string.IsNullOrEmpty(query)) return matches;
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        var at = 0;
        while (at <= Text.Length - query.Length)
        {
            var found = Text.IndexOf(query, at, comparison);
            if (found < 0) break;
            at = found + 1;
            if (wholeWord && (IsWord(found - 1) || IsWord(found + query.Length))) continue;
            matches.Add((found, query.Length));
        }
        return matches;
    }
}

/// <summary>Collects OCR words line by line and lays them out as a <see cref="RecognizedText"/>.</summary>
public sealed class RecognizedTextBuilder
{
    private readonly System.Text.StringBuilder _text = new();
    private readonly List<Rect> _boxes = new();
    private readonly List<int> _lines = new();
    private int _line = -1;

    public void StartLine()
    {
        if (_line >= 0) Append('\n', Rect.Empty, _line);
        _line++;
    }

    /// <summary>Adds one word, splitting its box across the characters so selection follows the letters.</summary>
    public void AddWord(string word, Rect box)
    {
        if (word.Length == 0) return;
        if (_text.Length > 0 && _text[^1] != '\n') Append(' ', Rect.Empty, _line);

        var step = box.Width / word.Length;
        for (var i = 0; i < word.Length; i++)
            Append(word[i], new Rect(box.X + step * i, box.Y, step, box.Height), _line);
    }

    private void Append(char value, Rect box, int line)
    {
        _text.Append(value);
        _boxes.Add(box);
        _lines.Add(line);
    }

    public bool IsEmpty => _text.Length == 0;

    public RecognizedText Build() => new(_text.ToString(), _boxes.ToArray(), _lines.ToArray());
}
