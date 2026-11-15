using System.Drawing;

namespace Snap2Board;

internal static class AppIcon
{
    private static Icon? _icon;

    public static Icon Get()
    {
        if (_icon != null) return _icon;
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("app.ico");
        _icon = stream != null ? new Icon(stream) : (Icon)SystemIcons.Application.Clone();
        return _icon;
    }
}
