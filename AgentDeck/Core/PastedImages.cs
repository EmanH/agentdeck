using System.Windows;
using System.Windows.Media.Imaging;

namespace AgentDeck.Core;

/// <summary>
/// Clipboard images pasted into a terminal: saved as PNGs in a temp folder so their path can be pasted
/// (agents like Claude Code attach an image from its path). The folder is emptied on quit and on startup,
/// since a killed process never gets to quit. Clipboard calls must run on the UI thread.
/// </summary>
static class PastedImages
{
    public static readonly string Dir = Path.Combine(Path.GetTempPath(), "AgentDeck", "pasted-images");

    /// <summary>
    /// Paths for what's on the clipboard: files copied in Explorer as they are, or an image saved to a new PNG.
    /// Empty if the clipboard holds neither.
    /// </summary>
    public static string[] FromClipboard()
    {
        if (Retry(Clipboard.ContainsFileDropList) && Retry(Clipboard.GetFileDropList) is { Count: > 0 } files)
            return files.Cast<string>().ToArray();

        // Prefer the PNG stream browsers and screenshot tools put there (keeps transparency); fall back to the bitmap.
        byte[]? png = null;
        if (Retry(() => Clipboard.GetData("PNG")) is MemoryStream stream) png = stream.ToArray();
        else if (Retry(Clipboard.ContainsImage) && Retry(Clipboard.GetImage) is { } image)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var buffer = new MemoryStream();
            encoder.Save(buffer);
            png = buffer.ToArray();
        }
        if (png == null) return [];

        Directory.CreateDirectory(Dir);
        var path = Path.Combine(Dir, $"pasted-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        File.WriteAllBytes(path, png);
        Log.Info($"Pasted image saved ({png.Length / 1024} KB)");
        return [path];
    }

    public static void DeleteAll()
    {
        if (!Directory.Exists(Dir)) return;
        foreach (var file in Directory.EnumerateFiles(Dir))
        {
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // in use; next cleanup gets it
        }
    }

    /// <summary>The clipboard is briefly locked whenever another app touches it.</summary>
    static T? Retry<T>(Func<T> read)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return read(); }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 4) { Thread.Sleep(30); }
            catch (System.Runtime.InteropServices.COMException) { return default; }
        }
    }
}
