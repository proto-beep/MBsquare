// MusicBrainz Links – MusicBee plugin
// Note: this code was written with Claude, an AI model by Anthropic. See README.md.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MusicBeePlugin
{
    /// <summary>
    /// Downloads the favicon of a website once and stores it as PNG in the plugin data folder.
    /// Order: the site's own /favicon.ico, then DuckDuckGo's favicon service.
    /// </summary>
    internal sealed class IconStore
    {
        private static readonly byte[] Missing = new byte[0];

        private readonly WebService web;
        private readonly string folder;
        private readonly ConcurrentDictionary<string, byte[]> memory =
            new ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        public IconStore(WebService web, string folder)
        {
            this.web = web;
            this.folder = folder;
            try { Directory.CreateDirectory(folder); } catch { }
        }

        public async Task<byte[]> GetAsync(string host, CancellationToken ct)
        {
            if (memory.TryGetValue(host, out byte[] known)) return known.Length == 0 ? null : known;

            string file = Path.Combine(folder, SafeFileName(host) + ".png");
            try
            {
                if (File.Exists(file))
                {
                    byte[] fromDisk = File.ReadAllBytes(file);
                    memory[host] = fromDisk;
                    return fromDisk;
                }
            }
            catch { }

            string[] sources =
            {
                "https://" + host + "/favicon.ico",
                "https://icons.duckduckgo.com/ip3/" + host + ".ico"
            };

            byte[] png = null;
            foreach (string url in sources)
            {
                byte[] raw = await web.TryGetBytesAsync(url, ct).ConfigureAwait(false);
                png = raw == null ? null : IconDecoder.ToPng(raw);
                if (png != null) break;
            }

            if (png == null)
            {
                memory[host] = Missing;   // do not retry in this session; a fallback icon is drawn
                return null;
            }

            memory[host] = png;
            try { File.WriteAllBytes(file, png); } catch { }
            return png;
        }

        /// <summary>All stored favicons as (website host, PNG data), sorted by host.</summary>
        public List<KeyValuePair<string, byte[]>> ListStored()
        {
            var list = new List<KeyValuePair<string, byte[]>>();
            try
            {
                if (!Directory.Exists(folder)) return list;
                foreach (string file in Directory.GetFiles(folder, "*.png").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    try { list.Add(new KeyValuePair<string, byte[]>(Path.GetFileNameWithoutExtension(file), File.ReadAllBytes(file))); }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error("Could not list the stored icons: " + ex.Message);
            }
            return list;
        }

        public string Folder => folder;

        /// <summary>Forgets all favicons; they are downloaded again the next time they are needed.</summary>
        public void Clear()
        {
            memory.Clear();
            try
            {
                foreach (string file in Directory.GetFiles(folder, "*.png"))
                    File.Delete(file);
            }
            catch (Exception ex)
            {
                PluginLog.Error("Could not delete the stored icons: " + ex.Message);
            }
        }

        private static string SafeFileName(string host)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                host = host.Replace(c, '_');
            return host;
        }
    }

    internal static class IconDecoder
    {
        /// <summary>Converts ICO, PNG, GIF or JPEG to PNG; null if the data is not a usable image.</summary>
        public static byte[] ToPng(byte[] data)
        {
            Bitmap bitmap = TryIcon(data) ?? TryImage(data);
            if (bitmap == null) return null;
            try
            {
                if (bitmap.Width < 8 || bitmap.Height < 8) return null;   // placeholder pixels
                using (var ms = new MemoryStream())
                {
                    bitmap.Save(ms, ImageFormat.Png);
                    return ms.ToArray();
                }
            }
            finally
            {
                bitmap.Dispose();
            }
        }

        private static Bitmap TryIcon(byte[] data)
        {
            // ICO header: 00 00 01 00
            if (data.Length < 6 || data[0] != 0 || data[1] != 0 || data[2] != 1 || data[3] != 0) return null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var icon = new Icon(ms, 64, 64))   // picks the closest size contained in the file
                    return icon.ToBitmap();
            }
            catch
            {
                return null;
            }
        }

        private static Bitmap TryImage(byte[] data)
        {
            try
            {
                using (var ms = new MemoryStream(data))
                using (Image image = Image.FromStream(ms))
                    return new Bitmap(image);
            }
            catch
            {
                return null;
            }
        }
    }
}
