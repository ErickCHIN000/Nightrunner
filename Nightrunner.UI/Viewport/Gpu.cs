using HelixToolkit.SharpDX;

namespace Nightrunner.UI.Viewport;

/// <summary>
/// The one Direct3D 11 device every viewport shares (Helix's effects manager owns it). Made on first use, released
/// when the app exits. Device loss is handled inside Helix: the render host disposes and re-creates the effects
/// manager's resources when a present reports it.
/// </summary>
internal static class Gpu
{
    private static IEffectsManager? _effects;

    public static IEffectsManager Effects => _effects ??= new DefaultEffectsManager();

    public static void Shutdown()
    {
        _effects?.Dispose();
        _effects = null;
    }
}
