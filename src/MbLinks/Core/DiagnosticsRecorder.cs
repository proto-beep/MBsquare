// MusicBrainz Links – MusicBee plugin
// Note: this code was written with Claude, an AI model by Anthropic. See README.md.

using System;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;

namespace MusicBeePlugin
{
    /// <summary>
    /// Records which calls MusicBee makes to the plugin, and every exception thrown in MusicBee's process during the
    /// first minutes after the plugin is loaded or started – also exceptions in MusicBee's own code that MusicBee
    /// catches itself. Each exception is recorded with the full call stack at the moment it is thrown. This finds
    /// errors such as "Unable to initialise plugin" that happen inside MusicBee after the plugin has returned.
    /// Written to MbLinks\diagnostics.txt in MusicBee's data folder; paths are shortened like in the log.
    /// </summary>
    internal static class DiagnosticsRecorder
    {
        public const string FileName = "diagnostics.txt";

        private static readonly TimeSpan WatchTime = TimeSpan.FromMinutes(15);
        private const int MaxNullReferences = 15;
        private const int MaxOtherExceptions = 30;
        private const int MaxStackLines = 60;
        private const long MaxFileBytes = 2 * 1024 * 1024;
        private const int MaxBufferChars = 200 * 1024;

        private static readonly object sync = new object();
        private static readonly StringBuilder buffer = new StringBuilder();   // until the data folder is known
        private static string file;
        private static long watchUntilTicks;
        private static int nullReferences;
        private static int otherExceptions;
        private static bool hooked;

        [ThreadStatic] private static bool busy;

        public static string FilePath => file;

        /// <summary>Starts recording, or extends it and resets the counters.</summary>
        public static void Begin()
        {
            bool wasBusy = busy;
            busy = true;
            try
            {
                lock (sync)
                {
                    Interlocked.Exchange(ref watchUntilTicks, DateTime.UtcNow.Add(WatchTime).Ticks);
                    Interlocked.Exchange(ref nullReferences, 0);
                    Interlocked.Exchange(ref otherExceptions, 0);
                    if (!hooked)
                    {
                        AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
                        hooked = true;
                    }
                }
            }
            catch { }
            finally { busy = wasBusy; }
        }

        /// <summary>From now on, entries go to MbLinks\diagnostics.txt; earlier entries are written there as well.</summary>
        public static void SetFolder(string dataFolder)
        {
            bool wasBusy = busy;
            busy = true;
            try
            {
                lock (sync)
                {
                    string target = Path.Combine(dataFolder, FileName);
                    if (target == file) return;
                    file = target;
                    try
                    {
                        var info = new FileInfo(target);
                        if (info.Exists && info.Length > MaxFileBytes) info.Delete();
                    }
                    catch { }
                    if (buffer.Length > 0)
                    {
                        WriteToFile(buffer.ToString());
                        buffer.Clear();
                    }
                }
            }
            catch { }
            finally { busy = wasBusy; }
        }

        /// <summary>One line about a call from MusicBee, e.g. "Initialise called".</summary>
        public static void Note(string text)
        {
            Append(Stamp() + " " + text + Environment.NewLine);
        }

        private static void OnFirstChanceException(object sender, FirstChanceExceptionEventArgs e)
        {
            if (busy) return;   // an exception inside this handler
            if (DateTime.UtcNow.Ticks > Interlocked.Read(ref watchUntilTicks)) return;

            Exception ex = e.Exception;
            if (ex == null || ex is OperationCanceledException || ex is ThreadAbortException) return;

            busy = true;
            try
            {
                bool isNull = ex is NullReferenceException;
                int count = isNull ? Interlocked.Increment(ref nullReferences) : Interlocked.Increment(ref otherExceptions);
                if (count > (isNull ? MaxNullReferences : MaxOtherExceptions)) return;

                Thread thread = Thread.CurrentThread;
                var sb = new StringBuilder();
                sb.Append(Stamp()).Append(" EXCEPTION on thread ").Append(thread.ManagedThreadId)
                  .Append(thread.IsBackground ? " (background)" : "")
                  .Append(": ").Append(ex.GetType().FullName).Append(": ").Append(ex.Message).AppendLine();

                string thrownAt = FirstLine(ex.StackTrace);
                if (thrownAt.Length > 0) sb.Append("    thrown at: ").AppendLine(thrownAt);

                sb.AppendLine("    call stack:");
                int shown = 0;
                foreach (string raw in Environment.StackTrace.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length == 0) continue;
                    if (line.IndexOf("System.Environment.", StringComparison.Ordinal) >= 0) continue;
                    if (line.IndexOf(nameof(DiagnosticsRecorder), StringComparison.Ordinal) >= 0) continue;
                    sb.Append("      ").AppendLine(line);
                    if (++shown == MaxStackLines)
                    {
                        sb.AppendLine("      …");
                        break;
                    }
                }
                Append(sb.ToString());
            }
            catch { }
            finally { busy = false; }
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            foreach (string line in text.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0) return trimmed;
            }
            return "";
        }

        private static string Stamp() =>
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

        private static void Append(string text)
        {
            bool wasBusy = busy;
            busy = true;
            try
            {
                lock (sync)
                {
                    if (file != null)
                        WriteToFile(text);
                    else if (buffer.Length < MaxBufferChars)
                        buffer.Append(text);
                }
            }
            catch { }
            finally { busy = wasBusy; }
        }

        // called inside the lock
        private static void WriteToFile(string text)
        {
            try { File.AppendAllText(file, PluginLog.Clean(text), new UTF8Encoding(false)); } catch { }
        }
    }
}
