using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Dokju
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] arguments)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.SystemDefault;
            bool smoke = arguments.Length == 2 && arguments[0] == "--smoke";
            Guid probeId;
            bool probe = arguments.Length == 2 && arguments[0] == "--startup-probe" && Guid.TryParseExact(arguments[1], "N", out probeId);
            string directory = smoke ? Path.GetFullPath(arguments[1]) : Installation.DataDirectory;
            try
            {
                bool first = true;
                // The first desktop release used this mutex. The file lock also covers other Windows sessions.
                using (var legacyGuard = smoke ? null : new Mutex(true, "Local\\Dokju-Desktop", out first))
                {
                    if (!first) return AlreadyOpen(probe);
                    Directory.CreateDirectory(directory);
                    FileStream guard;
                    try { guard = new FileStream(Path.Combine(directory, "workspace.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                    catch (IOException error)
                    {
                        if ((error.HResult & 0xffff) != 32) throw;
                        return AlreadyOpen(probe);
                    }
                    using (guard)
                    {
                        var app = new Application();
                        using (var workspace = new Workspace(directory, smoke))
                        {
                            if (smoke) workspace.Smoke();
                            if (probe) workspace.Window.ContentRendered += (sender, args) =>
                                Installation.WriteAtomic(Path.Combine(Installation.Root(directory), "probes", arguments[1]), new byte[] { 1 });
                            if (arguments.Length == 1 && arguments[0] == "--update-fallback") workspace.ShowUpdateFallback();
                            return app.Run(workspace.Window);
                        }
                    }
                }
            }
            catch (Exception error)
            {
                if (smoke) File.WriteAllText(Path.Combine(directory, "smoke-error.txt"), error.ToString());
                else if (!probe) MessageBox.Show(error.Message, "Dokju konnte nicht gestartet werden", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }

        private static int AlreadyOpen(bool probe)
        {
            if (!probe) MessageBox.Show("Dokju ist bereits geöffnet.", "Dokju");
            return Installation.AlreadyRunning;
        }
    }

    internal sealed class Workspace : IDisposable
    {
        private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
        private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        private readonly string directory;
        private readonly DispatcherTimer timer;
        private readonly UpdatePanel updates;
        private Backend backend;
        private Connection connection;
        private Dashboard dashboard;
        private string dataStamp;
        private string qrLink;
        private string qrKey;
        private bool updating;
        private string connectionNotice;
        public readonly Window Window;

        public Workspace(string directory, bool smoke = false)
        {
            this.directory = directory;
            Directory.CreateDirectory(directory);
            using (var layout = Assembly.GetExecutingAssembly().GetManifestResourceStream("Dokju.Main.xaml"))
                Window = (Window)XamlReader.Load(layout);
            SetWindowIcon();
            Window.SizeChanged += (sender, args) => AdaptLayout(args.NewSize.Width);
            Button("DemoButton").Click += (sender, args) => Run(() => CreateExample());
            Button("NewButton").Click += (sender, args) => Run(NewCase);
            Button("ConnectionButton").Click += (sender, args) => Run(Configure);
            Button("VerifyButton").Click += (sender, args) => Run(Verify);
            Button("ExportButton").Click += (sender, args) => Run(Export);
            Button("RenewButton").Click += (sender, args) => Run(Renew);
            Button("OpenButton").Click += (sender, args) => Run(() =>
            {
                if (qrLink != null) Process.Start(new ProcessStartInfo(qrLink) { UseShellExecute = true });
            });
            Button("CopyButton").Click += (sender, args) => Run(() =>
            {
                if (qrLink == null) return;
                Clipboard.SetText(qrLink);
                Message("Einladungslink kopiert.");
            });
            Control<TextBox>("SearchBox").TextChanged += (sender, args) => Run(Filter);
            Control<ComboBox>("CaseFilter").SelectionChanged += (sender, args) => Run(Filter);
            Control<ListBox>("CaseList").SelectionChanged += (sender, args) => { if (!updating) Run(ShowCase); };
            Control<ComboBox>("PersonList").SelectionChanged += (sender, args) => { if (!updating) Run(ShowPerson); };
            connection = smoke ? new Connection { port = 0 } : LoadConnection();
            try { Start(connection); }
            catch (IOException error)
            {
                if (connection.address == "127.0.0.1") throw;
                connectionNotice = "Die gespeicherte WLAN-Adresse ist nicht verfügbar. " + error.Message;
                connection = new Connection();
                Start(connection);
            }
            Refresh(null, true);
            Message(connectionNotice ?? "Bereit.");
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (sender, args) =>
            {
                try { Refresh(null, false); }
                catch (Exception error) { Message(error.Message); }
            };
            timer.Start();
            updates = new UpdatePanel(Window, Button("UpdateButton"), directory);
        }

        internal void ShowUpdateFallback() { Message("Das Update konnte nicht gestartet werden. Die vorherige Version ist wieder geöffnet."); }

        private void AdaptLayout(double width)
        {
            bool compact = width < 1030;
            Control<ColumnDefinition>("SidebarColumn").Width = new GridLength(compact ? 156 : 188);
            Control<DockPanel>("SidebarPanel").Margin = compact ? new Thickness(12, 23, 12, 14) : new Thickness(16, 25, 16, 18);
            Control<ColumnDefinition>("CasesColumn").Width = new GridLength(compact ? 204 : 242);
            Control<Grid>("WorkArea").Margin = compact ? new Thickness(19, 17, 19, 12) : new Thickness(27, 24, 27, 15);
            Control<StackPanel>("DetailPane").Margin = compact ? new Thickness(15, 13, 15, 15) : new Thickness(23, 19, 22, 20);
            Control<Border>("DecisionBand").Margin = compact ? new Thickness(0, 10, 0, 11) : new Thickness(0, 16, 0, 15);
            Label("HeaderSubtitle").Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            Label("DetailTitle").FontSize = compact ? 22 : 26;
            Button("ExportButton").Content = compact ? "Exportieren" : "Nachweis exportieren";
            Button("OpenButton").Content = compact ? "Browser öffnen" : "Im Browser öffnen";
            int qrSize = compact ? 135 : 180;
            Control<ColumnDefinition>("QrColumn").Width = new GridLength(qrSize);
            Control<Image>("QrImage").Width = Control<Image>("QrImage").Height = qrSize;
        }

        private void SetWindowIcon()
        {
            var drawing = new DrawingGroup();
            drawing.Children.Add(new GeometryDrawing(Brush("#08665D"), null, new RectangleGeometry(new Rect(0, 0, 32, 32), 7, 7)));
            var check = Geometry.Parse("M8,16 L13,21 24,10");
            drawing.Children.Add(new GeometryDrawing(null, new Pen(Brushes.White, 3), check));
            Window.Icon = new DrawingImage(drawing);
        }

        private Connection LoadConnection()
        {
            var path = Path.Combine(directory, "connection.json");
            if (!File.Exists(path)) return new Connection();
            try
            {
                var saved = Installation.ReadJson<Connection>(path);
                IPAddress address;
                if (saved != null && saved.port >= 1024 && saved.port <= 65535 && IPAddress.TryParse(saved.address, out address) &&
                    address.AddressFamily == AddressFamily.InterNetwork && address.ToString() == saved.address) return saved;
            }
            catch (IOException) { }
            catch (InvalidDataException) { }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            connectionNotice = "Die gespeicherte Verbindung ist ungültig. Dokju nutzt die lokale Vorschau.";
            return new Connection();
        }

        private void Start(Connection options)
        {
            backend = new Backend(directory, options.address, options.port);
            Label("ConnectionText").Text = (options.address == "127.0.0.1" ? "Lokale Vorschau" : "WLAN-Demo") + "\n" + backend.Origin;
        }

        private void Configure()
        {
            var addresses = new List<string> { "127.0.0.1" };
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var entry in network.GetIPProperties().UnicastAddresses)
                {
                    if (entry.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    byte[] ip = entry.Address.GetAddressBytes();
                    if (ip[0] == 10 || (ip[0] == 192 && ip[1] == 168) || (ip[0] == 172 && ip[1] >= 16 && ip[1] <= 31))
                        addresses.Add(entry.Address.ToString());
                }
            }
            var dialog = new FormDialog(Window, "Verbindung", "Lokale Vorschau für diesen PC oder WLAN-Demo für ein Smartphone im selben Netzwerk.");
            var selector = dialog.Select("Adresse", addresses.Distinct().ToArray(), connection.address);
            var port = dialog.Field("Port", connection.port.ToString(German), 5);
            dialog.Note("127.0.0.1 ist nur an diesem PC erreichbar. WLAN-Demo verwendet HTTP. Speichern Sie eine feste Adresse und einen festen Port, damit bereits ausgegebene Links erreichbar bleiben.");
            dialog.Note("Falls das Smartphone die Adresse nicht erreicht, prüfen Sie das gemeinsame WLAN und die eingehende Windows-Firewallregel für den gewählten Port.");
            if (!dialog.Show("Verbindung übernehmen")) return;
            int number;
            if (!Int32.TryParse(port.Text, out number) || number < 1024 || number > 65535)
                throw new IOException("Bitte einen Port zwischen 1024 und 65535 angeben.");
            var next = new Connection { address = (string)selector.SelectedItem, port = number };
            if (next.address == connection.address && next.port == connection.port) return;
            backend.Dispose();
            backend = null;
            try
            {
                Start(next);
                Installation.WriteAtomic(Path.Combine(directory, "connection.json"), Encoding.UTF8.GetBytes(Backend.Json.Serialize(next)));
            }
            catch
            {
                if (backend != null) backend.Dispose();
                Start(connection);
                throw;
            }
            connection = next;
            qrKey = null;
            Refresh(null, true);
            Message("Verbindung aktualisiert.");
        }

        private void NewCase()
        {
            var defaults = dashboard.defaults;
            var dialog = new FormDialog(Window, "Neuer Vorgang", "Verwenden Sie ein Testaktenzeichen und fiktive Angaben.");
            var title = dialog.Field("Gesprächsbezeichnung", "", 90);
            var reference = dialog.Field("Aktenzeichen", "", 64);
            var count = dialog.Field("Teilnehmende (1 bis 12)", "1", 2);
            var controller = dialog.Field("Verantwortliche Stelle", defaults.controller, 100);
            var provider = dialog.Field("KI-Anbieter", defaults.provider, 100);
            var contact = dialog.Field("Datenschutzkontakt", defaults.contact, 110);
            var audioDays = dialog.Field("Aufnahme und Transkript (Tage)", defaults.audioDays.ToString(), 3);
            var proofDays = dialog.Field("Nachweis in Dokju (Tage)", defaults.proofDays.ToString(), 4);
            dialog.Note("Der QR-Link gilt 30 Minuten. Pro teilnehmender Person wird ein eigener Link erstellt.");
            string id = null;
            dialog.Accept = () =>
            {
                var item = backend.Post<CaseRow>("/cases", new
                {
                    title = title.Text, reference = reference.Text, count = Number(count),
                    controller = controller.Text, provider = provider.Text, contact = contact.Text,
                    audioDays = Number(audioDays), proofDays = Number(proofDays)
                });
                id = item.id;
            };
            if (dialog.Show("Vorgang anlegen")) Reveal(id);
        }

        private static int Number(TextBox field)
        {
            int value;
            return Int32.TryParse(field.Text, out value) ? value : 0;
        }

        private void CreateExample()
        {
            var defaults = dashboard.defaults;
            var item = backend.Post<CaseRow>("/cases", new
            {
                title = "Beratungsgespräch Jugendamt", reference = "DEMO-" + DateTime.Now.ToString("yyMMdd-HHmmss"),
                count = 1, controller = defaults.controller, provider = defaults.provider,
                contact = defaults.contact, audioDays = defaults.audioDays, proofDays = defaults.proofDays
            });
            Reveal(item.id);
        }

        private void Reveal(string id)
        {
            Control<TextBox>("SearchBox").Clear();
            Control<ComboBox>("CaseFilter").SelectedIndex = 0;
            Refresh(id, true);
            Message("Vorgang angelegt. Die Einwilligung kann geöffnet werden.");
        }

        private void Refresh(string selection, bool force)
        {
            var latest = backend.Get<Dashboard>("/cases");
            string stamp = String.Join("|", latest.cases.Select(item => item.id + ":" +
                String.Join(",", item.invites.Select(person => person.status + "/" + person.changed + "/" + person.until))));
            if (!force && stamp == dataStamp) return;
            dashboard = latest;
            Label("CaseCount").Text = latest.cases.Length.ToString(German);
            Label("OpenCount").Text = latest.cases.SelectMany(item => item.invites).Count(person => person.status == "pending").ToString(German);
            Label("ReadyCount").Text = latest.cases.Count(item => item.ready).ToString(German);
            Filter(selection);
            dataStamp = stamp;
        }

        private void Filter() { Filter(null); }
        private void Filter(string selection)
        {
            if (dashboard == null) return;
            var list = Control<ListBox>("CaseList");
            var selected = Selected();
            string id = selection ?? (selected == null ? null : selected.id);
            string query = Control<TextBox>("SearchBox").Text.Trim();
            int state = Control<ComboBox>("CaseFilter").SelectedIndex;
            var rows = dashboard.cases.Where(item => (state <= 0 || item.FilterState == state) && (
                German.CompareInfo.IndexOf(item.title, query, CompareOptions.IgnoreCase) >= 0 ||
                German.CompareInfo.IndexOf(item.reference, query, CompareOptions.IgnoreCase) >= 0)).ToArray();
            updating = true;
            try
            {
                list.ItemsSource = rows;
                list.SelectedItem = rows.FirstOrDefault(item => item.id == id) ?? rows.FirstOrDefault();
            }
            finally { updating = false; }
            Label("EmptyList").Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Label("EmptyList").Text = query.Length > 0 || state > 0 ? "Keine Treffer" : "Keine Vorgänge";
            ShowCase();
        }

        private string activeId;
        private void ShowCase()
        {
            var item = Selected();
            Control<StackPanel>("EmptyDetail").Visibility = item == null ? Visibility.Visible : Visibility.Collapsed;
            Control<ScrollViewer>("Detail").Visibility = item == null ? Visibility.Collapsed : Visibility.Visible;
            if (item == null)
            {
                activeId = null;
                return;
            }
            Label("DetailTitle").Text = item.title;
            Label("DetailReference").Text = item.reference + "  ·  " + item.info.controller;
            bool blocked = item.FilterState == 3;
            Label("DecisionState").Text = item.ready ? "Freigegeben" : blocked ? "Blockiert" : "Ausstehend";
            Label("DecisionText").Text = blocked ? "Mindestens eine Einladung wurde abgelehnt, ist abgelaufen oder wurde widerrufen." :
                item.AcceptedCount + " von " + item.invites.Length + " Teilnehmenden haben zugestimmt.";
            Control<Border>("DecisionBand").Background = Brush(item.ready ? "#DEF2EA" : blocked ? "#FBE9E2" : "#FCF0D9");
            Label("DecisionState").Foreground = Brush(item.ready ? "#07594D" : blocked ? "#8A382D" : "#6C4B17");
            Label("RetentionText").Text = "Der Nachweis wird in Dokju am " + When(item.proofUntil) + " gelöscht.";
            var list = Control<ComboBox>("PersonList");
            int index = activeId == item.id && list.SelectedIndex >= 0 ? list.SelectedIndex : 0;
            activeId = item.id;
            updating = true;
            try
            {
                list.ItemsSource = item.invites;
                list.SelectedIndex = Math.Min(index, item.invites.Length - 1);
            }
            finally { updating = false; }
            ShowPerson();
        }

        private void ShowPerson()
        {
            var item = Selected();
            var person = (Person)Control<ComboBox>("PersonList").SelectedItem;
            if (item == null || person == null) return;
            bool pending = person.status == "pending";
            Label("PersonTime").Text = pending ? "Einladungslink gültig bis " + When(person.until) :
                person.changed == null ? "Die Einladung ist abgelaufen." : "Letzte Entscheidung am " + When(person.changed);
            Button("RenewButton").Visibility = person.status == "expired" ? Visibility.Visible : Visibility.Collapsed;
            Control<Border>("QrCard").Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
            if (!pending)
            {
                qrKey = null;
                qrLink = null;
                return;
            }
            string key = item.id + "/" + person.number + "/" + person.until;
            if (qrKey != key)
            {
                qrLink = null;
                Control<Image>("QrImage").Source = null;
                Button("CopyButton").IsEnabled = false;
                Button("OpenButton").IsEnabled = false;
                var invite = backend.Get<Invitation>("/cases/" + item.id + "/invite?person=" + person.number);
                Control<Image>("QrImage").Source = DrawQr(invite.qr);
                qrLink = invite.link;
                qrKey = key;
                Button("CopyButton").IsEnabled = true;
                Button("OpenButton").IsEnabled = true;
            }
            Label("QrHint").Text = connection.address == "127.0.0.1" ? "Am PC öffnen oder unter Verbindung die WLAN-Demo für das Smartphone wählen." :
                "Zeigen Sie den Code der ausgewählten Person. Ihr Smartphone muss im selben WLAN sein.";
        }

        private void Renew()
        {
            var item = Selected();
            var person = (Person)Control<ComboBox>("PersonList").SelectedItem;
            if (item == null || person == null) return;
            backend.Post<CaseRow>("/cases/" + item.id + "/renew", new { person = person.number });
            Refresh(item.id, true);
            Message("Ein neuer QR-Code ist verfügbar.");
        }

        private static BitmapSource DrawQr(string[] rows)
        {
            int modules = rows.Length;
            const int scale = 5;
            int size = (modules + 8) * scale;
            var pixels = Enumerable.Repeat((byte)255, size * size).ToArray();
            for (int y = 0; y < modules; y++)
                for (int x = 0; x < modules; x++)
                    if (rows[y][x] == '1')
                        for (int dy = 0; dy < scale; dy++)
                            for (int dx = 0; dx < scale; dx++)
                                pixels[((y + 4) * scale + dy) * size + (x + 4) * scale + dx] = 0;
            return BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, pixels, size);
        }

        private void Export()
        {
            var item = Selected();
            if (item == null) return;
            var name = new string(item.reference.Select(value => Path.GetInvalidFileNameChars().Contains(value) ? '-' : value).ToArray());
            var dialog = new SaveFileDialog
            {
                Title = "Einwilligungsnachweis exportieren", FileName = "Dokju-" + name,
                Filter = "PDF für die Akte (*.pdf)|*.pdf|ZIP mit PDF und prüfbarem JSON (*.zip)|*.zip", AddExtension = true
            };
            if (dialog.ShowDialog(Window) != true) return;
            var result = backend.Get<ExportResult>("/cases/" + item.id + "/export");
            var pdf = Convert.FromBase64String(result.pdf);
            if (dialog.FilterIndex == 1) Installation.WriteAtomic(dialog.FileName, pdf);
            else
            {
                using (var output = new MemoryStream())
                {
                    using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                    {
                        using (var stream = zip.CreateEntry("Dokju-Nachweis.pdf").Open()) stream.Write(pdf, 0, pdf.Length);
                        using (var writer = new StreamWriter(zip.CreateEntry("Dokju-Nachweis.json").Open(), new UTF8Encoding(false)))
                            writer.Write(Backend.Json.Serialize(result.proof));
                    }
                    Installation.WriteAtomic(dialog.FileName, output.ToArray());
                }
            }
            Message("Nachweis exportiert.");
        }

        private void Verify()
        {
            var dialog = new OpenFileDialog
            {
                Title = "JSON-Nachweis oder ZIP auswählen", Filter = "Dokju-Nachweis (*.json;*.zip)|*.json;*.zip"
            };
            if (dialog.ShowDialog(Window) != true) return;
            const int maximum = 120000;
            string data;
            string pdfHash = null;
            if (Path.GetExtension(dialog.FileName).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using (var stream = File.OpenRead(dialog.FileName))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    var json = ReadEntry(zip, "Dokju-Nachweis.json", maximum);
                    var pdf = ReadEntry(zip, "Dokju-Nachweis.pdf", 8000000);
                    data = Encoding.UTF8.GetString(json);
                    using (var hash = SHA256.Create()) pdfHash = BitConverter.ToString(hash.ComputeHash(pdf)).Replace("-", "").ToLowerInvariant();
                }
            }
            else
            {
                if (new FileInfo(dialog.FileName).Length > maximum) throw new IOException("Der JSON-Nachweis ist zu groß.");
                data = File.ReadAllText(dialog.FileName);
            }
            var result = backend.Post<Verification>("/verify", new { proof = Backend.Json.DeserializeObject(data), pdfHash = pdfHash });
            string message = !result.valid ? "Die Prüfung des JSON-Nachweises ist fehlgeschlagen." :
                result.document == false ? "Die PDF-Datei stimmt nicht mit dem JSON-Nachweis überein." :
                result.document == true ? "PDF und JSON stimmen mit dem Nachweis dieser Installation überein." :
                "Der JSON-Nachweis stimmt mit dem Prüfwert dieser Installation überein.";
            MessageBox.Show(Window, message, "Nachweis prüfen", MessageBoxButton.OK,
                result.valid && result.document != false ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        private static byte[] ReadEntry(ZipArchive zip, string name, int maximum)
        {
            var matches = zip.Entries.Where(entry => entry.FullName == name).Take(2).ToArray();
            if (matches.Length != 1 || matches[0].Length <= 0 || matches[0].Length > maximum)
                throw new IOException("Das Archiv enthält keine passende Datei: " + name);
            using (var input = matches[0].Open())
            {
                var bytes = new byte[(int)matches[0].Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = input.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0) throw new IOException("Unvollständige Datei: " + name);
                    offset += read;
                }
                if (input.ReadByte() != -1) throw new IOException("Unerwartete Dateigröße: " + name);
                return bytes;
            }
        }

        public void Smoke()
        {
            Window.ContentRendered += (sender, args) => Window.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    CreateExample();
                    var filter = Control<ComboBox>("CaseFilter");
                    filter.SelectedIndex = 2;
                    if (Control<ListBox>("CaseList").Items.Count != 0) throw new IOException("Statusfilter ist fehlerhaft.");
                    filter.SelectedIndex = 0;
                    var search = Control<TextBox>("SearchBox");
                    search.Text = "DEMO-";
                    if (Control<ListBox>("CaseList").Items.Count != 1) throw new IOException("Aktenzeichensuche ist fehlerhaft.");
                    search.Clear();
                    Window.UpdateLayout();
                    if (qrLink == null || Control<Image>("QrImage").Source == null) throw new IOException("QR-Code fehlt.");
                    Capture(Window, Path.Combine(directory, "desktop.png"));
                    Window.Width = Window.MinWidth;
                    Window.Height = Window.MinHeight;
                    Window.UpdateLayout();
                    var qr = Control<Image>("QrImage");
                    var detail = Control<ScrollViewer>("Detail");
                    var qrBottom = qr.TransformToAncestor(detail).Transform(new Point(0, qr.ActualHeight)).Y;
                    if (qrBottom > detail.ViewportHeight + 1) throw new IOException("Der QR-Code wird im kleinsten Fenster abgeschnitten.");
                    Capture(Window, Path.Combine(directory, "compact.png"));
                    updates.Show(window => Capture(window, Path.Combine(directory, "updates.png")));
                    var export = backend.Get<ExportResult>("/cases/" + Selected().id + "/export");
                    File.WriteAllBytes(Path.Combine(directory, "proof.pdf"), Convert.FromBase64String(export.pdf));
                    if (!backend.Post<Verification>("/verify", export.proof).valid) throw new IOException("Nachweisprüfung fehlgeschlagen.");
                    File.WriteAllText(Path.Combine(directory, "smoke-ok.txt"), qrLink);
                }
                catch (Exception error) { File.WriteAllText(Path.Combine(directory, "smoke-error.txt"), error.ToString()); }
                finally { Window.Close(); }
            }), DispatcherPriority.ApplicationIdle);
        }

        private static void Capture(Window window, string path)
        {
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(path)) encoder.Save(output);
        }

        private CaseRow Selected() { return (CaseRow)Control<ListBox>("CaseList").SelectedItem; }
        private T Control<T>(string name) where T : class { return (T)Window.FindName(name); }
        private TextBlock Label(string name) { return Control<TextBlock>(name); }
        private Button Button(string name) { return Control<Button>(name); }
        private void Message(string value) { Label("StatusText").Text = value; }
        private void Run(Action action)
        {
            try { action(); }
            catch (Exception error) { MessageBox.Show(Window, error.Message, "Dokju", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        private static Brush Brush(string value) { return (Brush)new BrushConverter().ConvertFromString(value); }
        private static string When(string value)
        {
            return TimeZoneInfo.ConvertTime(DateTimeOffset.Parse(value, CultureInfo.InvariantCulture), Berlin).ToString("dd.MM.yyyy, HH:mm 'Uhr' (Berlin)", German);
        }

        public void Dispose()
        {
            if (timer != null) timer.Stop();
            if (updates != null) updates.Dispose();
            if (backend != null) backend.Dispose();
        }
    }

    public sealed class Connection
    {
        public string address { get; set; }
        public int port { get; set; }
        public Connection() { address = "127.0.0.1"; port = 8787; }
    }
    public sealed class Dashboard
    {
        public Profile defaults { get; set; }
        public CaseRow[] cases { get; set; }
    }
    public sealed class Profile
    {
        public string controller { get; set; }
        public string provider { get; set; }
        public string contact { get; set; }
        public int audioDays { get; set; }
        public int proofDays { get; set; }
    }
    public sealed class CaseRow
    {
        public string id { get; set; }
        public string title { get; set; }
        public string reference { get; set; }
        public string inviteUntil { get; set; }
        public string proofUntil { get; set; }
        public Profile info { get; set; }
        public Person[] invites { get; set; }
        public bool ready { get; set; }
        public int FilterState { get { return ready ? 2 : invites.Any(person => person.status != "accepted" && person.status != "pending") ? 3 : 1; } }
        public int AcceptedCount { get { return invites.Count(person => person.status == "accepted"); } }
        public string StateLabel { get { return (FilterState == 2 ? "Freigegeben" : FilterState == 3 ? "Blockiert" : "Offen") +
            "  ·  " + AcceptedCount + "/" + invites.Length + " zugestimmt"; } }
    }
    public sealed class Person
    {
        private static readonly Dictionary<string, string> Text = new Dictionary<string, string>
        {
            { "pending", "Offen" }, { "accepted", "Zugestimmt" }, { "declined", "Abgelehnt" },
            { "withdrawn", "Widerrufen" }, { "expired", "Abgelaufen" }
        };
        public int number { get; set; }
        public string status { get; set; }
        public string changed { get; set; }
        public string until { get; set; }
        public string Summary { get { return "Person " + number + "  ·  " + Text[status]; } }
    }
    public sealed class Invitation { public string link { get; set; } public string[] qr { get; set; } }
    public sealed class ExportResult { public string pdf { get; set; } public object proof { get; set; } }
    public sealed class Verification { public bool valid { get; set; } public bool? document { get; set; } }
}
