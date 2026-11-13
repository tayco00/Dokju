using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Dokju
{
    internal sealed class UpdateOffer
    {
        internal string Repository;
        internal string Tag;
        internal string Commit;
        internal long Sequence;
        internal long Size;
        internal string Hash;
        internal Uri Download;
        internal string Caption { get { return "Build " + Sequence + " · " + Commit.Substring(0, 8); } }
    }

    internal sealed class GitHubUpdates
    {
        private const int Limit = 150 * 1024 * 1024;
        private static readonly HttpClient DefaultHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        private static readonly string[] Required = { Installation.Desktop, Installation.Launcher, "release.json", "backend/server.mjs" };
        private readonly HttpClient http;

        internal GitHubUpdates(HttpClient http = null) { this.http = http ?? DefaultHttp; }

        internal async Task<UpdateOffer> Check(string repository, long sequence, string rejected, CancellationToken cancellation)
        {
            if (!Installation.RepositoryIsValid(repository)) throw new InvalidDataException("Bitte ein GitHub-Repository in der Form Konto/Repository angeben.");
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var url = new Uri("https://api.github.com/repos/" + repository + "/releases?per_page=30");
                using (var response = await Send(url, true, timeout.Token).ConfigureAwait(false))
                {
                    if (response.StatusCode == HttpStatusCode.NotFound)
                        throw new IOException("Das Repository ist nicht öffentlich erreichbar oder existiert noch nicht.");
                    response.EnsureSuccessStatusCode();
                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var buffer = new MemoryStream())
                    {
                        await Copy(stream, buffer, 4000000, null, timeout.Token).ConfigureAwait(false);
                        return Select(System.Text.Encoding.UTF8.GetString(buffer.ToArray()), repository, sequence, rejected);
                    }
                }
            }
        }

        internal static UpdateOffer Select(string json, string repository, long installed, string rejected)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = 4000000 };
            var releases = serializer.Deserialize<Release[]>(json) ?? new Release[0];
            UpdateOffer selected = null;
            foreach (var release in releases)
            {
                if (release == null || release.draft || release.prerelease || release.tag_name == rejected || !Installation.IsTag(release.tag_name)) continue;
                var parts = release.tag_name.Split('-');
                long sequence = Int64.Parse(parts[1], CultureInfo.InvariantCulture);
                if (sequence <= installed || (selected != null && sequence <= selected.Sequence)) continue;
                var path = "/" + repository + "/releases/download/" + release.tag_name + "/" + Installation.Package;
                foreach (var asset in release.assets ?? new Asset[0])
                {
                    if (asset == null || asset.name != Installation.Package || asset.state != "uploaded" || asset.size <= 0 || asset.size > Limit ||
                        asset.digest == null || !Regex.IsMatch(asset.digest, @"\Asha256:[a-f0-9]{64}\z") ||
                        !String.Equals(asset.browser_download_url, "https://github.com" + path, StringComparison.OrdinalIgnoreCase)) continue;
                    selected = new UpdateOffer
                    {
                        Repository = repository, Tag = release.tag_name, Sequence = sequence, Commit = parts[2],
                        Download = new Uri("https://github.com" + path), Size = asset.size, Hash = asset.digest.Substring(7)
                    };
                    break;
                }
            }
            return selected;
        }

        internal async Task Stage(string root, UpdateOffer offer, IProgress<int> progress, CancellationToken cancellation)
        {
            var target = Installation.VersionPath(root, offer.Tag);
            var parent = Path.GetDirectoryName(target);
            Directory.CreateDirectory(parent);
            var work = Path.Combine(parent, ".staging-" + Guid.NewGuid().ToString("N"));
            var zip = Path.Combine(work, "download.zip");
            var content = Path.Combine(work, "bin");
            Directory.CreateDirectory(content);
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {
                    timeout.CancelAfter(TimeSpan.FromMinutes(3));
                    await Download(offer, zip, progress, timeout.Token).ConfigureAwait(false);
                }
                cancellation.ThrowIfCancellationRequested();
                Extract(zip, content, offer, cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (Directory.Exists(target))
                {
                    var state = Installation.Versions(root);
                    if (state.active == offer.Tag || state.previous == offer.Tag)
                        throw new IOException("Diese Version ist bereits installiert.");
                    Directory.Move(target, Path.Combine(work, "replaced"));
                }
                Directory.Move(content, target);
            }
            finally { try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }

        private async Task Download(UpdateOffer offer, string file, IProgress<int> progress, CancellationToken cancellation)
        {
            var url = offer.Download;
            for (int redirects = 0; redirects < 4; redirects++)
            {
                using (var response = await Send(url, false, cancellation).ConfigureAwait(false))
                {
                    if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
                    {
                        url = response.Headers.Location;
                        if (url == null || !url.IsAbsoluteUri || url.Scheme != "https" || !url.IsDefaultPort || url.UserInfo.Length != 0 ||
                            (url.Host != "release-assets.githubusercontent.com" && url.Host != "objects.githubusercontent.com"))
                            throw new IOException("GitHub hat das Update an eine unerwartete Adresse umgeleitet.");
                        continue;
                    }
                    response.EnsureSuccessStatusCode();
                    using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var target = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                    {
                        Action<long> report = progress == null ? (Action<long>)null : length => progress.Report((int)(100 * length / offer.Size));
                        var total = await Copy(source, target, offer.Size, report, cancellation).ConfigureAwait(false);
                        if (total != offer.Size) throw new IOException("Der Download ist unvollständig.");
                    }
                    return;
                }
            }
            throw new IOException("GitHub hat den Download zu oft umgeleitet.");
        }

        private async Task<HttpResponseMessage> Send(Uri url, bool api, CancellationToken cancellation)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.UserAgent.ParseAdd("Dokju-Windows/2.0");
                if (api) request.Headers.Accept.ParseAdd("application/vnd.github+json");
                return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            }
        }

        private static async Task<long> Copy(Stream source, Stream target, long maximum, Action<long> progress, CancellationToken cancellation)
        {
            var buffer = new byte[65536];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, 0, buffer.Length, cancellation).ConfigureAwait(false)) != 0)
            {
                total += read;
                if (total > maximum) throw new InvalidDataException("Die heruntergeladene Datei ist zu groß.");
                await target.WriteAsync(buffer, 0, read, cancellation).ConfigureAwait(false);
                if (progress != null) progress(total);
            }
            return total;
        }

        internal static void Extract(string package, string directory, UpdateOffer offer, CancellationToken cancellation)
        {
            using (var stream = File.OpenRead(package))
            using (var sha = SHA256.Create())
            {
                var hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                if (!String.Equals(hash, offer.Hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Die SHA-256-Prüfsumme des Updates stimmt nicht.");
                stream.Position = 0;
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    const string prefix = "Dokju/bin/";
                    var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    long total = 0;
                    if (zip.Entries.Count > 256) throw new InvalidDataException("Das Update enthält zu viele Dateien.");
                    foreach (var entry in zip.Entries.Where(item => item.FullName.StartsWith(prefix, StringComparison.Ordinal)))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var name = entry.FullName.Substring(prefix.Length);
                        if (!(Required.Contains(name) || name == "node.exe" || name == "NODE-LICENSE.txt" ||
                            Regex.IsMatch(name, @"\Abackend/[a-z][a-z0-9.-]*\.mjs\z")) || !files.Add(name))
                            throw new InvalidDataException("Das Update enthält einen unerwarteten oder doppelten Programmpfad.");
                        if (entry.Length <= 0 || entry.Length > Limit - total)
                            throw new InvalidDataException("Die entpackten Programmdateien sind zu groß oder leer.");
                        total += entry.Length;
                        var target = Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        using (var input = entry.Open())
                        using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                        {
                            var actual = Copy(input, output, entry.Length, null, cancellation).GetAwaiter().GetResult();
                            if (actual != entry.Length) throw new InvalidDataException("Das Archiv enthält eine unvollständige Programmdatei.");
                        }
                    }
                    if (Required.Any(name => !files.Contains(name)) || files.Contains("node.exe") != files.Contains("NODE-LICENSE.txt"))
                        throw new InvalidDataException("Das Update enthält nicht alle benötigten Programmdateien.");
                }
            }
            var build = BuildIdentity.Read(directory);
            if (build.tag != offer.Tag || build.sequence != offer.Sequence || build.commit != offer.Commit ||
                !String.Equals(build.repository, offer.Repository, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Der Inhalt des Updates stimmt nicht mit dem veröffentlichten Commit überein.");
        }

        public sealed class Release
        {
            public string tag_name { get; set; }
            public bool draft { get; set; }
            public bool prerelease { get; set; }
            public Asset[] assets { get; set; }
        }

        public sealed class Asset
        {
            public string name { get; set; }
            public string state { get; set; }
            public long size { get; set; }
            public string digest { get; set; }
            public string browser_download_url { get; set; }
        }
    }
}
