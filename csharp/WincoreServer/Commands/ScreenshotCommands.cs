using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using Wincore.ServerSdk;
using WincoreServer.State;
using WincoreServer.Uia3;

namespace WincoreServer.Commands;

public static class ScreenshotCommands
{
    public static object? GetScreenshot(SessionState state, JsonElement? parameters)
    {
        var root = state.GetLiveRoot();

        if (root == null)
        {
            // Return 1x1 transparent PNG if no root
            using var bitmap = new Bitmap(1, 1);
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return Convert.ToBase64String(stream.ToArray());
        }

        var rect = root.CurrentBoundingRectangle;
        var width = rect.right - rect.left;
        var height = rect.bottom - rect.top;
        using var bmp = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(bmp);
        graphics.CopyFromScreen(rect.left, rect.top, 0, 0, bmp.Size);

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return Convert.ToBase64String(ms.ToArray());
    }

    public static object? GetElementScreenshot(SessionState state, IUIAutomationElement element, JsonElement parameters)
    {
        var rect = element.CurrentBoundingRectangle;
        return CaptureRect(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);
    }

    public static object? GetProviderElementScreenshot(ITreeProvider provider, string elementId, JsonElement parameters)
    {
        // Provider rects are { x, y, width, height } objects minted in the plugin's
        // assembly — read them structurally rather than by type.
        var rect = JsonSerializer.SerializeToElement(provider.GetRect(elementId));
        return CaptureRect(
            (int)Math.Round(rect.GetProperty("x").GetDouble()),
            (int)Math.Round(rect.GetProperty("y").GetDouble()),
            (int)Math.Round(rect.GetProperty("width").GetDouble()),
            (int)Math.Round(rect.GetProperty("height").GetDouble()));
    }

    private static string CaptureRect(int left, int top, int width, int height)
    {
        using var bitmap = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(left, top, 0, 0, bitmap.Size);

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return Convert.ToBase64String(stream.ToArray());
    }
}
