using Foundation;

namespace GitHubShine;

/// <summary>
/// Apps built against the iOS 27 SDK must use the UIKit scene lifecycle (the build fails with
/// MT7199 otherwise, and the OS kills an app without one at launch). MAUI's own scene delegate
/// does all the work; it only needs registering under the name Info.plist's scene manifest uses.
/// </summary>
[Register("SceneDelegate")]
public class SceneDelegate : MauiUISceneDelegate
{
}
