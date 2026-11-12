// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CouchSubs.Core;
using CouchSubs.RemotePlay;
using CouchSubs.Shell;

namespace CouchSubs.App.Views.Steps;

/// <summary>
/// PSN girişi. Sony'nin sayfası sistem tarayıcısında açılır; giriş bitince tarayıcının gittiği yönlendirme adresi
/// kopyalanıp buraya yapıştırılır (Linux panelindeki yöntem). Eskiden giriş uygulama içi WebView2 penceresinde
/// kendiliğinden açılıyordu; bazı ağlarda Sony'nin sayfası orada yüklenmeyip zaman aşımına düşüyordu. O pencere yedek
/// olarak duruyor. Kullanıcı tarayıcıdan dönünce panodaki Sony adresi kendiliğinden alınır.
/// </summary>
public partial class PsnLoginStep : UserControl
{
    private Window? owner;
    /// <summary>Tarayıcı bu adımda açıldı mı; açıldıysa uygulamaya dönülünce pano kendiliğinden okunur.</summary>
    private bool browserOpened;
    /// <summary>Kullanılan son yönlendirme: kod tek kullanımlık, aynı pano içeriği ikinci kez gönderilmez.</summary>
    private string? lastUsed;
    private bool working;

    public PsnLoginStep()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Refresh();
            owner = Window.GetWindow(this);
            if (owner is not null) owner.Activated += OnOwnerActivated;
        };
        Unloaded += (_, _) =>
        {
            if (owner is not null) owner.Activated -= OnOwnerActivated;
            owner = null;
        };
    }

    private AppController Controller => (AppController)DataContext;

    private void Refresh()
    {
        OpenBrowser.Content = Localizer.Current[Controller.HasAccount ? "Sign in again in the browser" : "Open Sony sign-in in the browser"];
        OpenBrowser.Style = (Style)FindResource(Controller.HasAccount ? "SecondaryButton" : "PrimaryButton");
        Complete.IsEnabled = !working && PsnAccount.RedirectFromPasted(Redirect.Text) is not null;
        OpenBrowser.IsEnabled = InApp.IsEnabled = Paste.IsEnabled = !working;
    }

    private void OnOpenBrowser(object sender, RoutedEventArgs e)
    {
        Error.Text = "";
        try
        {
            Process.Start(new ProcessStartInfo(PsnAccount.AuthorizeUrl.AbsoluteUri) { UseShellExecute = true });
            browserOpened = true;
            RemotePlayLog.Note("PSN girişi: Sony sayfası tarayıcıda açıldı");
        }
        catch (Exception error)
        {
            // Varsayılan tarayıcı yoksa bağlantı kopyalanır; kullanıcı istediği tarayıcıya yapıştırır.
            RemotePlayLog.Note($"PSN girişi: tarayıcı açılamadı: {error.Message}");
            OnCopyLink(sender, e);
            Error.Text = Localizer.Current["The browser could not be opened. The link was copied; paste it into your browser's address bar."];
        }
    }

    private void OnCopyLink(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(PsnAccount.AuthorizeUrl.AbsoluteUri);
            browserOpened = true;
            Copied.Visibility = Visibility.Visible;
        }
        catch (Exception) { }
    }

    /// <summary>Tarayıcıdan uygulamaya dönülünce panoda Sony'nin yönlendirme adresi varsa alanı doldurur ve girişi bitirir.</summary>
    private async void OnOwnerActivated(object? sender, EventArgs e)
    {
        if (!browserOpened || working || !IsVisible) return;
        var text = ReadClipboard();
        if (!PsnAccount.LooksLikeRedirect(text) || text == lastUsed) return;
        Redirect.Text = text!.Trim();
        await CompleteAsync();
    }

    private void OnPaste(object sender, RoutedEventArgs e)
    {
        var text = ReadClipboard();
        if (string.IsNullOrWhiteSpace(text)) return;
        Redirect.Text = text.Trim();
        Redirect.Focus();
        Redirect.CaretIndex = Redirect.Text.Length;
    }

    private static string? ReadClipboard()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
        catch (Exception) { return null; } // Pano başka bir uygulamada kilitliyse sessizce geçilir.
    }

    private void OnRedirectChanged(object sender, TextChangedEventArgs e)
    {
        if (Complete is null) return; // InitializeComponent sırasında
        Refresh();
    }

    /// <summary>Adres alanında Enter girişi bitirir; adımın "Devam" düğmesine gitmez.</summary>
    private async void OnRedirectKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return)) return;
        e.Handled = true;
        await CompleteAsync();
    }

    private async void OnComplete(object sender, RoutedEventArgs e) => await CompleteAsync();

    private async Task CompleteAsync()
    {
        if (working) return;
        var pasted = Redirect.Text;
        if (PsnAccount.RedirectFromPasted(pasted) is not { } redirect)
        {
            Error.Text = Localizer.Current["This is not Sony's sign-in address. After signing in, copy the whole address of the page that opens (it starts with https://remoteplay.dl.playstation.net/) and paste it here."];
            return;
        }
        lastUsed = pasted.Trim();
        await RunLoginAsync(redirect);
        if (Controller.HasAccount) Redirect.Text = "";
    }

    /// <summary>Yedek: eski uygulama içi WebView2 penceresi.</summary>
    private async void OnSignInInApp(object sender, RoutedEventArgs e)
    {
        // Giriş penceresi WebView2 ile açılır; kurulum normalde onu da kurar, yine de eksikse çökmek yerine yol gösterilir.
        if (!PsnLoginWindow.IsBrowserAvailable())
        {
            RemotePlayLog.Note("PSN girişi: WebView2 kurulu değil");
            Error.Text = Localizer.Current["The sign-in window needs Microsoft's WebView2 component, which is missing on this computer. Install it from the page that just opened, then press Sign in again."];
            try { Process.Start(new ProcessStartInfo(PsnLoginWindow.BrowserDownloadUrl) { UseShellExecute = true }); }
            catch (Exception) { }
            return;
        }
        var (redirect, problem) = PsnLoginWindow.Show(Window.GetWindow(this));
        if (redirect is null)
        {
            // Pencere giriş bitmeden kapandı: eskiden sessizce dönülüyordu.
            Error.Text = Controller.LoginWindowError(problem).Message;
            return;
        }
        await RunLoginAsync(redirect);
    }

    private async Task RunLoginAsync(Uri redirect)
    {
        working = true;
        Refresh();
        Error.Text = "";
        try
        {
            await Controller.CompletePsnLoginAsync(redirect);
        }
        catch (AppException error)
        {
            Error.Text = error.Message;
        }
        catch (Exception error)
        {
            // Son çare: denetleyici her hatayı AppException'a çevirir; buraya düşen yine de ayrıntısıyla gösterilir.
            Error.Text = $"{AppException.Describe(AppError.InvalidLogin, Localizer.Current)} ({error.Message})";
        }
        working = false;
        Refresh();
    }

    private void OnShowManual(object sender, RoutedEventArgs e)
    {
        ShowManual.Visibility = Visibility.Collapsed;
        Manual.Visibility = Visibility.Visible;
        ManualId.Focus();
    }

    /// <summary>Kimlik alanında Enter kaydeder; adımın "Devam" düğmesine gitmez.</summary>
    private void OnManualKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return)) return;
        e.Handled = true;
        OnSaveManual(sender, e);
    }

    private void OnSaveManual(object sender, RoutedEventArgs e)
    {
        if (Controller.UseManualAccountId(ManualId.Text))
        {
            ManualError.Text = "";
            Error.Text = "";
            Refresh();
        }
        else
        {
            ManualError.Text = Localizer.Current["This is not a valid account ID. It is 12 characters in base64, e.g. AbCdEfGhIjk=."];
        }
    }
}
