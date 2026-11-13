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
        private const string Manifest = "Dokju-update.json";
        private static readonly HttpClient DefaultHttp = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        private static readonly string[] Required = { Installation.Desktop, Installation.Launcher, "release.json", "backend/server.mjs" };
        private readonly HttpClient http;

        internal GitHubUpdates(HttpClient http = null) { this.http = http ?? DefaultHttp; }

        internal async Task<UpdateOffer> Check(string repository, long sequence, string rejected, CancellationToken cancellation)
        {
            if (!Installation.RepositoryIsValid(repository)) throw new InvalidDataException("Bitte ein GitHub-Repository in der Form Konto/Repository angeben.");
            Exception failure;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                var url = new Uri("https://api.github.com/repos/" + repository + "/releases?per_page=30");
                try
                {
                    using (var response = await Send(url, true, timeout.Token).ConfigureAwait(false))
                    {
                        RequireSuccess(response);
                        var json = await ReadText(response, 4000000, timeout.Token).ConfigureAwait(false);
                        try { return Select(json, repository, sequence, rejected); }
                        catch (ArgumentException error) { throw new IOException("GitHub hat ungültige Release-Daten geliefert.", error); }
                        catch (InvalidOperationException error) { throw new IOException("GitHub hat ungültige Release-Daten geliefert.", error); }
                    }
                }
                catch (OperationCanceledException error) { cancellation.ThrowIfCancellationRequested(); failure = error; }
                catch (HttpRequestException error) { failure = error; }
                catch (IOException error) { failure = error; }
            }
            try { return await CheckPublishedManifest(repository, sequence, rejected, cancellation).ConfigureAwait(false); }
            catch (OperationCanceledException) { cancellation.ThrowIfCancellationRequested(); }
            catch (HttpRequestException) { }
            catch (IOException) { }
            throw failure;
        }

        internal async Task<UpdateOffer> CheckPublishedManifest(string repository, long sequence, string rejected, CancellationToken cancellation)
        {
            if (!Installation.RepositoryIsValid(repository)) throw new InvalidDataException("Ungültiges Update-Repository.");
            var url = new Uri("https://github.com/" + repository + "/releases/latest/download/" + Manifest);
            var assetPath = @"\A/" + Regex.Escape(repository) + @"/releases/download/(dokju-[1-9][0-9]{0,17}-[a-f0-9]{40})/" + Regex.Escape(Manifest) + @"\z";
            string tag = null;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(7));
                for (int redirects = 0; redirects < 4; redirects++)
                {
                    using (var response = await Send(url, false, timeout.Token).ConfigureAwait(false))
                    {
                        if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
                        {
                            url = response.Headers.Location;
                            if (!IsHttps(url)) throw new IOException("Ungültige Weiterleitung für die Updateinformationen.");
                            if (url.Host == "github.com")
                            {
                                var match = Regex.Match(url.AbsolutePath, assetPath, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                                if (!match.Success || (tag != null && tag != match.Groups[1].Value))
                                    throw new IOException("Die Weiterleitung führt zu einer anderen Veröffentlichung.");
                                tag = match.Groups[1].Value;
                            }
                            else if (!IsAssetHost(url)) throw new IOException("Unbekannte Adresse für die Updateinformationen.");
                            continue;
                        }
                        RequireSuccess(response);
                        var json = await ReadText(response, 16000, timeout.Token).ConfigureAwait(false);
                        Release release;
                        try { release = new JavaScriptSerializer { MaxJsonLength = 16000 }.Deserialize<Release>(json); }
                        catch (ArgumentException error) { throw new IOException("Ungültige Updateinformationen.", error); }
                        catch (InvalidOperationException error) { throw new IOException("Ungültige Updateinformationen.", error); }
                        if (release == null || !Installation.IsTag(release.tag_name) || (tag != null && release.tag_name != tag) ||
                            Select(new[] { release }, repository, 0, null) == null)
                            throw new IOException("Die Updateinformationen stimmen nicht mit der Veröffentlichung überein.");
                        return Select(new[] { release }, repository, sequence, rejected);
                    }
                }
                throw new IOException("GitHub hat die Updateabfrage zu oft umgeleitet.");
            }
        }

        internal static UpdateOffer Select(string json, string repository, long installed, string rejected)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = 4000000 };
            return Select(serializer.Deserialize<Release[]>(json) ?? new Release[0], repository, installed, rejected);
        }

        private static UpdateOffer Select(Release[] releases, string repository, long installed, string rejected)
        {
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
                        if (!IsHttps(url) || !IsAssetHost(url))
                            throw new IOException("GitHub hat das Update an eine unerwartete Adresse umgeleitet.");
                        continue;
                    }
                    RequireSuccess(response);
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

        private static bool IsHttps(Uri url)
        {
            return url != null && url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttps && url.IsDefaultPort && url.UserInfo.Length == 0;
        }

        private static bool IsAssetHost(Uri url)
        {
            return url.Host == "release-assets.githubusercontent.com" || url.Host == "objects.githubusercontent.com";
        }

        private static void RequireSuccess(HttpResponseMessage response)
        {
            if (response.IsSuccessStatusCode) return;
            var code = (int)response.StatusCode;
            IEnumerable<string> remaining;
            if (code == 429 || (code == 403 && (response.Headers.RetryAfter != null ||
                (response.Headers.TryGetValues("X-RateLimit-Remaining", out remaining) && remaining.Contains("0")))))
                throw new IOException("GitHub begrenzt gerade die Anfragen. Versuchen Sie es später erneut oder öffnen Sie die Downloadseite.");
            if (code == 407) throw new IOException("Der Netzwerkproxy verlangt eine Anmeldung. Öffnen Sie die Downloadseite im Browser.");
            if (code == 404) throw new IOException("GitHub findet das Repository oder die Veröffentlichung nicht (HTTP 404).");
            throw new IOException("GitHub antwortet mit HTTP " + code + ". Öffnen Sie bei Bedarf die Downloadseite.");
        }

        private static async Task<string> ReadText(HttpResponseMessage response, int maximum, CancellationToken cancellation)
        {
            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var buffer = new MemoryStream())
            {
                await Copy(stream, buffer, maximum, null, cancellation).ConfigureAwait(false);
                return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
            }
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
