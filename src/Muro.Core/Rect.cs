namespace Muro.Core;

/// Axis-aligned rectangle in physical pixels of the virtual desktop.
public readonly record struct Rect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public Rect? Intersect(Rect other)
    {
        var left = Math.Max(X, other.X);
        var top = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return right > left && bottom > top ? new Rect(left, top, right - left, bottom - top) : null;
    }
}
