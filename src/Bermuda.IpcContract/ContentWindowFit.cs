namespace Bermuda.IpcContract;

/// <summary>
/// Turns a <see cref="ContentSize"/> reported by a page into an outer window
/// placement. The host measures DPI, page zoom, the caption strip and the
/// monitor work area; this applies them. Pure so the rules can be tested
/// without a window.
/// </summary>
public static class ContentWindowFit
{
    /// <summary>
    /// <paramref name="PageZoom"/> is the WebView zoom (1 = 100%).
    /// <paramref name="RasterX"/>/<paramref name="RasterY"/> are physical pixels
    /// per CSS pixel; <paramref name="DipX"/>/<paramref name="DipY"/> are physical
    /// pixels per device-independent pixel. On a per-monitor-aware window those
    /// two scales match, so they cancel and a CSS pixel is one DIP before zoom.
    /// </summary>
    public readonly record struct Scales(double PageZoom, double RasterX, double RasterY, double DipX, double DipY);

    /// <summary>Host chrome around the page, in DIPs: caption strip plus any resize border the page does not cover.</summary>
    public readonly record struct Chrome(double Width, double Height);

    /// <summary>Outer-window min/max in DIPs. Non-positive or non-finite limits are treated as unset.</summary>
    public readonly record struct Constraints(double MinWidth, double MinHeight, double MaxWidth, double MaxHeight);

    /// <summary>Monitor work area in DIPs.</summary>
    public readonly record struct Area(double Left, double Top, double Width, double Height);

    /// <summary>Outer window placement in DIPs.</summary>
    public readonly record struct Placement(double Left, double Top, double Width, double Height);

    /// <summary>
    /// Returns null when <paramref name="content"/> is non-positive, so a page
    /// that reports 0 while it is hidden does not collapse the window.
    /// </summary>
    public static Placement? Fit(
        ContentSize content,
        Scales scales,
        Chrome chrome,
        Constraints constraints,
        Area workArea,
        double currentLeft,
        double currentTop)
    {
        var contentW = CssToDip(content.Width, scales.PageZoom, scales.RasterX, scales.DipX);
        var contentH = CssToDip(content.Height, scales.PageZoom, scales.RasterY, scales.DipY);
        if (contentW <= 0 || contentH <= 0) return null;

        var desiredW = Math.Ceiling(contentW + NonNegative(chrome.Width));
        var desiredH = Math.Ceiling(contentH + NonNegative(chrome.Height));

        var clampToWork = workArea.Width > 0 && workArea.Height > 0
            && double.IsFinite(workArea.Width) && double.IsFinite(workArea.Height);

        var maxW = Cap(constraints.MaxWidth, clampToWork ? workArea.Width : double.PositiveInfinity);
        var maxH = Cap(constraints.MaxHeight, clampToWork ? workArea.Height : double.PositiveInfinity);
        // A minimum larger than the work area (or the maximum) would push the
        // window off the monitor, so the smaller limit wins.
        var minW = Math.Min(NonNegative(constraints.MinWidth), maxW);
        var minH = Math.Min(NonNegative(constraints.MinHeight), maxH);

        var width = Math.Clamp(desiredW, minW, maxW);
        var height = Math.Clamp(desiredH, minH, maxH);

        var left = currentLeft;
        var top = currentTop;
        if (clampToWork)
        {
            var maxLeft = workArea.Left + workArea.Width - width;
            var maxTop = workArea.Top + workArea.Height - height;
            left = maxLeft >= workArea.Left ? Math.Clamp(left, workArea.Left, maxLeft) : workArea.Left;
            top = maxTop >= workArea.Top ? Math.Clamp(top, workArea.Top, maxTop) : workArea.Top;
        }

        return new Placement(left, top, width, height);
    }

    /// <summary>CSS pixels → DIPs. See <see cref="Scales"/> for what the scales mean.</summary>
    public static double CssToDip(double cssPixels, double pageZoom, double rasterScale, double dipScale)
    {
        if (!double.IsFinite(cssPixels) || cssPixels <= 0) return 0;
        if (!double.IsFinite(pageZoom) || pageZoom <= 0) pageZoom = 1;
        if (!double.IsFinite(dipScale) || dipScale <= 0) dipScale = 1;
        if (!double.IsFinite(rasterScale) || rasterScale <= 0) rasterScale = dipScale;
        return cssPixels * pageZoom * rasterScale / dipScale;
    }

    private static double Cap(double constraint, double workLimit)
    {
        var cap = double.IsFinite(constraint) && constraint > 0 ? constraint : double.PositiveInfinity;
        if (double.IsFinite(workLimit) && workLimit > 0)
            cap = Math.Min(cap, workLimit);
        return cap;
    }

    private static double NonNegative(double value) =>
        double.IsFinite(value) && value > 0 ? value : 0;
}
