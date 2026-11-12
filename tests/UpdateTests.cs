using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Dokju
{
    internal static class UpdateTests
    {
        private const string Repo = "example/dokju";
        private static readonly string Commit = new string('a', 40);
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        private static int Main(string[] args)
        {
            if (args.Length > 0 && (args[0] == "--startup-probe" || args[0] == "--update-fallback")) return FakeDesktop(args);
            try
            {
                if (args.Length == 4 && args[0] == "--live")
                {
                    Console.WriteLine(DownloadLive(args[1], args[2], Int64.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture)));
                    return 0;
                }
                var root = args[0];
                SelectNewest();
                PackageIntegrity(root);
                DownloadAndStage(root);
                ActivationAndRollback(root);
                if (args.Length == 3) PackagedBuild(args[1], args[2], root);
                Console.WriteLine("Updater: Auswahl, Integritaet, Aktivierung und Wiederanlauf bestanden.");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        private static string DownloadLive(string root, string repository, long minimum)
        {
            Require(minimum > 0, "A positive release sequence is required.");
            ServicePointManager.SecurityProtocol = SecurityProtocolType.SystemDefault;
            var updates = new GitHubUpdates();
            UpdateOffer offer = null;
            for (int attempt = 0; attempt < 4 && offer == null; attempt++)
            {
                if (attempt > 0) Thread.Sleep(4000);
                offer = updates.Check(repository, minimum - 1, null, CancellationToken.None).GetAwaiter().GetResult();
            }
            Require(offer != null, "No matching published GitHub release was found.");
            updates.Stage(root, offer, null, CancellationToken.None).GetAwaiter().GetResult();
            return Path.Combine(Installation.VersionPath(root, offer.Tag), Installation.Desktop);
        }

        private static void DownloadAndStage(string root)
        {
            var build = Build(8);
            var package = Path.Combine(root, "download-fixture.zip");
            CreatePackage(package, build, null);
            var bytes = File.ReadAllBytes(package);
            var release = Release(8);
            release.assets[0].digest = "sha256:" + Hash(package).ToLowerInvariant();
            release.assets[0].size = bytes.Length;
            var install = Directory.CreateDirectory(Path.Combine(root, "staged")).FullName;
            using (var handler = new ReleaseFixture(Json.Serialize(new[] { release }), bytes))
            using (var http = new HttpClient(handler))
            {
                var updates = new GitHubUpdates(http);
                var offer = updates.Check(Repo, 0, null, CancellationToken.None).GetAwaiter().GetResult();
                handler.UntrustedRedirect = true;
                try
                {
                    updates.Stage(install, offer, null, CancellationToken.None).GetAwaiter().GetResult();
                    throw new Exception("An untrusted redirect must fail.");
                }
                catch (IOException) { }
                Require(!Directory.Exists(Installation.VersionPath(install, offer.Tag)), "Failed download must not publish a version directory.");
                handler.UntrustedRedirect = false;
                updates.Stage(install, offer, null, CancellationToken.None).GetAwaiter().GetResult();
                Require(BuildIdentity.Read(Installation.VersionPath(install, offer.Tag)).commit == build.commit, "Validate downloaded build before staging it.");
                Require(Installation.Versions(install).active == null, "Staging must not activate an update.");
            }
        }

        private sealed class ReleaseFixture : HttpMessageHandler
        {
            private readonly string feed;
            private readonly byte[] package;
            internal bool UntrustedRedirect;

            internal ReleaseFixture(string feed, byte[] package) { this.feed = feed; this.package = package; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
            {
                cancellation.ThrowIfCancellationRequested();
                var host = request.RequestUri.Host;
                if (host == "api.github.com") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(feed) });
                if (host == "github.com")
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                    redirect.Headers.Location = new Uri(UntrustedRedirect ? "https://foreign.test/release" : "https://release-assets.githubusercontent.com/release");
                    return Task.FromResult(redirect);
                }
                if (host == "release-assets.githubusercontent.com")
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) });
                throw new Exception("Untrusted redirect reached the HTTP client.");
            }
        }

        private static void SelectNewest()
        {
            var first = Release(4);
            var newest = Release(9);
            var lastPublished = Release(7);
            var badDigest = Release(11);
            badDigest.assets[0].digest = null;
            var foreignDownload = Release(12);
            foreignDownload.assets[0].browser_download_url = "https://elsewhere.test/Dokju.zip";
            var draft = Release(13);
            draft.draft = true;
            string feed = Json.Serialize(new[] { first, newest, lastPublished, badDigest, foreignDownload, draft });
            Require(GitHubUpdates.Select(feed, Repo, 6, null).Sequence == 9, "Choose newest valid release, regardless of feed ordering.");
            Require(GitHubUpdates.Select(feed, Repo, 6, newest.tag_name).Sequence == 7, "Skip a release rejected at startup.");
            Require(GitHubUpdates.Select(feed, Repo, 9, null) == null, "Never downgrade.");
            Require(!Installation.RepositoryIsValid("user/..") && !Installation.RepositoryIsValid("https://github.com/user/repo"), "Reject invalid repository paths.");
            Require(!Installation.IsTag("../../Dokju"), "Reject filesystem path as tag.");
        }

        private static void PackageIntegrity(string root)
        {
            var offer = GitHubUpdates.Select(Json.Serialize(new[] { Release(5) }), Repo, 0, null);
            var valid = Path.Combine(root, "valid.zip");
            CreatePackage(valid, Build(5), null);
            offer.Hash = Hash(valid);
            GitHubUpdates.Extract(valid, Directory.CreateDirectory(Path.Combine(root, "valid")).FullName, offer, CancellationToken.None);

            var malicious = Path.Combine(root, "malicious.zip");
            CreatePackage(malicious, Build(5), "backend/../../outside.mjs");
            var target = Directory.CreateDirectory(Path.Combine(root, "malicious")).FullName;
            ExpectInvalid(() => GitHubUpdates.Extract(malicious, target, offer, CancellationToken.None));
            Require(Directory.GetFileSystemEntries(target).Length == 0, "Reject altered archive before extracting any file.");
            offer.Hash = Hash(malicious);
            ExpectInvalid(() => GitHubUpdates.Extract(malicious, target, offer, CancellationToken.None));
            Require(!File.Exists(Path.Combine(root, "outside.mjs")), "Keep extraction inside the version directory.");

            var wrongCommit = Path.Combine(root, "wrong.zip");
            CreatePackage(wrongCommit, Build(6), null);
            offer.Hash = Hash(wrongCommit);
            ExpectInvalid(() => GitHubUpdates.Extract(wrongCommit, Directory.CreateDirectory(Path.Combine(root, "wrong")).FullName, offer, CancellationToken.None));
        }

        private static void ActivationAndRollback(string data)
        {
            var root = Directory.CreateDirectory(Installation.Root(data)).FullName;
            var portable = Directory.CreateDirectory(Path.Combine(data, "portable")).FullName;
            var current = WriteVersion(Installation.VersionPath(root, Build(2).tag), Build(2), false);
            var broken = WriteVersion(Installation.VersionPath(root, Build(3).tag), Build(3), true);
            WriteVersion(portable, Build(1), false);
            Environment.SetEnvironmentVariable("DOKJU_LAUNCHER_FIXTURE", data);
            var request = Guid.NewGuid();
            LauncherProgram.Activate(data, root, portable, Build(2).tag, Int32.MaxValue, request);
            Require(File.Exists(Installation.ActivationSignal(root, request)), "Starter acknowledges handoff.");
            Require(Installation.Versions(root).active == Build(2).tag, "Make a ready application active.");
            if (!Directory.Exists(broken)) WriteVersion(broken, Build(3), true);
            LauncherProgram.Activate(data, root, current, Build(3).tag, Int32.MaxValue, Guid.NewGuid());
            var state = Installation.Versions(root);
            Require(state.active == Build(2).tag && state.rejected == Build(3).tag, "Keep the active version after failed startup.");
            var wait = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(data, "fallback")) && wait.ElapsedMilliseconds < 5000) Thread.Sleep(50);
            Require(File.Exists(Path.Combine(data, "fallback")), "Restart the previous executable.");
            Thread.Sleep(850);
        }

        private static int FakeDesktop(string[] args)
        {
            var data = Environment.GetEnvironmentVariable("DOKJU_LAUNCHER_FIXTURE");
            if (String.IsNullOrEmpty(data)) return 1;
            if (args[0] == "--update-fallback")
            {
                File.WriteAllText(Path.Combine(data, "fallback"), "ok");
                return 0;
            }
            if (args[0] != "--startup-probe" || File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fail"))) return 1;
            File.WriteAllText(Path.Combine(Installation.Root(data), "probes", args[1]), "ready");
            Thread.Sleep(800);
            return 0;
        }

        private static void PackagedBuild(string zip, string bin, string root)
        {
            var build = BuildIdentity.Read(bin);
            using (var stream = File.OpenRead(zip))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                Require(archive.GetEntry("Dokju/Start-Dokju.cmd") != null, "Ship the Windows launcher.");
                Require(archive.GetEntry("Dokju/Dokju.exe") != null, "Ship the native Windows entry point.");
                Require(archive.GetEntry("Dokju/.github/workflows/windows-release.yml") != null, "Ship the release workflow.");
                foreach (var entry in archive.Entries)
                    Require(!entry.Name.EndsWith(".dpapi") && !entry.Name.EndsWith(".dokju") && entry.Name != "workspace.lock", "Exclude application data from release.");
            }
            if (build.sequence == 0) return;
            var offer = new UpdateOffer { Commit = build.commit, Repository = build.repository, Tag = build.tag, Sequence = build.sequence, Hash = Hash(zip) };
            var output = Directory.CreateDirectory(Path.Combine(root, "package")).FullName;
            GitHubUpdates.Extract(zip, output, offer, CancellationToken.None);
            Require(File.Exists(Path.Combine(output, Installation.Desktop)), "Use the actual release archive as an update.");
        }

        private static string WriteVersion(string directory, BuildIdentity build, bool fails)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "release.json"), Json.Serialize(build));
            File.Copy(System.Reflection.Assembly.GetExecutingAssembly().Location, Path.Combine(directory, Installation.Desktop));
            if (fails) File.WriteAllText(Path.Combine(directory, "fail"), "1");
            return directory;
        }

        private static void CreatePackage(string path, BuildIdentity build, string extra)
        {
            using (var stream = File.Create(path))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var file in new[] { Installation.Desktop, Installation.Launcher, "backend/server.mjs", "release.json", extra })
                {
                    if (file == null) continue;
                    using (var entry = new StreamWriter(zip.CreateEntry("Dokju/bin/" + file).Open()))
                        entry.Write(file == "release.json" ? Json.Serialize(build) : "fixture");
                }
            }
        }

        private static BuildIdentity Build(long number)
        {
            return new BuildIdentity { format = 1, sequence = number, tag = Installation.TagFor(number, Commit), repository = Repo, commit = Commit };
        }

        private static GitHubUpdates.Release Release(long number)
        {
            var tag = Build(number).tag;
            return new GitHubUpdates.Release { tag_name = tag, assets = new[] { new GitHubUpdates.Asset
                { name = Installation.Package, state = "uploaded", digest = "sha256:" + new string('a', 64), size = 1000,
                    browser_download_url = "https://github.com/" + Repo + "/releases/download/" + tag + "/" + Installation.Package } } };
        }

        private static string Hash(string path)
        {
            using (var sha = SHA256.Create())
            using (var input = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");
        }

        private static void ExpectInvalid(Action action)
        {
            try { action(); }
            catch (InvalidDataException) { return; }
            throw new Exception("Expected InvalidDataException.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
    }
}
