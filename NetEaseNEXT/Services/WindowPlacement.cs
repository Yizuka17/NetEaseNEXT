using Windows.Graphics;

namespace NetEaseNEXT.Services;

public sealed record WindowPlacement(int X, int Y, int Width, int Height, bool Maximized = false)
{
    internal bool IsValid => Width is >= 200 and <= 20000 && Height is >= 150 and <= 20000 && X is >= -100000 and <= 100000 && Y is >= -100000 and <= 100000;
    internal RectInt32 Bounds => new(X, Y, Width, Height);
    internal RectInt32 ClampTo(RectInt32 work)
    {
        var width = Math.Clamp(Width, Math.Min(900, work.Width), work.Width);
        var height = Math.Clamp(Height, Math.Min(600, work.Height), work.Height);
        return new(Math.Clamp(X, work.X, work.X + work.Width - width), Math.Clamp(Y, work.Y, work.Y + work.Height - height), width, height);
    }
}
