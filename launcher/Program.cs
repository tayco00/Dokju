using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;

namespace Dokju
{
    internal static class LauncherProgram
    {
        private enum StartResult { Failed, Ready, AlreadyRunning }

        [STAThread]
        private static int Main(string[] args)
        {
            var data = Installation.DataDirectory;
            var root = Installation.Root(data);
            var current = AppDomain.CurrentDomain.BaseDirectory;
            var packaged = Path.Combine(current, "bin");
            if (File.Exists(Path.Combine(packaged, Installation.Desktop))) current = packaged;
            try
            {
                if (args.Length == 2 && args[0] == "--smoke")
                {
                    var checkDirectory = Path.GetFullPath(args[1]);
                    using (var check = Installation.Launch(Path.Combine(current, Installation.Desktop), "--smoke \"" + checkDirectory + "\""))
                    {
                        if (check.WaitForExit(30000)) return check.ExitCode;
                        check.Kill();
                        return 1;
                    }
                }
                Directory.CreateDirectory(root);
                using (var mutex = new Mutex(false, @"Local\Dokju-Launcher"))
                {
                    try { if (!mutex.WaitOne(0)) return 0; }
                    catch (AbandonedMutexException) { }
                    try
                    {
                        Guid request;
                        if (args.Length == 4 && args[0] == "--activate" && Guid.TryParseExact(args[3], "N", out request))
                        {
                            Activate(data, root, current, args[1], Int32.Parse(args[2], CultureInfo.InvariantCulture), request);
                            return 0;
                        }
                        var state = Installation.Versions(root);
                        if (Installation.IsTag(state.active))
                        {
                            var active = Installation.VersionPath(root, state.active);
                            if (PreferInstalled(current, active))
                            {
                                if (TryStart(data, active) != StartResult.Failed) return 0;
                                state.rejected = state.active;
                                state.active = state.previous;
                                state.previous = null;
                                Installation.SaveVersions(root, state);
                                if (Installation.IsTag(state.active) && TryStart(data, Installation.VersionPath(root, state.active)) != StartResult.Failed) return 0;
                            }
                        }
                        using (Installation.Launch(Path.Combine(current, Installation.Desktop), "")) { }
                        return 0;
                    }
                    finally { mutex.ReleaseMutex(); }
                }
            }
            catch (Exception error)
            {
                if (args.Length == 0 || args[0] != "--activate")
                    MessageBox.Show(error.Message, "Dokju starten", MessageBoxButton.OK, MessageBoxImage.Warning);
                return 1;
            }
        }

        private static bool PreferInstalled(string current, string active)
        {
            var portable = BuildIdentity.Read(current);
            if (portable.sequence == 0) return true;
            try
            {
                var installed = BuildIdentity.Read(active);
                return installed.sequence >= portable.sequence &&
                    String.Equals(installed.repository, portable.repository, StringComparison.OrdinalIgnoreCase);
            }
            catch (IOException) { return false; }
            catch (InvalidDataException) { return false; }
            catch (ArgumentException) { return false; }
        }

        internal static void Activate(string data, string root, string current, string tag, int previousPid, Guid request)
        {
            var candidate = Installation.VersionPath(root, tag);
            var build = BuildIdentity.Read(candidate);
            if (build.tag != tag) throw new InvalidDataException("Das vorbereitete Update stimmt nicht mit der Version überein.");
            var state = Installation.Versions(root);
            var currentBuild = BuildIdentity.Read(current);
            if (Math.Max(currentBuild.sequence, Installation.ActiveSequence(root, state, build.repository)) >= build.sequence || (currentBuild.sequence > 0 &&
                !String.Equals(currentBuild.repository, build.repository, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Die vorbereitete Version ist kein Update der laufenden Installation.");

            Installation.WriteAtomic(Installation.ActivationSignal(root, request), new byte[] { 1 });
            try
            {
                using (var previous = Process.GetProcessById(previousPid))
                    if (!previous.WaitForExit(15000)) throw new IOException("Dokju konnte für das Update nicht geschlossen werden.");
            }
            catch (ArgumentException) { }

            var oldTag = currentBuild.tag;
            var oldIsManaged = Installation.IsTag(oldTag) && SameDirectory(current, Installation.VersionPath(root, oldTag));

            var result = TryStart(data, candidate);
            if (result == StartResult.Ready)
            {
                state.previous = oldIsManaged ? oldTag : Installation.ActiveSequence(root, state, build.repository) > 0 ? state.active : null;
                state.active = tag;
                state.rejected = null;
                Installation.SaveVersions(root, state);
                RemoveOldVersions(root, state);
                return;
            }
            if (result == StartResult.AlreadyRunning) return;

            state.rejected = tag;
            Installation.SaveVersions(root, state);
            using (Installation.Launch(Path.Combine(current, Installation.Desktop), "--update-fallback")) { }
        }

        private static void RemoveOldVersions(string root, InstalledVersions state)
        {
            try
            {
                foreach (var directory in new DirectoryInfo(Path.Combine(root, "versions")).GetDirectories("dokju-*"))
                {
                    if (!Installation.IsTag(directory.Name) || directory.Name == state.active || directory.Name == state.previous ||
                        (directory.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    try { directory.Delete(true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static bool SameDirectory(string first, string second)
        {
            return String.Equals(first.TrimEnd('\\'), second.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        private static StartResult TryStart(string data, string directory)
        {
            var executable = Path.Combine(directory, Installation.Desktop);
            if (!File.Exists(executable)) return StartResult.Failed;
            var probes = Path.Combine(Installation.Root(data), "probes");
            Directory.CreateDirectory(probes);
            var id = Guid.NewGuid().ToString("N");
            var ready = Path.Combine(probes, id);
            try
            {
                using (var process = Installation.Launch(executable, "--startup-probe " + id))
                {
                    var timeout = Stopwatch.StartNew();
                    while (timeout.ElapsedMilliseconds < 18000)
                    {
                        if (File.Exists(ready) && !process.HasExited) return StartResult.Ready;
                        if (process.WaitForExit(100)) return process.ExitCode == Installation.AlreadyRunning ? StartResult.AlreadyRunning : StartResult.Failed;
                    }
                    if (!process.HasExited)
                    {
                        process.Kill();
                        process.WaitForExit(2000);
                    }
                }
                return StartResult.Failed;
            }
            catch (System.ComponentModel.Win32Exception) { return StartResult.Failed; }
            finally { if (File.Exists(ready)) File.Delete(ready); }
        }
    }
}
