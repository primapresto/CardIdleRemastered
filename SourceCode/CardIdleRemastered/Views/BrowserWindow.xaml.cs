using System;
using System.Net;
using System.Threading.Tasks;
using System.Windows;

namespace CardIdleRemastered
{
    /// <summary>
    /// Interaction logic for BrowserWindow.xaml
    /// </summary>
    public partial class BrowserWindow : Window
    {
        public BrowserWindow()
        {
            InitializeComponent();
        }

        public ISettingsStorage Storage { get; set; }

        private async void BrowserWindowLoaded(object sender, RoutedEventArgs e)
        {
            await wbAuth.EnsureCoreWebView2Async(null);

            wbAuth.Source = new Uri("https://steamcommunity.com/login/home/?goto=my/profile");
        }

        private void BrowserInitializationCompleted(object sender, Microsoft.Web.WebView2.Core.CoreWebView2InitializationCompletedEventArgs e)
        {
            if (e.IsSuccess)
            {
                wbAuth.CoreWebView2.CookieManager.DeleteAllCookies();
            }
        }

        private async Task GetProfileAuthCookies()
        {
            var cookies = await wbAuth.CoreWebView2.CookieManager.GetCookiesAsync(wbAuth.Source.AbsoluteUri);
            foreach (var cookie in cookies)
            {
                if (cookie.Name == "sessionid")
                {
                    Storage.SessionId = cookie.Value;
                }

                // Save the "steamLogin" cookie and construct and save the user's profile link
                else if (cookie.Name == "steamLoginSecure")
                {
                    string login = cookie.Value;
                    Storage.SteamLoginSecure = login;

                    var steamId = WebUtility.UrlDecode(login);
                    var index = steamId.IndexOf('|');
                    if (index >= 0)
                        steamId = steamId.Remove(index);
                    Storage.SteamProfileUrl = "https://steamcommunity.com/profiles/" + steamId;
                }
            }
        }

        private static bool IsSteamProfileUrl(Uri url)
        {
            return url.Host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase)
                && (url.AbsolutePath.StartsWith("/id/", StringComparison.OrdinalIgnoreCase)
                    || url.AbsolutePath.StartsWith("/profiles/", StringComparison.OrdinalIgnoreCase));
        }

        private async void BrowserNavigationCompleted(object sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
        {
            // Get the URL of the page that just finished loading
            var url = wbAuth.Source.AbsoluteUri;

            Logger.Info("Navigated to " + url);

            if (!Uri.TryCreate(url, UriKind.Absolute, out var profileUrl) || !IsSteamProfileUrl(profileUrl))
                return;

            await GetProfileAuthCookies();

            // Save all of the data to the program settings file, and close this form
            if (false == String.IsNullOrWhiteSpace(Storage.SteamLoginSecure))
            {
                if (App.CardIdle.IsNewUser)
                {
                    App.CardIdle.IsNewUser = false;
                    Title = Properties.Resources.TradingCardsFAQ;
                    wbAuth.Source = new Uri("https://steamcommunity.com/tradingcards/faq");
                }
                else if (IsSteamProfileUrl(profileUrl))
                {
                    Close();
                }
            }
        }
    }
}
