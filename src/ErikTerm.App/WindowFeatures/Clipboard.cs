using ErikTerm.App.Native;
using ErikTerm.App.Panes;
using ErikTerm.App.Ui;
using ErikTerm.Ui;

namespace ErikTerm.App.WindowFeatures;

/// <summary>The two things every feature hands the desktop: text for the clipboard, a link for the browser.</summary>
internal static class Clipboard
{
    /// <summary>Puts <paramref name="text"/> on the clipboard and the primary selection, with a toast saying <paramref name="what"/>.</summary>
    public static void Copy(IWindowChrome chrome, string text, string what)
    {
        Sdl.SetClipboardText(text);
        Sdl.SetPrimarySelectionText(text);
        chrome.Toast(ToastKind.Success, what, text);
    }

    /// <summary>Opens a link with the system opener, with a toast either way.</summary>
    public static void OpenLink(IWindowChrome chrome, string uri)
    {
        if (LinkOpener.Open(uri) is { } error) chrome.Toast(ToastKind.Error, "Couldn't open link", error);
        else chrome.Toast(ToastKind.Info, "Opening link", uri);
    }
}
