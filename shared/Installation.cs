using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Dokju
{
    public sealed class BuildIdentity
    {
        public int format { get; set; }
        public string repository { get; set; }
        public string tag { get; set; }
        public long sequence { get; set; }
        public string commit { get; set; }

        public static BuildIdentity Read(string directory)
        {
            var path = Path.Combine(directory, "release.json");
            if (!File.Exists(path)) return new BuildIdentity();
            var value = Installation.ReadJson<BuildIdentity>(path);
            if (value == null || value.format != 1 || !Installation.RepositoryIsValid(value.repository) ||
                !Installation.IsTag(value.tag) || !Installation.IsCommit(value.commit) ||
                value.sequence <= 0 || value.tag != Installation.TagFor(value.sequence, value.commit))
                throw new InvalidDataException("Die Versionsdaten von Dokju sind ungültig.");
            return value;
        }
    }

    public sealed class InstalledVersions
    {
        public string active { get; set; }
        public string previous { get; set; }
        public string rejected { get; set; }
    }

    internal static class Installation
    {
        internal const int AlreadyRunning = 23;
        internal const string Desktop = "Dokju.exe";
        internal const string Launcher = "Dokju.Start.exe";
        internal const string Package = "Dokju.zip";

        internal static string Root(string data) { return Path.Combine(data, "App"); }
        internal static string ActivationSignal(string root, Guid request) { return Path.Combine(root, "activate-" + request.ToString("N")); }
        internal static string DataDirectory
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dokju"); }
        }

        internal static bool RepositoryIsValid(string value)
        {
            if (value == null || !Regex.IsMatch(value, @"\A[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9._-]{1,100}\z")) return false;
            var name = value.Substring(value.IndexOf('/') + 1);
            return name != "." && name != "..";
        }

        internal static bool IsCommit(string value) { return value != null && Regex.IsMatch(value, @"\A[0-9a-f]{40}\z"); }
        internal static bool IsTag(string value) { return value != null && Regex.IsMatch(value, @"\Adokju-[1-9][0-9]{0,17}-[0-9a-f]{40}\z"); }
        internal static string TagFor(long sequence, string commit)
        {
            return "dokju-" + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + commit;
        }

        internal static string VersionPath(string root, string tag)
        {
            if (!IsTag(tag)) throw new InvalidDataException("Ungültiges Update-Verzeichnis.");
            return Path.Combine(root, "versions", tag);
        }

        internal static InstalledVersions Versions(string root)
        {
            var file = Path.Combine(root, "versions.json");
            return File.Exists(file) ? ReadJson<InstalledVersions>(file) ?? new InstalledVersions() : new InstalledVersions();
        }

        internal static long ActiveSequence(string root, InstalledVersions state, string repository)
        {
            if (!IsTag(state.active)) return 0;
            try
            {
                var active = BuildIdentity.Read(VersionPath(root, state.active));
                return String.Equals(active.repository, repository, StringComparison.OrdinalIgnoreCase) ? active.sequence : 0;
            }
            catch (IOException) { return 0; }
            catch (InvalidDataException) { return 0; }
            catch (ArgumentException) { return 0; }
        }

        internal static void SaveVersions(string root, InstalledVersions state)
        {
            WriteAtomic(Path.Combine(root, "versions.json"), Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(state)));
        }

        internal static T ReadJson<T>(string file)
        {
            if (new FileInfo(file).Length > 32768) throw new InvalidDataException("Die Konfigurationsdatei ist zu groß.");
            return new JavaScriptSerializer().Deserialize<T>(File.ReadAllText(file));
        }

        internal static void WriteAtomic(string path, byte[] bytes)
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal static Process Launch(string executable, string arguments)
        {
            return Process.Start(new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executable)
            });
        }
    }
}
