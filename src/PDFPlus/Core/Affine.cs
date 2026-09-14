using System.Windows;

namespace PDFPlus.Core;

/// <summary>
/// 2D affine transform using PDF matrix conventions: (x, y) -> (A*x + C*y + E, B*x + D*y + F).
/// </summary>
public readonly record struct Affine(double A, double B, double C, double D, double E, double F)
{
    public static readonly Affine Identity = new(1, 0, 0, 1, 0, 0);

    public static Affine Translation(double x, double y) => new(1, 0, 0, 1, x, y);
    public static Affine Scale(double sx, double sy) => new(sx, 0, 0, sy, 0, 0);

    public Point Transform(Point p) => new(A * p.X + C * p.Y + E, B * p.X + D * p.Y + F);

    /// <summary>Returns a transform that applies this one first, then <paramref name="next"/>.</summary>
    public Affine Then(Affine next) => new(
        next.A * A + next.C * B,
        next.B * A + next.D * B,
        next.A * C + next.C * D,
        next.B * C + next.D * D,
        next.A * E + next.C * F + next.E,
        next.B * E + next.D * F + next.F);

    public Affine Invert()
    {
        var det = A * D - B * C;
        if (Math.Abs(det) < 1e-12) return Identity;
        return new(D / det, -B / det, -C / det, A / det, (C * F - D * E) / det, (B * E - A * F) / det);
    }

    public Rect TransformBounds(Rect r)
    {
        var p1 = Transform(r.TopLeft);
        var p2 = Transform(r.TopRight);
        var p3 = Transform(r.BottomLeft);
        var p4 = Transform(r.BottomRight);
        var minX = Math.Min(Math.Min(p1.X, p2.X), Math.Min(p3.X, p4.X));
        var minY = Math.Min(Math.Min(p1.Y, p2.Y), Math.Min(p3.Y, p4.Y));
        var maxX = Math.Max(Math.Max(p1.X, p2.X), Math.Max(p3.X, p4.X));
        var maxY = Math.Max(Math.Max(p1.Y, p2.Y), Math.Max(p3.Y, p4.Y));
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }
}
