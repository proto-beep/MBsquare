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
    /// <summary>Small JSON helpers without an extra library.</summary>
    internal static class Json
    {
        public static Dictionary<string, object> Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try
            {
                return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(text) as Dictionary<string, object>;
            }
            catch
            {
                return null;
            }
        }

        public static Dictionary<string, object> Obj(Dictionary<string, object> o, string key)
        {
            return o != null && o.TryGetValue(key, out object v) ? v as Dictionary<string, object> : null;
        }

        public static string Str(Dictionary<string, object> o, string key)
        {
            return o != null && o.TryGetValue(key, out object v) ? v as string : null;
        }

        public static bool Bool(Dictionary<string, object> o, string key)
        {
            return o != null && o.TryGetValue(key, out object v) && v is bool b && b;
        }

        public static double? Number(Dictionary<string, object> o, string key)
        {
            return o != null && o.TryGetValue(key, out object v) ? ToDouble(v) : null;
        }

        public static double? ToDouble(object value)
        {
            if (value == null || value is string || value is bool) return null;
            try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
            catch { return null; }
        }

        public static IEnumerable<object> Arr(Dictionary<string, object> o, string key)
        {
            if (o != null && o.TryGetValue(key, out object v) && v is IEnumerable e && !(v is string) && !(v is IDictionary))
                return e.Cast<object>();
            return Enumerable.Empty<object>();
        }
    }
}
