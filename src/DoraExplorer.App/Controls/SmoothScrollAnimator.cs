using System.Windows;

using System.Windows.Controls;

using System.Windows.Input;

using System.Windows.Media;

using DoraExplorer.App.Services;



namespace DoraExplorer.App.Controls;



/// <summary>

/// Scroll contínuo estilo CSS: o destino acumula em pixels e a posição segue suavemente a cada frame.

/// </summary>

public static class SmoothScrollAnimator

{

    private sealed class ScrollState

    {

        public double Target;

        public double LastFrameMs;

    }



    private static readonly Dictionary<ScrollViewer, ScrollState> States = new();

    private static bool _isRenderingHooked;



    /// <summary>Linhas percorridas por cada "notch" da roda (2/3 do padrão de 3 linhas do Windows).</summary>

    private const int LinesPerNotch = 2;



    private const double FollowHalfLifeMs = 110;



    private static double SnapThreshold => 0.25;



    public static void HandleMouseWheel(ScrollViewer scrollViewer, MouseWheelEventArgs e)

    {

        if (scrollViewer.ScrollableHeight <= 0 || e.Delta == 0)

        {

            return;

        }



        e.Handled = true;



        var isNew = !States.ContainsKey(scrollViewer);

        var state = GetOrCreateState(scrollViewer, syncTargetFromViewer: isNew);



        // Delta WPF positivo = roda para cima => diminuir offset. Escala linear (roda de alta resolução).

        var deltaPixels = -(e.Delta / 120.0) * LinesPerNotch * TreeViewHierarchy.HeaderRowHeight;

        state.Target = Math.Clamp(state.Target + deltaPixels, 0, scrollViewer.ScrollableHeight);

        EnsureRenderingHook();

    }



    public static void ScrollTo(ScrollViewer scrollViewer, double targetOffset)

    {

        scrollViewer.UpdateLayout();



        var state = GetOrCreateState(scrollViewer, syncTargetFromViewer: !States.ContainsKey(scrollViewer));

        var max = scrollViewer.ScrollableHeight;



        if (max <= 0)

        {

            state.Target = Math.Max(0, targetOffset);

            EnsureRenderingHook();

            return;

        }



        state.Target = Math.Clamp(targetOffset, 0, max);

        EnsureRenderingHook();

    }



    public static void SyncTarget(ScrollViewer scrollViewer, double offset)

    {

        if (States.TryGetValue(scrollViewer, out var state))

        {

            state.Target = offset;

            state.LastFrameMs = 0;

            return;

        }



        States[scrollViewer] = new ScrollState

        {

            Target = offset,

            LastFrameMs = 0

        };

    }



    private static ScrollState GetOrCreateState(ScrollViewer scrollViewer, bool syncTargetFromViewer)

    {

        if (!States.TryGetValue(scrollViewer, out var state))

        {

            state = new ScrollState

            {

                Target = scrollViewer.VerticalOffset,

                LastFrameMs = 0

            };

            States[scrollViewer] = state;

            return state;

        }



        if (syncTargetFromViewer)

        {

            state.Target = scrollViewer.VerticalOffset;

            state.LastFrameMs = 0;

        }



        return state;

    }



    private static void EnsureRenderingHook()

    {

        if (_isRenderingHooked)

        {

            return;

        }



        CompositionTarget.Rendering += OnRendering;

        _isRenderingHooked = true;

    }



    private static void OnRendering(object? sender, EventArgs e)

    {

        if (States.Count == 0)

        {

            CompositionTarget.Rendering -= OnRendering;

            _isRenderingHooked = false;

            return;

        }



        var now = e is RenderingEventArgs args

            ? args.RenderingTime.TotalMilliseconds

            : 0;

        var completed = new List<ScrollViewer>();



        foreach (var pair in States)

        {

            var scrollViewer = pair.Key;

            var state = pair.Value;

            var current = scrollViewer.VerticalOffset;

            var max = scrollViewer.ScrollableHeight;



            if (max > 0)

            {

                state.Target = Math.Clamp(state.Target, 0, max);

            }

            else if (Math.Abs(current) < SnapThreshold)

            {

                completed.Add(scrollViewer);

                continue;

            }



            var dtMs = state.LastFrameMs > 0

                ? Math.Clamp(now - state.LastFrameMs, 1, 32)

                : 1000.0 / AnimationFrameRateService.Rate;



            state.LastFrameMs = now;



            var alpha = 1 - Math.Exp(-0.693147 * dtMs / FollowHalfLifeMs);

            var next = current + ((state.Target - current) * alpha);



            if (Math.Abs(state.Target - next) < SnapThreshold &&

                Math.Abs(state.Target - current) < SnapThreshold)

            {

                next = state.Target;

                completed.Add(scrollViewer);

            }



            if (Math.Abs(next - current) > 0.01)

            {

                scrollViewer.ScrollToVerticalOffset(next);

            }

            else if (Math.Abs(state.Target - current) < SnapThreshold)

            {

                completed.Add(scrollViewer);

            }

        }



        foreach (var scrollViewer in completed)

        {

            States.Remove(scrollViewer);

        }

    }

}


