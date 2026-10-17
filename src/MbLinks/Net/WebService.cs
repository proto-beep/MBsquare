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
    /// <summary>All web access. MusicBrainz requests run one after another, at most about one per second.</summary>
    internal sealed class WebService : IDisposable
    {
        public const double SecondsPerMusicBrainzRequest = 1.1;

        private const string MusicBrainzApi = "https://musicbrainz.org/ws/2/";
        private static readonly TimeSpan MusicBrainzInterval = TimeSpan.FromSeconds(SecondsPerMusicBrainzRequest);

        private readonly HttpClient http;
        private readonly SemaphoreSlim musicBrainzGate = new SemaphoreSlim(1, 1);
        private DateTime nextMusicBrainzSlot = DateTime.MinValue;

        public WebService()
        {
            EnsureTls12();
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ProjectInfo.UserAgent);
        }

        public static bool IsWebUrl(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out Uri uri)
                && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
        }

        public static string DescribeStatus(int status) => status == 0 ? "no connection" : "HTTP " + status;

        /// <summary>GET on the MusicBrainz API; with a token for user data (e.g. own ratings).</summary>
        public async Task<(Dictionary<string, object> Data, bool Ok, int Status)> GetMusicBrainzAsync(string pathAndQuery, CancellationToken ct, string bearerToken = null)
        {
            var result = await SendMusicBrainzAsync(() => CreateGet(MusicBrainzApi + pathAndQuery, bearerToken), ct).ConfigureAwait(false);
            return Interpret(result);
        }

        /// <summary>Submits ratings (MMD XML). Returns the HTTP status, 0 on connection errors.</summary>
        public Task<int> PostRatingsAsync(string xml, string bearerToken, CancellationToken ct) =>
            PostSubmissionAsync("rating", xml, bearerToken, ct);

        /// <summary>Submits MMD XML to a submission endpoint ("rating" or "tag"). Returns the HTTP status, 0 on connection errors.</summary>
        public async Task<int> PostSubmissionAsync(string endpoint, string xml, string bearerToken, CancellationToken ct)
        {
            string url = MusicBrainzApi + endpoint + "?client=" + Uri.EscapeDataString(ProjectInfo.SubmissionClient);
            var result = await SendMusicBrainzAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(xml, Encoding.UTF8, "application/xml")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
                return request;
            }, ct).ConfigureAwait(false);
            return result.Status;
        }

        /// <summary>GET on the MusicBrainz API for plain text answers (e.g. the list of all genres).</summary>
        public async Task<(string Body, bool Ok, int Status)> GetMusicBrainzTextAsync(string pathAndQuery, CancellationToken ct)
        {
            var result = await SendMusicBrainzAsync(() => CreateGet(MusicBrainzApi + pathAndQuery, null), ct).ConfigureAwait(false);
            bool ok = result.Status >= 200 && result.Status < 300 && !string.IsNullOrEmpty(result.Body);
            return (ok ? result.Body : null, ok, result.Status);
        }

        /// <summary>GET on any JSON address (Wikidata, OAuth user info).</summary>
        public async Task<(Dictionary<string, object> Data, bool Ok, int Status)> GetJsonAsync(string url, CancellationToken ct, string bearerToken = null)
        {
            var result = await SendAsync(CreateGet(url, bearerToken), ct).ConfigureAwait(false);
            return Interpret(result);
        }

        /// <summary>POST as a form (OAuth). The JSON response is parsed even for error statuses.</summary>
        public async Task<(Dictionary<string, object> Data, int Status)> PostFormAsync(string url, IEnumerable<KeyValuePair<string, string>> form, CancellationToken ct)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
            var result = await SendAsync(request, ct).ConfigureAwait(false);
            return (Json.Parse(result.Body), result.Status);
        }

        /// <summary>Gets the Wikipedia article in the requested language via Wikidata. Url = null if there is none.</summary>
        public async Task<(string Url, bool Ok, int Status)> GetWikipediaUrlAsync(string qid, string language, CancellationToken ct)
        {
            string site = language.Replace('-', '_') + "wiki";
            string url = "https://www.wikidata.org/w/api.php?action=wbgetentities&format=json&props=sitelinks%2Furls"
                       + "&ids=" + Uri.EscapeDataString(qid)
                       + "&sitefilter=" + Uri.EscapeDataString(site);

            var result = await GetJsonAsync(url, ct).ConfigureAwait(false);
            var entities = Json.Obj(result.Data, "entities");
            if (entities != null)
            {
                foreach (object entity in entities.Values)
                {
                    var sitelinks = Json.Obj(entity as Dictionary<string, object>, "sitelinks");
                    string link = Json.Str(Json.Obj(sitelinks, site), "url");
                    if (IsWebUrl(link)) return (link, true, result.Status);
                }
            }
            return (null, result.Ok, result.Status);
        }

        public async Task<byte[]> TryGetBytesAsync(string url, CancellationToken ct)
        {
            try
            {
                using (HttpResponseMessage response = await http.GetAsync(url, ct).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode) return null;
                    return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { return null; }
        }

        private async Task<(int Status, string Body)> SendMusicBrainzAsync(Func<HttpRequestMessage> createRequest, CancellationToken ct)
        {
            await musicBrainzGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                for (int attempt = 0; ; attempt++)
                {
                    TimeSpan wait = nextMusicBrainzSlot - DateTime.UtcNow;
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
                    nextMusicBrainzSlot = DateTime.UtcNow + MusicBrainzInterval;

                    var result = await SendAsync(createRequest(), ct).ConfigureAwait(false);
                    if (result.Status == 503 && attempt == 0)
                    {
                        // throttled: wait a moment, then try once more
                        nextMusicBrainzSlot = DateTime.UtcNow + TimeSpan.FromSeconds(3);
                        continue;
                    }
                    return result;
                }
            }
            finally
            {
                musicBrainzGate.Release();
            }
        }

        private async Task<(int Status, string Body)> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            try
            {
                using (request)
                using (HttpResponseMessage response = await http.SendAsync(request, ct).ConfigureAwait(false))
                {
                    string body = response.Content == null ? null : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return ((int)response.StatusCode, body);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { return (0, null); }
        }

        private static HttpRequestMessage CreateGet(string url, string bearerToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(bearerToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            return request;
        }

        /// <summary>2xx = data; 4xx (except 401/429) = final answer without data (e.g. unknown ID); otherwise a temporary error.</summary>
        private static (Dictionary<string, object> Data, bool Ok, int Status) Interpret((int Status, string Body) result)
        {
            if (result.Status >= 200 && result.Status < 300)
            {
                var data = Json.Parse(result.Body);
                return (data, data != null, result.Status);
            }
            if (result.Status >= 400 && result.Status < 500 && result.Status != 401 && result.Status != 429)
                return (null, true, result.Status);
            return (null, false, result.Status);
        }

        private static void EnsureTls12()
        {
            try
            {
                SecurityProtocolType current = ServicePointManager.SecurityProtocol;
                if (current != 0 && (current & SecurityProtocolType.Tls12) == 0)
                    ServicePointManager.SecurityProtocol = current | SecurityProtocolType.Tls12;
            }
            catch { }
        }

        public void Dispose()
        {
            http.Dispose();
        }
    }
}
