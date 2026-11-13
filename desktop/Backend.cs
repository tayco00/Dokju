using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Dokju
{
    internal sealed class BackendStartException : IOException
    {
        public readonly string Code;
        internal BackendStartException(string message, string code) : base(message) { Code = code; }
    }

    internal sealed class Backend : IDisposable
    {
        private readonly Process process;
        private bool disposed;
        public readonly string Origin;
        public static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 8000000 };
        public bool Available { get { return !disposed && !process.HasExited; } }

        public Backend(string directory, int port, string publicOrigin)
        {
            Directory.CreateDirectory(directory);
            var program = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "backend", "server.mjs");
            if (!File.Exists(program)) throw new IOException("Der Ordner backend fehlt neben Dokju.exe.");
            var bundled = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "node.exe");
            var start = new ProcessStartInfo(File.Exists(bundled) ? bundled : "node.exe", "\"" + program + "\"")
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            start.EnvironmentVariables["DOKJU_DATA"] = directory;
            start.EnvironmentVariables["DOKJU_BIND"] = "127.0.0.1";
            start.EnvironmentVariables["DOKJU_PORT"] = port.ToString();
            start.EnvironmentVariables["DOKJU_PUBLIC_ORIGIN"] = publicOrigin ?? "";
            start.EnvironmentVariables["DOKJU_KEY"] = Convert.ToBase64String(Key(directory));
            try { process = Process.Start(start); }
            catch (System.ComponentModel.Win32Exception)
            {
                throw new InvalidOperationException("Node.js 20 oder neuer wird benötigt. node.exe muss im Windows-Pfad verfügbar sein.");
            }
            try
            {
                process.BeginErrorReadLine();
                var reply = Json.Deserialize<Handshake>(ReadLine(6000));
                if (!String.IsNullOrWhiteSpace(reply.error)) throw new BackendStartException(reply.error, reply.code);
                if (!Uri.IsWellFormedUriString(reply.origin, UriKind.Absolute))
                    throw new IOException("Der lokale Dokju-Dienst meldet eine ungültige Adresse.");
                Origin = reply.origin;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public T Get<T>(string path) { return Request<T>("GET", path, null); }
        public T Post<T>(string path, object value) { return Request<T>("POST", path, value); }

        private string ReadLine(int timeout)
        {
            Task<string> pending = process.StandardOutput.ReadLineAsync();
            if (!pending.Wait(timeout) || String.IsNullOrWhiteSpace(pending.Result))
            {
                Dispose();
                throw new IOException("Der lokale Dokju-Dienst hat nicht geantwortet. Bitte Dokju erneut starten.");
            }
            return pending.Result;
        }

        private T Request<T>(string method, string path, object data)
        {
            if (!Available) throw new IOException("Der lokale Dokju-Dienst ist beendet.");
            var command = Json.Serialize(new { method = method, path = path, data = data });
            byte[] bytes = Encoding.UTF8.GetBytes(command + "\n");
            var stream = process.StandardInput.BaseStream;
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
            string line = ReadLine(4500);
            var reply = Json.Deserialize<Reply<T>>(line);
            if (reply.status >= 400)
                throw new IOException(Json.Deserialize<Reply<Handshake>>(line).value.error ?? "Die Anfrage konnte nicht abgeschlossen werden.");
            return reply.value;
        }

        private static byte[] Key(string directory)
        {
            string path = Path.Combine(directory, "key.dpapi");
            if (File.Exists(path)) return ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            if (File.Exists(Path.Combine(directory, "store.dokju")))
                throw new IOException("Der Windows-Schlüssel für die vorhandenen Vorgänge fehlt.");
            byte[] key = Random(32);
            Installation.WriteAtomic(path, ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser));
            return key;
        }

        private static byte[] Random(int length)
        {
            byte[] bytes = new byte[length];
            using (var generator = RandomNumberGenerator.Create()) generator.GetBytes(bytes);
            return bytes;
        }

        public void Dispose()
        {
            if (process == null || disposed) return;
            disposed = true;
            if (!process.HasExited)
            {
                process.StandardInput.Close();
                if (!process.WaitForExit(900)) process.Kill();
            }
            process.Dispose();
        }

        public sealed class Handshake
        {
            public string origin { get; set; }
            public string error { get; set; }
            public string code { get; set; }
        }

        public sealed class Reply<T>
        {
            public int status { get; set; }
            public T value { get; set; }
        }
    }
}
