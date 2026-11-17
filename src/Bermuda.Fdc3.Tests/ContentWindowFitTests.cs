using Bermuda.IpcContract;

namespace Bermuda.Fdc3.Tests;

/// <summary>
/// The sizing rules behind <c>window.setContentSize</c>: DPI conversion, the
/// caption strip added on top of the content, and clamping to min/max and the
/// monitor work area.
/// </summary>
public class ContentWindowFitTests
{
    private static readonly ContentWindowFit.Scales Identity = new(1, 1, 1, 1, 1);
    private static readonly ContentWindowFit.Constraints Unconstrained = new(0, 0, double.PositiveInfinity, double.PositiveInfinity);
    private static readonly ContentWindowFit.Area Desktop = new(0, 0, 2000, 1200);

    [Fact]
    public void Css_pixels_become_dips_and_the_caption_strip_is_added()
    {
        // Client title bar: 3px resize border each side, 40px caption + 3px bottom border.
        var fit = Fit(400, 240, chrome: new ContentWindowFit.Chrome(6, 43));

        Assert.Equal(10, fit.Left);
        Assert.Equal(20, fit.Top);
        Assert.Equal(406, fit.Width);
        Assert.Equal(283, fit.Height);
    }

    [Fact]
    public void Matching_raster_and_dip_scales_cancel_so_dpi_does_not_double_the_window()
    {
        var fit = Fit(400, 240, scales: new ContentWindowFit.Scales(1, 1.5, 1.5, 1.5, 1.5), chrome: new ContentWindowFit.Chrome(6, 43));

        Assert.Equal(406, fit.Width);
        Assert.Equal(283, fit.Height);
    }

    [Fact]
    public void A_raster_scale_above_the_dip_scale_grows_the_window()
    {
        var fit = Fit(100, 80, scales: new ContentWindowFit.Scales(1, 2, 2, 1, 1));

        Assert.Equal(200, fit.Width);
        Assert.Equal(160, fit.Height);
    }

    [Fact]
    public void Page_zoom_scales_the_content_before_chrome_is_added()
    {
        var fit = Fit(100, 80, scales: new ContentWindowFit.Scales(2, 1, 1, 1, 1), chrome: new ContentWindowFit.Chrome(6, 43));

        Assert.Equal(206, fit.Width);
        Assert.Equal(203, fit.Height);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_zoom_is_treated_as_one(double zoom)
    {
        var fit = Fit(100, 80, scales: new ContentWindowFit.Scales(zoom, 1, 1, 1, 1));

        Assert.Equal(100, fit.Width);
        Assert.Equal(80, fit.Height);
    }

    [Fact]
    public void A_non_positive_content_size_is_ignored()
    {
        Assert.Null(ContentWindowFit.Fit(
            new ContentSize(0, 240),
            Identity,
            default,
            Unconstrained,
            Desktop,
            10,
            20));
    }

    [Fact]
    public void A_huge_content_size_is_clamped_to_the_work_area_and_pulled_on_screen()
    {
        var fit = Fit(100_000, 100_000, work: new ContentWindowFit.Area(50, 80, 800, 600), left: 900, top: 900);

        Assert.Equal(800, fit.Width);
        Assert.Equal(600, fit.Height);
        Assert.Equal(50, fit.Left);
        Assert.Equal(80, fit.Top);
    }

    [Fact]
    public void Position_is_left_alone_when_the_fitted_window_already_fits()
    {
        var fit = Fit(100, 80, left: 40, top: 60);

        Assert.Equal(40, fit.Left);
        Assert.Equal(60, fit.Top);
    }

    [Fact]
    public void Min_and_max_constraints_bound_the_outer_size()
    {
        var small = Fit(100, 80, constraints: new ContentWindowFit.Constraints(500, 400, double.PositiveInfinity, double.PositiveInfinity));
        Assert.Equal(500, small.Width);
        Assert.Equal(400, small.Height);

        var large = Fit(1000, 800, constraints: new ContentWindowFit.Constraints(0, 0, 300, 200));
        Assert.Equal(300, large.Width);
        Assert.Equal(200, large.Height);
    }

    [Fact]
    public void A_minimum_larger_than_the_work_area_does_not_push_the_window_off_screen()
    {
        var fit = Fit(100, 80, constraints: new ContentWindowFit.Constraints(900, 900, double.PositiveInfinity, double.PositiveInfinity), work: new ContentWindowFit.Area(0, 0, 800, 600));

        Assert.Equal(800, fit.Width);
        Assert.Equal(600, fit.Height);
    }

    private static ContentWindowFit.Placement Fit(
        int width,
        int height,
        ContentWindowFit.Scales? scales = null,
        ContentWindowFit.Chrome chrome = default,
        ContentWindowFit.Constraints? constraints = null,
        ContentWindowFit.Area? work = null,
        double left = 10,
        double top = 20)
    {
        var placement = ContentWindowFit.Fit(
            new ContentSize(width, height),
            scales ?? Identity,
            chrome,
            constraints ?? Unconstrained,
            work ?? Desktop,
            left,
            top);

        Assert.NotNull(placement);
        return placement.Value;
    }
}
