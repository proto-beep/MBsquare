// Test plugins for finding out why MusicBee cannot enable "MusicBrainz Links". Not part of the plugin.
// Note: this code was written with Claude, an AI model by Anthropic. See README.md.
//
// One source file, five variants (build with -p:Variant=A … E). None of them does anything.
//   A  like MusicBee's sample plugin: author set, version 1.0.1, no panel
//   B  A plus a dockable panel with a header
//   C  A with the plugin information of MusicBrainz Links (empty author, version 0.12.4, long description,
//      panel header text, player and tag events), but without a panel
//   D  the plugin information of MusicBrainz Links including the panel, named "MusicBrainz Links D"
//   E  A without the assembly information that the .NET SDK generates
// Each variant writes the calls it receives from MusicBee to MbLinks\diagnostics.txt in MusicBee's data folder.

using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

#if VARIANT_E
[assembly: System.Reflection.AssemblyTitle("mb_MbTestE")]
[assembly: System.Reflection.AssemblyVersion("1.0.1.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.1.0")]
#endif

namespace MusicBeePlugin
{
    public partial class Plugin
    {
#if VARIANT_A
        private const string Variant = "A";
#elif VARIANT_B
        private const string Variant = "B";
#elif VARIANT_C
        private const string Variant = "C";
#elif VARIANT_D
        private const string Variant = "D";
#else
        private const string Variant = "E";
#endif

        private static readonly object sync = new object();
        private static readonly StringBuilder pending = new StringBuilder();
        private static string logFile;

        private MusicBeeApiInterface mbApi;
        private readonly PluginInfo about = new PluginInfo();

        public Plugin()
        {
            Log("plugin object created" + ThreadInfo());
        }

        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            mbApi = new MusicBeeApiInterface();
            mbApi.Initialise(apiInterfacePtr);
            try { SetLogFolder(mbApi.Setting_GetPersistentStoragePath()); } catch { }

            about.PluginInfoVersion = PluginInfoVersion;
            about.Type = PluginType.General;
            about.MinInterfaceVersion = MinInterfaceVersion;
            about.MinApiRevision = MinApiRevision;
            about.ConfigurationPanelHeight = 0;
#if VARIANT_C || VARIANT_D
#if VARIANT_D
            about.Name = "MusicBrainz Links D";
#else
            about.Name = "MB Test C";
#endif
            about.Description = "Links to MusicBrainz and other services, optional rating sync with MusicBrainz (code written by an AI)";
            about.Author = "";
            about.TargetApplication = about.Name;
            about.VersionMajor = 0;
            about.VersionMinor = 12;
            about.Revision = 4;
            about.ReceiveNotifications = ReceiveNotificationFlags.PlayerEvents | ReceiveNotificationFlags.TagEvents;
#else
            about.Name = "MB Test " + Variant;
            about.Description = "Test plugin " + Variant + " for MusicBrainz Links (does nothing)";
            about.Author = "MBsquare";
#if VARIANT_B
            about.TargetApplication = "MB Test B";
#else
            about.TargetApplication = "";
#endif
            about.VersionMajor = 1;
            about.VersionMinor = 0;
            about.Revision = 1;
            about.ReceiveNotifications = ReceiveNotificationFlags.PlayerEvents;
#endif
            Log("Initialise called" + ThreadInfo() + ", MusicBee API revision " + mbApi.ApiRevision);
            return about;
        }

        public bool Configure(IntPtr panelHandle)
        {
            Log("Configure called");
            MessageBox.Show("MB Test " + Variant + " is enabled and works.", "MB Test " + Variant);
            return true;
        }

        public void SaveSettings()
        {
        }

        public void Close(PluginCloseReason reason)
        {
            Log("Close(" + reason + ") called");
        }

        public void Uninstall()
        {
            Log("Uninstall called");
        }

        public void ReceiveNotification(string sourceFileUrl, NotificationType type)
        {
            if (type == NotificationType.PluginStartup) Log("PluginStartup received: the plugin is enabled" + ThreadInfo());
        }

#if VARIANT_B || VARIANT_D
        public int OnDockablePanelCreated(Control panel)
        {
            Log("OnDockablePanelCreated called" + ThreadInfo());
            float scale = 1f;
            try
            {
                using (Graphics g = panel.CreateGraphics())
                    scale = g.DpiY / 96f;
            }
            catch { }
            panel.Controls.Add(new Label { Text = "MB Test " + Variant, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter });
            return Convert.ToInt32(30 * scale);
        }
#endif

        private static string ThreadInfo()
        {
            try
            {
                Thread thread = Thread.CurrentThread;
                return " (thread " + thread.ManagedThreadId + (thread.IsBackground ? ", background" : "") + ")";
            }
            catch
            {
                return "";
            }
        }

        private static void SetLogFolder(string storagePath)
        {
            if (string.IsNullOrEmpty(storagePath)) return;
            string folder = Path.Combine(storagePath, "MbLinks");
            Directory.CreateDirectory(folder);
            lock (sync)
            {
                logFile = Path.Combine(folder, "diagnostics.txt");
                if (pending.Length > 0)
                {
                    Write(pending.ToString());
                    pending.Clear();
                }
            }
        }

        private static void Log(string text)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) +
                          " [Test " + Variant + "] " + text + Environment.NewLine;
            try
            {
                lock (sync)
                {
                    if (logFile == null) pending.Append(line);
                    else Write(line);
                }
            }
            catch { }
        }

        // called inside the lock; the main plugin may be writing to the same file at the same moment
        private static void Write(string text)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    File.AppendAllText(logFile, text, new UTF8Encoding(false));
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(20);
                }
                catch
                {
                    return;
                }
            }
        }
    }
}
