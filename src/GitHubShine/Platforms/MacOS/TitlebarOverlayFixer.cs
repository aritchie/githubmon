using AppKit;

namespace GitHubShine.Platforms.MacOS;

/// <summary>
/// Platform.Maui.MacOS.BlazorWebView installs a full-width titlebar drag overlay
/// whose HitTest swallows mouse clicks on the standard window buttons (they're
/// not NSToolbarItems, the only thing it passes through) — making close/minimize/
/// zoom unclickable. Shift the overlay's leading edge past the traffic-light zone
/// so the buttons get their clicks back; the rest of the titlebar still drags.
/// </summary>
public static class TitlebarOverlayFixer
{
    const int ButtonZoneWidth = 80;

    public static void Apply()
    {
        foreach (var window in NSApplication.SharedApplication.DangerousWindows)
        {
            var frame = window.ContentView?.Superview;
            if (frame is null)
                continue;

            foreach (var sub in frame.Subviews)
            {
                if (sub.Class?.Name?.Contains("TitlebarDragOverlay", StringComparison.OrdinalIgnoreCase) != true)
                    continue;

                foreach (var constraint in frame.Constraints)
                {
                    if (constraint.FirstAttribute == NSLayoutAttribute.Leading &&
                        constraint.FirstItem is NSView first &&
                        first.Handle == sub.Handle &&
                        constraint.Constant < ButtonZoneWidth)
                    {
                        constraint.Constant = ButtonZoneWidth;
                        Console.WriteLine("[TitlebarOverlayFixer] drag overlay leading offset to {0}px", ButtonZoneWidth);
                    }
                }
            }
        }
    }
}
