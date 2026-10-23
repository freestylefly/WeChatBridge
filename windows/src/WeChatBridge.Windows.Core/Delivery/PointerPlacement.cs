using System.Drawing;

namespace WeChatBridge.Windows.Core.Delivery;

/// <summary>
/// Where a panel that answers a click belongs on screen. Ported from
/// <c>PointerPlacement.swift</c>.
///
/// A fixed corner reads as WeChatBridge's home rather than as an answer to what
/// the user just did — and on more than one display it may not even be the
/// display they were working on. A question about a click belongs next to that
/// click, the way a context menu does: hanging off the pointer, on the pointer's
/// screen.
///
/// Coordinates are Windows desktop pixels: the origin is top-left and y grows
/// *downward*, so 「指针下方」 is a larger y — the mirror image of AppKit.
/// </summary>
public static class PointerPlacement
{
    /// <summary>
    /// Frame for a panel of <paramref name="size"/> answering a click at
    /// <paramref name="pointer"/>, kept <paramref name="margin"/> inside
    /// <paramref name="visible"/>.
    ///
    /// The panel hangs below-right of the pointer, the direction a Windows menu
    /// opens. Each axis flips to the other side when that side does not fit —
    /// flipping rather than clamping, because a panel clamped against the bottom
    /// of the screen ends up sitting *under* the pointer, hiding the row it is
    /// nearest. Clamping is still the last word, for a panel taller or wider
    /// than any corner it could hang in.
    /// </summary>
    public static RectangleF Frame(
        SizeF size,
        PointF pointer,
        RectangleF visible,
        float gap = 10,
        float margin = 12)
    {
        var room = RectangleF.Inflate(visible, -margin, -margin);

        var x = pointer.X + gap;
        if (x + size.Width > room.Right && pointer.X - gap - size.Width >= room.Left)
            x = pointer.X - gap - size.Width;

        var y = pointer.Y + gap;
        if (y + size.Height > room.Bottom && pointer.Y - gap - size.Height >= room.Top)
            y = pointer.Y - gap - size.Height;

        return Clamp(new RectangleF(x, y, size.Width, size.Height), room);
    }

    /// <summary>
    /// Inside <paramref name="room"/>, moved rather than resized: cropping a
    /// panel would cost the user a row of the list they are being asked to
    /// choose from.
    /// </summary>
    private static RectangleF Clamp(RectangleF frame, RectangleF room)
    {
        var x = Math.Min(Math.Max(frame.Left, room.Left), Math.Max(room.Left, room.Right - frame.Width));
        var y = Math.Min(Math.Max(frame.Top, room.Top), Math.Max(room.Top, room.Bottom - frame.Height));
        return new RectangleF(x, y, frame.Width, frame.Height);
    }
}
