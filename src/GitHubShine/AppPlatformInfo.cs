namespace GitHubShine;

/// <summary>
/// Host-supplied platform facts the shared Blazor UI can't determine on its own
/// (the WebView runs identical code on every head). Registered in <see cref="MauiProgram"/>
/// where the compile-time MOBILE constant is known.
/// </summary>
/// <param name="IsMobile">True on iOS/Android, where some desktop-oriented features are hidden.</param>
public sealed record AppPlatformInfo(bool IsMobile);
