using System.Windows;

namespace DoraExplorer.App.Services;

public static class AnimationFrameRateService
{
    public const int Rate = 144;

    public static readonly Duration ChildRevealDuration = new(TimeSpan.FromMilliseconds(380));

    public const double ChildRevealStaggerMs = 26;

    public const double ChildRevealOffsetY = -14;
}
