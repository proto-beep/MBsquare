// MusicBrainz Links – MusicBee plugin
// Note: this code was written with Claude, an AI model by Anthropic. See README.md.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace MusicBeePlugin
{
    /// <summary>
    /// OAuth2 login with MusicBrainz (authorization code with PKCE, out-of-band: the user copies the code).
    /// Only the refresh token, client credentials and user name are stored, encrypted with Windows DPAPI
    /// (only the user's own Windows account can read the file). The plugin never sees the password.
    /// </summary>
    internal sealed class MusicBrainzAuth
    {
        public const string RedirectUri = "urn:ietf:wg:oauth:2.0:oob";
        private const string AuthorizeUrl = "https://musicbrainz.org/oauth2/authorize";
        private const string TokenUrl = "https://musicbrainz.org/oauth2/token";
        private const string RevokeUrl = "https://musicbrainz.org/oauth2/revoke";
        private const string UserInfoUrl = "https://musicbrainz.org/oauth2/userinfo";
        private const string Scopes = "profile rating tag";   // tag: needed to submit genres

        // Whoever publishes the plugin can enter the credentials of their own application registered with
        // MusicBrainz (type "Installed application") here. If left empty, every user enters their own.
        public const string DefaultClientId = "";
        public const string DefaultClientSecret = "";

        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MbLinks.MusicBrainzAuth");

        private readonly WebService web;
        private readonly string tokenFile;
        private readonly SemaphoreSlim refreshLock = new SemaphoreSlim(1, 1);
        private readonly object stateLock = new object();

        private string clientId;
        private string clientSecret;
        private string refreshToken;
        private string accessToken;
        private DateTime accessTokenExpiresUtc;
        private string userName;
        private string grantedScopes;
        private string pendingVerifier;

        public MusicBrainzAuth(WebService web, string folder)
        {
            this.web = web;
            tokenFile = Path.Combine(folder, "auth.dat");
            Load();
        }

        public event Action StateChanged;

        public bool IsLoggedIn { get { lock (stateLock) return !string.IsNullOrEmpty(refreshToken); } }
        public string UserName { get { lock (stateLock) return userName; } }

        /// <summary>True if the login includes the permission to submit tags (genres). Logins from before 0.9.0 do not.</summary>
        public bool CanSubmitTags
        {
            get
            {
                lock (stateLock)
                    return !string.IsNullOrEmpty(refreshToken) &&
                           Array.IndexOf((grantedScopes ?? "").Split(' ', ','), "tag") >= 0;
            }
        }
        public string StoredClientId { get { lock (stateLock) return clientId; } }
        public string StoredClientSecret { get { lock (stateLock) return clientSecret; } }

        /// <summary>Creates the address where the user grants access in the browser.</summary>
        public string BeginLogin(string id)
        {
            pendingVerifier = CreateVerifier();
            return AuthorizeUrl
                + "?response_type=code"
                + "&client_id=" + Uri.EscapeDataString(id)
                + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri)
                + "&scope=" + Uri.EscapeDataString(Scopes)
                + "&access_type=offline"
                + "&code_challenge=" + CreateChallenge(pendingVerifier)
                + "&code_challenge_method=S256";
        }

        /// <summary>Exchanges the code copied by the user for tokens. Returns null on success, otherwise an error text.</summary>
        public async Task<string> CompleteLoginAsync(string code, string id, string secret, CancellationToken ct)
        {
            var form = new List<KeyValuePair<string, string>>
            {
                Pair("grant_type", "authorization_code"),
                Pair("code", (code ?? "").Trim()),
                Pair("client_id", id),
                Pair("redirect_uri", RedirectUri)
            };
            if (!string.IsNullOrEmpty(secret)) form.Add(Pair("client_secret", secret));
            if (!string.IsNullOrEmpty(pendingVerifier)) form.Add(Pair("code_verifier", pendingVerifier));

            var response = await web.PostFormAsync(TokenUrl, form, ct).ConfigureAwait(false);
            string newAccess = Json.Str(response.Data, "access_token");
            string newRefresh = Json.Str(response.Data, "refresh_token");
            if (string.IsNullOrEmpty(newAccess) || string.IsNullOrEmpty(newRefresh))
            {
                string error = DescribeError(response.Data, response.Status);
                PluginLog.Error(error);
                return error;
            }

            string name = null;
            try
            {
                var info = await web.GetJsonAsync(UserInfoUrl, ct, newAccess).ConfigureAwait(false);
                name = Json.Str(info.Data, "sub");
            }
            catch (Exception) { }

            lock (stateLock)
            {
                clientId = id;
                clientSecret = string.IsNullOrEmpty(secret) ? null : secret;
                accessToken = newAccess;
                refreshToken = newRefresh;
                accessTokenExpiresUtc = DateTime.UtcNow.AddSeconds(Json.Number(response.Data, "expires_in") ?? 3600);
                userName = name;
                pendingVerifier = null;
                grantedScopes = Json.Str(response.Data, "scope") ?? Scopes;
            }
            Save();
            PluginLog.Info("Logged in to MusicBrainz" + (string.IsNullOrEmpty(name) ? "." : " as " + name + "."));
            StateChanged?.Invoke();
            return null;
        }

        /// <summary>A valid access token (renewed if necessary), or null when not logged in.</summary>
        public async Task<string> GetAccessTokenAsync(CancellationToken ct)
        {
            lock (stateLock)
            {
                if (string.IsNullOrEmpty(refreshToken)) return null;
                if (!string.IsNullOrEmpty(accessToken) && DateTime.UtcNow < accessTokenExpiresUtc.AddMinutes(-1)) return accessToken;
            }

            await refreshLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                string id, secret, refresh;
                lock (stateLock)
                {
                    if (!string.IsNullOrEmpty(accessToken) && DateTime.UtcNow < accessTokenExpiresUtc.AddMinutes(-1)) return accessToken;
                    id = clientId;
                    secret = clientSecret;
                    refresh = refreshToken;
                }
                if (string.IsNullOrEmpty(refresh)) return null;

                var form = new List<KeyValuePair<string, string>>
                {
                    Pair("grant_type", "refresh_token"),
                    Pair("refresh_token", refresh),
                    Pair("client_id", id ?? "")
                };
                if (!string.IsNullOrEmpty(secret)) form.Add(Pair("client_secret", secret));

                var response = await web.PostFormAsync(TokenUrl, form, ct).ConfigureAwait(false);
                string newAccess = Json.Str(response.Data, "access_token");
                if (string.IsNullOrEmpty(newAccess))
                {
                    // expired or revoked login: log out; stay logged in on network errors
                    bool expired = response.Status == 400 || response.Status == 401;
                    PluginLog.Error("Renewing the MusicBrainz login failed (" + WebService.DescribeStatus(response.Status) + ")" +
                                    (expired ? "; logged out, please log in again." : "."));
                    if (expired) Clear();
                    return null;
                }

                lock (stateLock)
                {
                    accessToken = newAccess;
                    accessTokenExpiresUtc = DateTime.UtcNow.AddSeconds(Json.Number(response.Data, "expires_in") ?? 3600);
                    string newRefresh = Json.Str(response.Data, "refresh_token");
                    if (!string.IsNullOrEmpty(newRefresh)) refreshToken = newRefresh;
                }
                Save();
                return newAccess;
            }
            finally
            {
                refreshLock.Release();
            }
        }

        /// <summary>After a 401 response: get a new access token on the next request.</summary>
        public void InvalidateAccessToken()
        {
            lock (stateLock) accessToken = null;
        }

        public async Task LogoutAsync(CancellationToken ct)
        {
            string id, secret, refresh;
            lock (stateLock)
            {
                id = clientId;
                secret = clientSecret;
                refresh = refreshToken;
            }

            if (!string.IsNullOrEmpty(refresh))
            {
                var form = new List<KeyValuePair<string, string>>
                {
                    Pair("token", refresh),
                    Pair("client_id", id ?? "")
                };
                if (!string.IsNullOrEmpty(secret)) form.Add(Pair("client_secret", secret));
                try { await web.PostFormAsync(RevokeUrl, form, ct).ConfigureAwait(false); }
                catch (Exception) { }   // revoking is optional; the local logout happens anyway
            }
            Clear();
            PluginLog.Info("Logged out of MusicBrainz.");
        }

        private void Clear()
        {
            lock (stateLock)
            {
                refreshToken = null;
                accessToken = null;
                userName = null;
            }
            Save();
            StateChanged?.Invoke();
        }

        private static string DescribeError(Dictionary<string, object> data, int status)
        {
            string detail = Json.Str(data, "error_description") ?? Json.Str(data, "error");
            if (detail != null) return "Login failed: " + detail;
            return status == 0 ? "Login failed: no connection to MusicBrainz." : "Login failed (HTTP " + status + ").";
        }

        private void Save()
        {
            try
            {
                XElement x;
                lock (stateLock)
                {
                    x = new XElement("auth",
                        new XElement("clientId", clientId ?? ""),
                        new XElement("clientSecret", clientSecret ?? ""),
                        new XElement("refreshToken", refreshToken ?? ""),
                        new XElement("userName", userName ?? ""),
                        new XElement("scopes", grantedScopes ?? ""));
                }
                byte[] cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(x.ToString()), Entropy, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(tokenFile, cipher);
            }
            catch { }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(tokenFile)) return;
                byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(tokenFile), Entropy, DataProtectionScope.CurrentUser);
                XElement x = XElement.Parse(Encoding.UTF8.GetString(plain));
                clientId = NullIfEmpty((string)x.Element("clientId"));
                clientSecret = NullIfEmpty((string)x.Element("clientSecret"));
                refreshToken = NullIfEmpty((string)x.Element("refreshToken"));
                userName = NullIfEmpty((string)x.Element("userName"));
                grantedScopes = NullIfEmpty((string)x.Element("scopes")) ?? "profile rating";   // older logins had no tag permission
            }
            catch { }
        }

        private static string NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

        private static KeyValuePair<string, string> Pair(string key, string value) => new KeyValuePair<string, string>(key, value);

        private static string CreateVerifier()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Base64Url(bytes);
        }

        private static string CreateChallenge(string verifier)
        {
            using (var sha = SHA256.Create())
                return Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
        }

        private static string Base64Url(byte[] data) =>
            Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
