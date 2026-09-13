using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Newtonsoft.Json;

namespace CardIdleRemastered
{
    public class SteamParser
    {
        public static async Task<string> DownloadString(string url)
        {
            var wc = new WebClient();
            wc.Encoding = Encoding.UTF8;
            return await wc.DownloadStringTaskAsync(url);
        }

        public static async Task<string> DownloadStringWithAuth(string url)
        {
            return await new CookieClient().DownloadStringTaskAsync(url);
        }

        public async Task<bool> IsLogined(string profileUrl)
        {
            var response = await DownloadStringWithAuth(profileUrl);
            if (String.IsNullOrWhiteSpace(response))
                return false;
            var document = new HtmlDocument();
            document.LoadHtml(response);
            return document.DocumentNode.SelectSingleNode("//div[@class=\"responsive_menu_user_area\"]") != null;
        }

        private static Dictionary<string, GameIdentity> _appsCache;
        public static async Task<IDictionary<string, GameIdentity>> GetSteamApps()
        {
            if (_appsCache == null)
            {
                string response = String.Empty;
                try
                {
                    response = await DownloadString("http://api.steampowered.com/ISteamApps/GetAppList/v2");
                    var data = JsonConvert.DeserializeObject<SteamAppList>(response);
                    _appsCache = new Dictionary<string, GameIdentity>();
                    foreach(var item in data.applist.apps)
                    {
                        _appsCache[item.appid] = item;
                    }
                }
                catch (Exception ex)
                {
                    _appsCache = new Dictionary<string, GameIdentity>();
                    if (!String.IsNullOrWhiteSpace(response))
                        File.WriteAllText("AppList.json", response, Encoding.UTF8);
                    else if (ex is WebException webException && webException.Response is HttpWebResponse httpResponse && httpResponse.StatusCode == HttpStatusCode.NotFound)
                        Logger.Info("Steam app list endpoint is unavailable; continuing without the optional app catalog");
                    else
                        Logger.Exception(ex, "GetSteamApps");
                }
            }
            return _appsCache;
        }

        #region Profiles

        private async Task<HtmlNode> GetSteamHtmlDocument(string profileLink)
        {
            var response = await DownloadStringWithAuth(profileLink);
            var doc = new HtmlDocument();
            doc.LoadHtml(response);
            return doc.DocumentNode;
        }

        private static void ParseProfile(HtmlNode root, ProfileInfo result)
        {
            // profile background
            HtmlNode html = null;
            var nodes = root.SelectNodes("//div[contains(@class, 'no_header profile_page')]");
            if (nodes != null)
                html = nodes.FirstOrDefault();
            string bg = null;
            if (html != null)
            {
                var bgi = html.GetAttributeValue("style", string.Empty);
                if (!string.IsNullOrEmpty(bgi))
                {
                    int lp = bgi.IndexOf('(') + 1;
                    int rp = bgi.IndexOf(')');
                    if (lp > 0 && rp > lp)
                    {
                        string src = bgi.Substring(lp, rp - lp);
                        bg = src.Trim(' ', '\'');
                    }
                }
            }
            result.BackgroundUrl = bg;

            // avatar
            html = root.SelectSingleNode("//div[@class='playerAvatarAutoSizeInner']");
            if (html != null)
            {
                var image = html.SelectSingleNode(".//img[@src]");
                if (image != null)
                    result.AvatarUrl = image.GetAttributeValue("src", null);
            }

            // user name
            html = root.SelectNodes("//span[@class='actual_persona_name']").FirstOrDefault();
            if (html != null)
            {
                result.UserName = html.InnerText;
            }

            // user level
            // same css class for user level and friends level
            var level = root.SelectSingleNode("//span[@class='friendPlayerLevelNum']");
            if (level != null)
                result.Level = level.InnerText.Trim();

            // favorite badge
            var badge = root.SelectSingleNode("//div[@class='profile_header_badge']");
            if (badge != null)
            {
                var badgeImage = badge.SelectSingleNode(".//img[contains(@class, 'badge_icon')]");
                if (badgeImage != null)
                    result.BadgeUrl = badgeImage.GetAttributeValue("src", null);

                var badgeTitle = badge.SelectSingleNode(".//div[@class='favorite_badge_description']//a");
                if (badgeTitle != null)
                    result.BadgeTitle = badgeTitle.InnerText.Trim();
            }
        }

        public async Task<ProfileInfo> LoadProfileAsync(string profileLink)
        {
            var html = await GetSteamHtmlDocument(profileLink);

            var result = new ProfileInfo();

            ParseProfile(html, result);

            return result;
        }

        public async Task<CardIdleProfileInfo> LoadCardIdleProfileAsync()
        {
            string url = "https://steamcommunity.com/profiles/76561198801350858/";

            var html = await GetSteamHtmlDocument(url);

            var result = new CardIdleProfileInfo();
            result.ProfileUrl = url;

            ParseProfile(html, result);

            var titleNode = html.SelectSingleNode("//div[contains(@class, 'profile_customization_header')]");
            if (titleNode != null)
                result.MessageTitle = titleNode.InnerText.Replace("&amp;", "&").Trim();

            var messageNode = html.SelectSingleNode("//div[contains(@class, 'showcase_notes')]");
            if (messageNode != null)
                result.SetMessage(messageNode.InnerText.Replace("&amp;", "&").Trim());


            return result;
        }

        #endregion

        #region Badges

        public async Task<IEnumerable<BadgeModel>> LoadBadgesAsync(string profileLink)
        {
            var document = new HtmlDocument();
            var badges = new List<BadgeModel>();
            int pagesCount = 1;

            try
            {
                // loading pages with user badges
                for (var p = 1; p <= pagesCount; p++)
                {
                    var pageUrl = string.Format("{0}/badges/?p={1}", profileLink, p);
                    var response = await DownloadStringWithAuth(pageUrl);

                    if (string.IsNullOrEmpty(response))
                        return badges;

                    document.LoadHtml(response);

                    if (p == 1)
                    {
                        // get pages count from navigation tab after first request
                        // possible formats depend on badge count, e.g. < 1 2 3 4 5 > or < 1 2 3 ... 15 >
                        var pageNodes = document.DocumentNode.SelectNodes("//a[@class=\"pagelink\"]");
                        if (pageNodes != null)
                            pagesCount = pageNodes
                                .Select(a => a.Attributes["href"].Value)
                                .Select(s => int.Parse(s.Replace("?p=", "")))
                                .Max();
                    }

                    // add all badges from page to list
                    ProcessBadgesOnPage(badges, document);
                }
            }
            catch (Exception ex)
            {
                Logger.Exception(ex, "LoadBadgesAsync");
                return Enumerable.Empty<BadgeModel>();
            }

            foreach (var badge in badges)
                badge.ProfileUrl = profileLink;

            return badges;
        }

        /// <summary>
        /// Processes all badges on page
        /// </summary>
        /// <param name="badges"></param>
        /// <param name="document">HTML document (1 page) from x</param>
        private void ProcessBadgesOnPage(IList<BadgeModel> badges, HtmlDocument document)
        {
            var nodes = document.DocumentNode.SelectNodes("//div[@class=\"badge_row is_link\"]");
            if (nodes == null)
                return;
            foreach (var badge in nodes)
            {
                var appIdNode = badge.SelectSingleNode(".//a[@class=\"badge_row_overlay\"]").Attributes["href"].Value;
                var appid = Regex.Match(appIdNode, @"gamecards/(\d+)/").Groups[1].Value;

                if (string.IsNullOrWhiteSpace(appid) || appid == "368020" || appid == "335590" || appIdNode.Contains("border=1"))
                {
                    continue;
                }

                var hoursNode = badge.SelectSingleNode(".//div[@class=\"badge_title_stats_playtime\"]");
                var hours = hoursNode == null ? string.Empty : Regex.Match(hoursNode.InnerText, @"[0-9\.,]+").Value;

                var nameNode = badge.SelectSingleNode(".//div[@class=\"badge_title\"]");
                var name = WebUtility.HtmlDecode(nameNode.FirstChild.InnerText).Trim();

                var cardNode = badge.SelectSingleNode(".//span[@class=\"progress_info_bold\"]");
                var cards = cardNode == null ? string.Empty : Regex.Match(cardNode.InnerText, @"[0-9]+").Value;

                var progressNode = badge.SelectSingleNode(".//div[@class=\"badge_progress_info\"]");
                HtmlNode craftNode = null;

                int current = 0, total = 0;
                if (progressNode != null)
                {
                    Match m = Regex.Match(progressNode.InnerText, @"([0-9]+)\s.+\s([0-9]+)");
                    string gCurrent = m.Groups[1].Value;
                    string gTotal = m.Groups[2].Value;
                    int.TryParse(gCurrent, out current);
                    int.TryParse(gTotal, out total);

                    craftNode = progressNode.SelectSingleNode("a[@class='badge_craft_button']");
                }

                var unlockedNode = badge.SelectSingleNode(".//div[@class=\"badge_info_title\"]");
                string unlockedName = null;
                if (unlockedNode != null)
                    unlockedName = unlockedNode.InnerText.Trim();

                var badgeModel = new BadgeModel(appid, name, cards, hours, current, total)
                                 {
                                     UnlockedBadge = unlockedName,
                                     CanCraft = craftNode != null
                                 };

                badges.Add(badgeModel);
            }
        }

        #endregion

        #region Showcases

        /// <summary>
        /// Loads common and foil badges for a single game
        /// </summary>
        /// <param name="appId"></param>
        public static async Task<BadgeShowcase> GetBadgeShowcase(string appId)
        {
            string site = "http://www.st3amcard3xchang3.n3t/ind3x.php?gam3pag3-appid-".Replace("3", "e") + appId;

            var response = await DownloadString(site);

            var showcase = new BadgeShowcase(appId, appId);

            var doc = new HtmlDocument();
            doc.LoadHtml(response);

            // parse common badges
            HtmlNode badgesDiv = doc.DocumentNode.SelectSingleNode("//span[@id='series-1-badges']");

            if (badgesDiv != null)
                badgesDiv = badgesDiv.ParentNode.NextSibling;

            while(badgesDiv != null && badgesDiv.Name != "div")
            {
                badgesDiv = badgesDiv.NextSibling;
            }

            if (badgesDiv != null)
            {
                var levels = ParseBadgesContainer(badgesDiv).Take(5);
                foreach (var item in levels)
                    showcase.CommonBadges.Add(item);
            }

            // parse foil badge
            HtmlNode foilDiv = doc.DocumentNode.SelectSingleNode("//span[@id='series-1-foilbadges']");

            if (foilDiv != null)
                foilDiv = foilDiv.ParentNode.NextSibling;

            while (foilDiv != null && foilDiv.Name != "div")
            {
                foilDiv = foilDiv.NextSibling;
            }

            if (foilDiv != null)
            {
                showcase.FoilBadge = ParseBadgesContainer(foilDiv).FirstOrDefault();
                if (showcase.FoilBadge != null)
                    showcase.FoilBadge.Level = "Foil";
            }

            return showcase;
        }

        private static IEnumerable<BadgeLevelData> ParseBadgesContainer(HtmlNode badgesDiv)
        {
            foreach (HtmlNode showcaseDiv in badgesDiv.Descendants("div").Where(n => n.HasClass("p-5")))
            {
                var img = showcaseDiv.SelectSingleNode("img[@class='sm:h-[80px]']");
                if (img == null)
                    continue;

                var nameDiv = showcaseDiv.Descendants("div").Where(n => n.HasClass("text-sm")).FirstOrDefault();

                var levelDiv = showcaseDiv.Descendants("div").Where(n => n.HasClass("text-xs")).FirstOrDefault();

                yield return new BadgeLevelData
                {
                    PictureUrl = img.GetAttributeValue("src", ""),
                    Name = nameDiv?.InnerText ?? img.GetAttributeValue("alt", ""),
                    Level = levelDiv?.InnerText ?? ""
                };
            }
        }

        #endregion

        public static async Task<ReleaseInfo> GetLatestCardIdlerRelease()
        {
            var json = await DownloadString("https://raw.githubusercontent.com/AlexanderSharykin/CardIdleRemastered/master/Release.json");
            return JsonConvert.DeserializeObject<ReleaseInfo>(json);
        }
    }
}
