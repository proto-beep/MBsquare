// MusicBrainz Links – MusicBee plugin
// Note: this code was written with Claude, an AI model by Anthropic. See README.md.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace MusicBeePlugin
{
    /// <summary>One icon in the panel or one entry in the context menu.</summary>
    internal sealed class SiteLink
    {
        public SiteLink(string siteKey, string url, string title, string fallbackText, string badge = null)
        {
            SiteKey = siteKey;
            Url = url;
            Title = title;
            FallbackText = fallbackText;
            Badge = badge;
            IconHost = new Uri(url).Host.ToLowerInvariant();
        }

        public string SiteKey { get; }
        public string Url { get; }
        public string Title { get; }
        public string FallbackText { get; }     // drawn when no favicon can be loaded
        public string Badge { get; }            // "RG" for the MusicBrainz release group, "R" for the release
        public string IconHost { get; }
        public byte[] IconData { get; set; }     // PNG

        public string Tooltip => Title + Environment.NewLine + Url;
    }

    internal sealed class ServiceCount
    {
        public ServiceCount(string siteKey, string name, int count)
        {
            SiteKey = siteKey;
            Name = name;
            Count = count;
        }

        public string SiteKey { get; }
        public string Name { get; }
        public int Count { get; }
    }

    /// <summary>How many links are stored, in total and per service.</summary>
    internal sealed class LinkStatistics
    {
        public int Albums { get; set; }
        public int TotalLinks { get; set; }
        public List<ServiceCount> Services { get; } = new List<ServiceCount>();
    }

    internal sealed class RefreshResult
    {
        public int Total;
        public int Changed;
        public int Failed;

        public string Describe()
        {
            string text = "stored links checked: " + Fmt.Number(Total) + (Total == 1 ? " album" : " albums")
                        + ", " + Fmt.Number(Changed) + " changed";
            if (Failed > 0) text += ", " + Fmt.Number(Failed) + " could not be checked and keep their previous links";
            return text + ".";
        }
    }

    /// <summary>
    /// Determines the links of an album. Results are stored permanently in the LinkCache and are only fetched
    /// again when the user checks all stored links. Which services are shown is decided only when displaying,
    /// so toggling services needs no network access.
    /// </summary>
    internal sealed class LinkService
    {
        private static readonly Regex QidPattern = new Regex(@"^/wiki/(Q\d+)/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly WebService web;
        private readonly IconStore icons;
        private readonly LinkCache cache;

        public LinkService(WebService web, IconStore icons, LinkCache cache)
        {
            this.web = web;
            this.icons = icons;
            this.cache = cache;
        }

        public bool IsLoaded => cache.IsLoaded;
        public int StoredAlbums => cache.Count;

        public void WaitUntilLoaded() => cache.WhenLoaded.Wait();

        public bool IsStored(string key) => cache.Get(key) != null;

        /// <summary>When the links of an album were fetched (UTC), or null if it is not stored.</summary>
        public DateTime? GetCheckedUtc(string key) => cache.Get(key)?.CheckedUtc;

        /// <summary>All existing links of an album: from the store, otherwise fetched from MusicBrainz and stored.</summary>
        public async Task<IList<SiteLink>> GetLinksAsync(MbIds ids, string language, bool includeWikipedia, CancellationToken ct)
        {
            await cache.WhenLoaded.ConfigureAwait(false);

            AlbumLinks entry = cache.Get(ids.CacheKey);
            if (entry == null)
            {
                var fetched = await FetchAsync(ids.CacheKey, ids.ReleaseId, ids.ReleaseGroupId, ct).ConfigureAwait(false);
                entry = fetched.Entry;
                if (fetched.Complete) cache.Put(entry);   // incomplete results (network errors) are not stored
            }

            SiteLink wiki = includeWikipedia ? await ResolveWikiAsync(entry, language, ct).ConfigureAwait(false) : null;
            return BuildLinks(entry, wiki);
        }

        /// <summary>Only what is already stored, without any network access. Empty if the album is not stored.</summary>
        public IList<SiteLink> GetStoredLinks(MbIds ids, string language, bool includeWikipedia)
        {
            AlbumLinks entry = cache.Get(ids.CacheKey);
            if (entry == null) return new List<SiteLink>();

            SiteLink wiki = null;
            if (includeWikipedia)
            {
                StoredUrl direct = FindWikipedia(entry, language);
                StoredUrl wikidata = FindWikidata(entry);
                if (direct != null)
                {
                    wiki = WikipediaLink(direct.Url, language);
                }
                else if (wikidata != null)
                {
                    string article = cache.GetWikipedia(entry, language);
                    wiki = !string.IsNullOrEmpty(article) ? WikipediaLink(article, language) : WikidataLink(wikidata.Url);
                }
            }
            return BuildLinks(entry, wiki);
        }

        public async Task EnsureIconsAsync(IEnumerable<SiteLink> links, CancellationToken ct)
        {
            foreach (SiteLink link in links)
                if (link.IconData == null)
                    link.IconData = await icons.GetAsync(link.IconHost, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Fetches the planned albums (analysis or manual fetch). Stored albums are skipped,
        /// unless refreshStored is set; then their links are fetched again and replaced.
        /// </summary>
        public async Task<AnalysisResult> AnalyzeAsync(IList<MbIds> albums, string language, bool includeWikipedia, bool refreshStored,
                                                       IProgress<string> progress, CancellationToken ct)
        {
            await cache.WhenLoaded.ConfigureAwait(false);
            var result = new AnalysisResult { Total = albums.Count };

            for (int i = 0; i < albums.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report("analyzing … " + Fmt.Number(i + 1) + "/" + Fmt.Number(albums.Count) + " albums, " + Fmt.Remaining(albums.Count - i));

                MbIds ids = albums[i];
                AlbumLinks old = cache.Get(ids.CacheKey);
                if (old != null && !refreshStored)
                {
                    result.AlreadyStored++;   // e.g. played in the meantime
                    continue;
                }

                var fetched = await FetchAsync(ids.CacheKey, ids.ReleaseId, ids.ReleaseGroupId, ct).ConfigureAwait(false);
                if (!fetched.Complete)
                {
                    result.Failed++;
                    continue;
                }

                AlbumLinks fresh = fetched.Entry;
                if (old != null) await CarryOverWikipediaAsync(old, fresh, ct).ConfigureAwait(false);
                cache.Put(fresh);
                if (includeWikipedia) await ResolveWikiAsync(fresh, language, ct).ConfigureAwait(false);
                if (old != null) result.Updated++;
                else result.Fetched++;
            }

            cache.Flush();
            cache.CompactIfNeeded();
            return result;
        }

        /// <summary>Fetches every stored album again from MusicBrainz (and Wikidata) and replaces the stored links.</summary>
        public async Task<RefreshResult> RefreshAllAsync(IProgress<string> progress, CancellationToken ct)
        {
            await cache.WhenLoaded.ConfigureAwait(false);
            List<string> keys = cache.Keys();
            var result = new RefreshResult { Total = keys.Count };

            for (int i = 0; i < keys.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report("checking stored links … " + Fmt.Number(i + 1) + "/" + Fmt.Number(keys.Count) + ", " + Fmt.Remaining(keys.Count - i));

                AlbumLinks old = cache.Get(keys[i]);
                if (old == null) continue;

                ParseKey(old.Key, out string releaseId, out string groupId);
                var fetched = await FetchAsync(old.Key, releaseId, groupId, ct).ConfigureAwait(false);
                if (!fetched.Complete)
                {
                    result.Failed++;
                    continue;
                }

                AlbumLinks fresh = fetched.Entry;
                Dictionary<string, string> oldWikipedia = cache.WikipediaSnapshot(old);
                await CarryOverWikipediaAsync(old, fresh, ct).ConfigureAwait(false);

                if (Signature(old, oldWikipedia) != Signature(fresh, fresh.Wikipedia)) result.Changed++;
                cache.Put(fresh);
            }

            cache.Flush();
            cache.CompactIfNeeded();
            return result;
        }

        /// <summary>
        /// Counts the stored links per service, regardless of whether the service is switched on.
        /// Wikipedia counts articles known for the given language, Wikidata counts albums with a Wikidata item.
        /// Returns null while the store is still loading.
        /// </summary>
        public LinkStatistics GetStatistics(string language)
        {
            List<AlbumLinks> entries = cache.Snapshot();
            if (entries == null) return null;

            Dictionary<string, int> counts = Sites.All.ToDictionary(s => s.Key, s => 0);
            int wikipedia = 0;
            int wikidata = 0;
            string wikipediaHost = language + ".wikipedia.org";

            foreach (AlbumLinks entry in entries)
            {
                List<Uri> uris = entry.Urls.Select(u => TryParse(u.Url)).Where(u => u != null).ToList();

                if (entry.ReleaseGroupId != null && entry.ReleaseGroupExists) counts[Sites.MusicBrainzReleaseGroup]++;
                if (entry.ReleaseId != null && entry.ReleaseExists) counts[Sites.MusicBrainzRelease]++;

                foreach (SiteDefinition site in Sites.All)
                    if (site.Domains.Length > 0 && uris.Any(site.Matches)) counts[site.Key]++;

                if (uris.Any(IsWikidata)) wikidata++;
                if (uris.Any(u => string.Equals(u.Host, wikipediaHost, StringComparison.OrdinalIgnoreCase)) ||
                    !string.IsNullOrEmpty(cache.GetWikipedia(entry, language)))
                    wikipedia++;
            }

            var stats = new LinkStatistics { Albums = entries.Count };
            foreach (SiteDefinition site in Sites.All)
            {
                if (site.Key == Sites.Wikipedia)
                {
                    stats.Services.Add(new ServiceCount(site.Key, "Wikidata", wikidata));
                    stats.Services.Add(new ServiceCount(site.Key, "Wikipedia (" + language + ")", wikipedia));
                }
                else
                {
                    stats.Services.Add(new ServiceCount(site.Key, site.Name, counts[site.Key]));
                }
            }
            stats.TotalLinks = stats.Services.Sum(c => c.Count);
            return stats;
        }

        /// <summary>Looks up again the Wikipedia articles of all languages that were stored for the old entry.</summary>
        private async Task CarryOverWikipediaAsync(AlbumLinks old, AlbumLinks fresh, CancellationToken ct)
        {
            StoredUrl wikidata = FindWikidata(fresh);
            if (wikidata == null) return;
            foreach (string language in cache.WikipediaSnapshot(old).Keys)
            {
                var article = await web.GetWikipediaUrlAsync(QidOf(wikidata), language, ct).ConfigureAwait(false);
                if (article.Ok) fresh.Wikipedia[language] = article.Url ?? "";
                // languages that could not be checked are looked up again when the album is shown
            }
        }

        // ---------- MusicBrainz ----------

        private async Task<(AlbumLinks Entry, bool Complete)> FetchAsync(string key, string releaseId, string groupId, CancellationToken ct)
        {
            var entry = new AlbumLinks
            {
                Key = key,
                ReleaseId = releaseId,
                ReleaseGroupId = groupId,
                CheckedUtc = DateTime.UtcNow
            };
            bool complete = true;
            bool groupDone = false;

            if (releaseId != null)
            {
                // one request: the release, its release group and the URL relationships of both
                var release = await web.GetMusicBrainzAsync(
                    "release/" + releaseId + "?inc=url-rels+release-groups+release-group-level-rels&fmt=json", ct).ConfigureAwait(false);
                complete &= release.Ok;
                // on network errors it is unknown whether the release exists; then the link is shown
                entry.ReleaseExists = release.Data != null || !release.Ok;
                if (!release.Ok)
                    PluginLog.Error("MusicBrainz lookup of release " + releaseId + " failed (" + WebService.DescribeStatus(release.Status) + ").");

                if (release.Data != null)
                {
                    CollectUrls(release.Data, false, entry.Urls);
                    Dictionary<string, object> releaseGroup = Json.Obj(release.Data, "release-group");
                    string current = Json.Str(releaseGroup, "id");
                    if (!string.IsNullOrEmpty(current))
                    {
                        // the current assignment in MusicBrainz takes precedence over the tag (e.g. after merges)
                        entry.ReleaseGroupId = current;
                        entry.ReleaseGroupExists = true;
                        if (releaseGroup.ContainsKey("relations"))
                        {
                            CollectUrls(releaseGroup, true, entry.Urls);
                            groupDone = true;
                        }
                    }
                }
            }

            if (!groupDone && entry.ReleaseGroupId != null)
            {
                // only release group ID known, or the release lookup did not include the release group relationships
                var group = await web.GetMusicBrainzAsync("release-group/" + entry.ReleaseGroupId + "?inc=url-rels&fmt=json", ct).ConfigureAwait(false);
                complete &= group.Ok;
                entry.ReleaseGroupExists = group.Data != null || !group.Ok;
                if (!group.Ok)
                    PluginLog.Error("MusicBrainz lookup of release group " + entry.ReleaseGroupId + " failed (" + WebService.DescribeStatus(group.Status) + ").");
                if (group.Data != null) CollectUrls(group.Data, true, entry.Urls);
            }

            return (entry, complete);
        }

        private static void CollectUrls(Dictionary<string, object> entity, bool releaseGroupLevel, List<StoredUrl> target)
        {
            foreach (object item in Json.Arr(entity, "relations"))
            {
                var rel = item as Dictionary<string, object>;
                if (rel == null || Json.Str(rel, "target-type") != "url") continue;
                if (Json.Bool(rel, "ended")) continue;   // links marked as ended are usually dead

                string resource = Json.Str(Json.Obj(rel, "url"), "resource");
                if (WebService.IsWebUrl(resource))
                    target.Add(new StoredUrl { Url = resource, ReleaseGroupLevel = releaseGroupLevel });
            }
        }

        // ---------- building the links ----------

        /// <summary>Only links that exist: MusicBrainz only if the ID is known there, other services only if linked.</summary>
        private static IList<SiteLink> BuildLinks(AlbumLinks entry, SiteLink wiki)
        {
            var parsed = entry.Urls
                .Select(u => new { Stored = u, Uri = TryParse(u.Url) })
                .Where(x => x.Uri != null)
                .ToList();

            var links = new List<SiteLink>();
            foreach (SiteDefinition site in Sites.All)
            {
                switch (site.Key)
                {
                    case Sites.MusicBrainzReleaseGroup:
                        if (entry.ReleaseGroupId != null && entry.ReleaseGroupExists)
                            links.Add(new SiteLink(site.Key, "https://musicbrainz.org/release-group/" + entry.ReleaseGroupId, site.Name, site.FallbackText, "RG"));
                        break;

                    case Sites.MusicBrainzRelease:
                        if (entry.ReleaseId != null && entry.ReleaseExists)
                            links.Add(new SiteLink(site.Key, "https://musicbrainz.org/release/" + entry.ReleaseId, site.Name, site.FallbackText, "R"));
                        break;

                    case Sites.Wikipedia:
                        if (wiki != null) links.Add(wiki);
                        break;

                    default:
                        // one link per service; release group level before release level
                        var best = parsed
                            .Where(x => site.Matches(x.Uri))
                            .OrderBy(x => x.Stored.ReleaseGroupLevel ? 0 : 1)
                            .FirstOrDefault();
                        if (best != null)
                            links.Add(new SiteLink(site.Key, best.Stored.Url, TitleFor(site, best.Uri), site.FallbackText));
                        break;
                }
            }
            return links;
        }

        private static string TitleFor(SiteDefinition site, Uri uri)
        {
            if (site.Key == Sites.Discogs)
                return uri.AbsolutePath.StartsWith("/master", StringComparison.OrdinalIgnoreCase) ? "Discogs – Master" : "Discogs – Release";
            return site.Name;
        }

        /// <summary>Wikipedia in the requested language, otherwise Wikidata, otherwise nothing.</summary>
        private async Task<SiteLink> ResolveWikiAsync(AlbumLinks entry, string language, CancellationToken ct)
        {
            StoredUrl direct = FindWikipedia(entry, language);
            if (direct != null) return WikipediaLink(direct.Url, language);

            StoredUrl wikidata = FindWikidata(entry);
            if (wikidata == null) return null;

            string article = cache.GetWikipedia(entry, language);
            if (article == null)
            {
                var result = await web.GetWikipediaUrlAsync(QidOf(wikidata), language, ct).ConfigureAwait(false);
                if (result.Ok) cache.SetWikipedia(entry, language, result.Url);
                else PluginLog.Error("Wikidata lookup of " + QidOf(wikidata) + " failed (" + WebService.DescribeStatus(result.Status) + ").");
                article = result.Url;
            }

            return !string.IsNullOrEmpty(article) ? WikipediaLink(article, language) : WikidataLink(wikidata.Url);
        }

        private static SiteLink WikipediaLink(string url, string language) =>
            new SiteLink(Sites.Wikipedia, url, "Wikipedia (" + language + ")", "W");

        private static SiteLink WikidataLink(string url) =>
            new SiteLink(Sites.Wikipedia, url, "Wikidata", "WD");

        private static StoredUrl FindWikipedia(AlbumLinks entry, string language)
        {
            string host = language + ".wikipedia.org";
            return entry.Urls
                .Where(u => string.Equals(TryParse(u.Url)?.Host, host, StringComparison.OrdinalIgnoreCase))
                .OrderBy(u => u.ReleaseGroupLevel ? 0 : 1)
                .FirstOrDefault();
        }

        private static StoredUrl FindWikidata(AlbumLinks entry)
        {
            return entry.Urls
                .Where(u =>
                {
                    Uri uri = TryParse(u.Url);
                    return uri != null && IsWikidata(uri);
                })
                .OrderBy(u => u.ReleaseGroupLevel ? 0 : 1)
                .FirstOrDefault();
        }

        private static bool IsWikidata(Uri uri) =>
            uri.Host.EndsWith("wikidata.org", StringComparison.OrdinalIgnoreCase) && QidPattern.IsMatch(uri.AbsolutePath);

        private static string QidOf(StoredUrl wikidata) =>
            QidPattern.Match(new Uri(wikidata.Url).AbsolutePath).Groups[1].Value.ToUpperInvariant();

        private static Uri TryParse(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out Uri uri) ? uri : null;

        private static void ParseKey(string key, out string releaseId, out string groupId)
        {
            string[] parts = (key ?? "").Split('|');
            releaseId = parts.Length > 0 && parts[0] != "-" && parts[0].Length > 0 ? parts[0] : null;
            groupId = parts.Length > 1 && parts[1] != "-" && parts[1].Length > 0 ? parts[1] : null;
        }

        private static string Signature(AlbumLinks entry, Dictionary<string, string> wikipedia)
        {
            var sb = new StringBuilder();
            sb.Append(entry.ReleaseGroupId).Append('|').Append(entry.ReleaseExists).Append('|').Append(entry.ReleaseGroupExists);
            foreach (StoredUrl u in entry.Urls.OrderBy(x => x.Url, StringComparer.Ordinal).ThenBy(x => x.ReleaseGroupLevel))
                sb.Append('|').Append(u.ReleaseGroupLevel ? 'g' : 'r').Append(u.Url);
            foreach (var kv in wikipedia.OrderBy(x => x.Key, StringComparer.Ordinal))
                sb.Append('|').Append(kv.Key).Append('=').Append(kv.Value);
            return sb.ToString();
        }
    }
}
