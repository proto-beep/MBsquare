// MusicBrainz Links – MusicBee plugin
// Note: this code was written with Claude, an AI model by Anthropic. See README.md.

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace MusicBeePlugin
{
    /// <summary>
    /// Error reports for failures in the places where MusicBee calls the plugin (start, panel, notifications).
    /// The full report including the stack trace is written to MbLinks\startup-error.txt in MusicBee's data folder,
    /// or to the Windows temp folder if that folder is not known yet. The short summary names the error and the
    /// first code locations, so it is already useful in MusicBee's error dialog.
    /// </summary>
    internal static class StartupDiagnostics
    {
        public const string FileName = "startup-error.txt";

        /// <summary>Writes the report and returns a short summary that names the error and where it happened.</summary>
        public static string Report(string stage, Exception ex, string musicBeeStoragePath, short apiRevision, short interfaceVersion)
        {
            string summary = Summarize(stage, ex);
            string file = Write(stage, ex, musicBeeStoragePath, apiRevision, interfaceVersion);

            try { PluginLog.Error(stage + " failed: " + ex); } catch { }
            return summary + (file != null ? Environment.NewLine + "Full report: " + file : "");
        }

        private static string Summarize(string stage, Exception ex)
        {
            Exception inner = ex;
            while (inner.InnerException != null) inner = inner.InnerException;

            var sb = new StringBuilder();
            sb.Append(ProjectInfo.Name).Append(' ').Append(ProjectInfo.Version)
              .Append(" – error in ").Append(stage).Append(": ")
              .Append(inner.GetType().Name).Append(": ").Append(inner.Message);

            // the first three code locations of the innermost exception
            int shown = 0;
            foreach (string line in (inner.StackTrace ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                sb.Append(Environment.NewLine).Append("  ").Append(line.Trim());
                if (++shown == 3) break;
            }
            return sb.ToString();
        }

        private static string Write(string stage, Exception ex, string musicBeeStoragePath, short apiRevision, short interfaceVersion)
        {
            var text = new StringBuilder();
            text.AppendLine(ProjectInfo.Name + " " + ProjectInfo.Version + " – error report");
            text.AppendLine("Time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            text.AppendLine("Stage: " + stage);
            text.AppendLine("MusicBee API: interface version " + interfaceVersion + ", API revision " + apiRevision);
            text.AppendLine("Plugin built for: interface version " + Plugin.MinInterfaceVersion + ", API revision " + Plugin.MinApiRevision);
            text.AppendLine(".NET: " + Environment.Version + ", Windows: " + Environment.OSVersion.VersionString +
                            ", " + (Environment.Is64BitProcess ? "64" : "32") + "-bit process");
            text.AppendLine("Culture: " + CultureInfo.CurrentCulture.Name + " / UI " + CultureInfo.CurrentUICulture.Name);
            try { text.AppendLine("Plugin file: " + PluginLog.FileRef(typeof(StartupDiagnostics).Assembly.Location)); } catch { }
            text.AppendLine();
            text.AppendLine(ex.ToString());

            foreach (string folder in Candidates(musicBeeStoragePath))
            {
                try
                {
                    Directory.CreateDirectory(folder);
                    string path = Path.Combine(folder, FileName);
                    File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
                    return path;
                }
                catch { }
            }
            try { Trace.WriteLine(text.ToString()); } catch { }
            return null;
        }

        private static string[] Candidates(string musicBeeStoragePath)
        {
            string temp = null;
            try { temp = Path.GetTempPath(); } catch { }
            string data = null;
            try { if (!string.IsNullOrEmpty(musicBeeStoragePath)) data = Path.Combine(musicBeeStoragePath, "MbLinks"); } catch { }

            if (data != null && temp != null) return new[] { data, temp };
            if (data != null) return new[] { data };
            return temp != null ? new[] { temp } : new string[0];
        }
    }
}
