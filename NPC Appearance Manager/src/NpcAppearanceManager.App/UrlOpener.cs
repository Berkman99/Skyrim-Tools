using System.Diagnostics;
using System.Windows;
using NpcAppearanceManager.Core.Settings;

namespace NpcAppearanceManager.App;

/// <summary>Opens a web page in the browser. If Windows refuses, the link is copied and the user is told.</summary>
public static class UrlOpener
{
    /// <param name="report">Where to tell the user it didn't open (default: a message box).</param>
    public static void Open(string? url, Action<string>? report = null)
    {
        if (url == null || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            return;
        }

        var link = uri.AbsoluteUri;
        var errors = new List<string>();
        foreach (var start in new Func<ProcessStartInfo>[]
        {
            () => new ProcessStartInfo(link) { UseShellExecute = true },
            () => new ProcessStartInfo("explorer.exe", link) { UseShellExecute = false },
            () => new ProcessStartInfo("rundll32.exe", $"url.dll,FileProtocolHandler {link}") { UseShellExecute = false },
        })
        {
            try
            {
                Process.Start(start());
                return;
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }
        }

        Log(link, errors);
        var copied = false;
        try
        {
            Clipboard.SetText(link);
            copied = true;
        }
        catch { }
        var message = copied
            ? $"Couldn't open your browser. The link was copied, paste it into your browser: {link}"
            : $"Couldn't open your browser. Paste this link into your browser: {link}";
        if (report != null) report(message);
        else MessageBox.Show(Application.Current.MainWindow!, message, AppSettings.AppName);
    }

    private static void Log(string link, List<string> errors) =>
        Core.Logging.NamLog.Write($"Couldn't open {link}: {string.Join(" | ", errors)}");
}
