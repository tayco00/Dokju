using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
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
            bool liveSmoke = arguments.Length == 2 && arguments[0] == "--smoke-live";
            bool smoke = liveSmoke || (arguments.Length == 2 && arguments[0] == "--smoke");
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
                        using (var workspace = new Workspace(directory, smoke, liveSmoke))
                        {
                            if (smoke) workspace.Smoke(liveSmoke);
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
        private bool disconnected;
        private DateTime nextReconnect;
        private string connectionNotice;
        public readonly Window Window;

        public Workspace(string directory, bool smoke, bool liveSmoke)
        {
            this.directory = directory;
            Directory.CreateDirectory(directory);
            using (var layout = Assembly.GetExecutingAssembly().GetManifestResourceStream("Dokju.Main.xaml"))
                Window = (Window)XamlReader.Load(layout);
            SetWindowIcon();
            Window.SizeChanged += (sender, args) => AdaptLayout(args.NewSize.Width);
            Button("NewButton").Click += (sender, args) => Run(NewCase);
            Button("FirstCaseButton").Click += (sender, args) => Run(NewCase);
            Button("EditButton").Click += (sender, args) => Run(() => CaseDialog(Selected()));
            Button("WithdrawButton").Click += (sender, args) => Run(Withdraw);
            Button("WithdrawalLinkButton").Click += (sender, args) => Run(CopyWithdrawalLink);
            Button("MobileButton").Click += (sender, args) => Run(Configure);
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
            var newCommand = new System.Windows.Input.RoutedCommand("NeuerVorgang", typeof(Workspace));
            Window.InputBindings.Add(new System.Windows.Input.KeyBinding(newCommand, System.Windows.Input.Key.N, System.Windows.Input.ModifierKeys.Control));
            Window.CommandBindings.Add(new System.Windows.Input.CommandBinding(newCommand, (sender, args) => Run(NewCase)));
            Control<TextBox>("SearchBox").TextChanged += (sender, args) => Run(Filter);
            Control<ComboBox>("CaseFilter").SelectionChanged += (sender, args) => Run(Filter);
            Control<ListBox>("CaseList").SelectionChanged += (sender, args) => { if (!updating) Run(ShowCase); };
            Control<ComboBox>("PersonList").SelectionChanged += (sender, args) => { if (!updating) Run(ShowPerson); };
            connection = smoke ? new Connection { port = 0, publicOrigin = "https://einwilligung.example" } : LoadConnection();
            try { Start(connection); }
            catch (BackendStartException error)
            {
                if (connection.publicOrigin != null || error.Code != "EADDRINUSE") throw;
                connection = new Connection { port = 0 };
                Start(connection);
                connectionNotice = "Der bisherige Port ist belegt. Dokju verwendet " + connection.port + ". Bereits geteilte lokale Links bitte neu öffnen.";
                try { SaveConnection(connection); }
                catch (IOException) { connectionNotice += " Die Änderung konnte nicht gespeichert werden."; }
                catch (UnauthorizedAccessException) { connectionNotice += " Die Änderung konnte nicht gespeichert werden."; }
            }
            Refresh(null, true);
            Message(connectionNotice ?? "Bereit.");
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (sender, args) => PollStatus();
            timer.Start();
            updates = new UpdatePanel(Window, Button("UpdateButton"), directory, smoke && !liveSmoke ? new GitHubUpdates(new System.Net.Http.HttpClient(new StartupUpdateFixture())) : null);
        }

        private void PollStatus()
        {
            if (disconnected && DateTime.UtcNow < nextReconnect) return;
            try
            {
                if (backend == null || !backend.Available)
                {
                    if (backend != null) backend.Dispose();
                    Start(connection);
                }
                Refresh(null, false);
            }
            catch (Exception error) { ShowConnectionFailure(error); }
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
            Button("RenewButton").Content = compact ? "Link erneuern" : "Einladung erneuern";
            bool mobile = connection != null && connection.publicOrigin != null;
            int qrSize = compact ? 135 : 180;
            var qr = Control<Image>("QrImage");
            Control<ColumnDefinition>("QrColumn").Width = new GridLength(mobile ? qrSize : 0);
            qr.Width = qr.Height = qrSize;
            qr.Visibility = mobile ? Visibility.Visible : Visibility.Collapsed;
            Control<StackPanel>("QrDetails").Margin = new Thickness(mobile ? 15 : 0, 0, 0, 0);
        }

        private void SetWindowIcon()
        {
            using (var asset = Assembly.GetExecutingAssembly().GetManifestResourceStream("Dokju.Logo.png"))
            {
                var logo = BitmapFrame.Create(asset, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                Window.Icon = logo;
                Control<Image>("BrandLogo").Source = logo;
            }
        }

        private Connection LoadConnection()
        {
            var path = Path.Combine(directory, "connection.json");
            if (!File.Exists(path)) return new Connection();
            try
            {
                var saved = Installation.ReadJson<Connection>(path);
                if (saved != null && saved.port >= 1024 && saved.port <= 65535)
                {
                    saved.publicOrigin = NormalizeOrigin(saved.publicOrigin);
                    if (saved.address != "127.0.0.1")
                        connectionNotice = "Dokju nutzt die lokale Verbindung. Für Mobilgeräte kann unter Verbindung eine HTTPS-Adresse hinterlegt werden.";
                    saved.address = "127.0.0.1";
                    return saved;
                }
            }
            catch (IOException) { }
            catch (InvalidDataException) { }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            connectionNotice = "Die gespeicherte Verbindung ist ungültig. Dokju nutzt die lokale Verbindung.";
            return new Connection();
        }

        private void Start(Connection options)
        {
            backend = new Backend(directory, options.port, options.publicOrigin);
            if (options.port == 0 && options.publicOrigin == null) options.port = new Uri(backend.Origin).Port;
            ShowConnection(options);
        }

        private void ShowConnection(Connection options)
        {
            Label("ConnectionText").Text = (options.publicOrigin == null ? "Nur auf diesem Gerät" : "Portaladresse") + "\n" + backend.Origin;
        }

        private void SaveConnection(Connection options)
        {
            Installation.WriteAtomic(Path.Combine(directory, "connection.json"), Encoding.UTF8.GetBytes(Backend.Json.Serialize(options)));
        }

        private static string NormalizeOrigin(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return null;
            Uri uri;
            if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps ||
                uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new IOException("Bitte eine HTTPS-Adresse ohne Anmeldedaten, Pfad oder Abfrage eingeben.");
            var canonical = new UriBuilder(uri) { Host = uri.IdnHost };
            string origin = canonical.Uri.GetLeftPart(UriPartial.Authority);
            if (origin.Length > 71) throw new IOException("Die HTTPS-Adresse darf für den QR-Code höchstens 71 Zeichen lang sein.");
            return origin;
        }

        private void Configure()
        {
            var dialog = new FormDialog(Window, "Verbindung", "Hier legen Sie fest, wie Teilnehmende das Einwilligungsportal erreichen.");
            var portal = dialog.Field("HTTPS-Adresse für Mobilgeräte (optional)", connection.publicOrigin ?? "", 160);
            var port = dialog.Field("Lokaler Port", connection.port.ToString(German), 5);
            dialog.Note("Bei leerer Portaladresse lassen sich Einladungen auf diesem PC öffnen. Für Mobilgeräte leitet ein HTTPS-Reverse-Proxy auf diesem PC Anfragen an 127.0.0.1 und den lokalen Port weiter.");
            dialog.Note("Verwenden Sie für bereits ausgegebene Links weiterhin dieselbe Portaladresse. Im lokalen Modus gilt das auch für den Port.");
            Connection next = null;
            dialog.Accept = () =>
            {
                int number = Number(port);
                if (number < 1024 || number > 65535) throw new IOException("Bitte einen Port zwischen 1024 und 65535 angeben.");
                next = new Connection { port = number, publicOrigin = NormalizeOrigin(portal.Text) };
            };
            if (!dialog.Show("Verbindung übernehmen")) return;
            if (next.publicOrigin == connection.publicOrigin && next.port == connection.port) return;
            if (backend != null) backend.Dispose();
            backend = null;
            try
            {
                Start(next);
                SaveConnection(next);
            }
            catch
            {
                if (backend != null) backend.Dispose();
                try { Start(connection); }
                catch (Exception error) { ShowConnectionFailure(error); }
                throw;
            }
            connection = next;
            qrKey = null;
            AdaptLayout(Window.ActualWidth);
            Refresh(null, true);
            Message("Verbindung aktualisiert.");
        }

        private void NewCase()
        {
            CaseDialog(null);
        }

        private void CaseDialog(CaseRow existing)
        {
            if (disconnected) return;
            var profile = existing != null ? existing.info : dashboard.defaults;
            var dialog = new FormDialog(Window, existing == null ? "Neuer Vorgang" : "Vorgang bearbeiten",
                existing == null ? "Nach dem Anlegen können Sie die Einladung direkt im Browser öffnen." :
                "Wenn Sie Angaben ändern, erstellt Dokju neue Einladungen. Bereits geteilte Links werden ungültig.");
            var title = dialog.Field("Gesprächsbezeichnung", existing == null ? "" : existing.title, 90);
            var reference = dialog.Field("Aktenzeichen", existing == null ? "" : existing.reference, 64);
            var count = dialog.Field("Teilnehmende (1 bis 12)", existing == null ? "1" : existing.invites.Length.ToString(German), 2);
            dialog.Section("Angaben zur Einwilligung");
            var controller = dialog.Field("Verantwortliche Stelle", profile == null ? "" : profile.controller, 100);
            var provider = dialog.Field("KI-Anbieter", profile == null ? "" : profile.provider, 100);
            var contact = dialog.Field("Datenschutzkontakt", profile == null ? "" : profile.contact, 110);
            var audioDays = dialog.Field("Frist für Aufnahme und Transkript (Tage)", profile == null ? "" : profile.audioDays.ToString(German), 3);
            var proofDays = dialog.Field("Frist für den Nachweis in Dokju (Tage)", profile == null ? "" : profile.proofDays.ToString(German), 4);
            var remember = existing == null ? dialog.Check("Diese Angaben für neue Vorgänge merken", true) : null;
            string id = null;
            bool changed = false;
            dialog.Accept = () =>
            {
                var values = new
                {
                    title = FormDialog.Required(title, "Bitte eine Gesprächsbezeichnung eingeben."),
                    reference = FormDialog.Required(reference, "Bitte ein Aktenzeichen eingeben."),
                    count = FormDialog.InRange(count, 1, 12, "Bitte 1 bis 12 Teilnehmende eingeben."),
                    controller = FormDialog.Required(controller, "Bitte die verantwortliche Stelle eingeben."),
                    provider = FormDialog.Required(provider, "Bitte den KI-Anbieter eingeben."),
                    contact = FormDialog.Required(contact, "Bitte einen Datenschutzkontakt eingeben."),
                    audioDays = FormDialog.InRange(audioDays, 1, 365, "Bitte für Aufnahme und Transkript 1 bis 365 Tage eingeben."),
                    proofDays = FormDialog.InRange(proofDays, 1, 3650, "Bitte für den Nachweis 1 bis 3650 Tage eingeben."),
                    remember = remember != null && remember.IsChecked == true
                };
                if (existing == null) id = backend.Post<CaseRow>("/cases", values).id;
                else changed = backend.Post<CaseEdit>("/cases/" + existing.id + "/edit", values).changed;
            };
            if (!dialog.Show(existing == null ? "Vorgang anlegen" : "Änderungen speichern")) return;
            if (existing == null) Reveal(id);
            else if (changed)
            {
                qrKey = null;
                Refresh(existing.id, true);
                Message("Vorgang aktualisiert. Die neuen Einladungen können geteilt werden.");
            }
        }

        private static int Number(TextBox field)
        {
            int value;
            return Int32.TryParse(field.Text, out value) ? value : 0;
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
            if (!force && !disconnected && stamp == dataStamp) return;
            dashboard = latest;
            bool recovered = disconnected;
            disconnected = false;
            SetCaseActions(true);
            Label("CaseCount").Text = latest.cases.Length.ToString(German);
            Label("OpenCount").Text = latest.cases.SelectMany(item => item.invites).Count(person => person.status == "pending").ToString(German);
            Label("ReadyCount").Text = latest.cases.Count(item => item.ready).ToString(German);
            Filter(selection);
            dataStamp = stamp;
            if (recovered)
            {
                ShowConnection(connection);
                Message("Verbindung wiederhergestellt. Die Statusanzeige ist aktuell.");
            }
        }

        private void ShowConnectionFailure(Exception error)
        {
            disconnected = true;
            nextReconnect = DateTime.UtcNow.AddSeconds(5);
            qrKey = qrLink = null;
            SetCaseActions(false);
            Label("ReadyCount").Text = "–";
            Label("OpenCount").Text = "–";
            Label("ConnectionText").Text = "Verbindung unterbrochen";
            ShowStatusUnavailable();
            Message("Dokju versucht, die Verbindung wiederherzustellen. " + error.Message);
        }

        private void SetCaseActions(bool available)
        {
            foreach (var name in new[] { "NewButton", "FirstCaseButton", "EditButton", "ExportButton", "RenewButton", "WithdrawButton", "WithdrawalLinkButton", "VerifyButton" })
                Button(name).IsEnabled = available;
            Control<ListBox>("CaseList").IsEnabled = available;
            Control<ComboBox>("PersonList").Visibility = available ? Visibility.Visible : Visibility.Hidden;
            Label("PersonTime").Visibility = available ? Visibility.Visible : Visibility.Hidden;
        }

        private void ShowStatusUnavailable()
        {
            Label("DecisionState").Text = "Status derzeit nicht verfügbar";
            Label("DecisionState").Foreground = Brush("#33465D");
            Label("DecisionText").Text = "Warten Sie auf die erneute Prüfung, bevor Sie eine Aufnahme starten.";
            Control<Border>("DecisionBand").Background = Brush("#EDF4F6");
            Control<Border>("QrCard").Visibility = Visibility.Collapsed;
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
                bool empty = dashboard.cases.Length == 0;
                Label("EmptyTitle").Text = empty ? "Erste Einwilligung erfassen" : "Keine passenden Vorgänge";
                Label("EmptyHint").Text = empty ? "Vorgang anlegen, Einladung öffnen und die Entscheidung für die Akte sichern." : "Ändern Sie den Suchbegriff oder den Statusfilter.";
                Button("FirstCaseButton").Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
                return;
            }
            Label("DetailTitle").Text = item.title;
            Label("DetailReference").Text = item.reference + "  ·  " + item.info.controller;
            Button("EditButton").Visibility = item.editable ? Visibility.Visible : Visibility.Collapsed;
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
            if (disconnected)
            {
                ShowStatusUnavailable();
                return;
            }
            ShowPerson();
        }

        private void ShowPerson()
        {
            var item = Selected();
            var person = (Person)Control<ComboBox>("PersonList").SelectedItem;
            if (disconnected || item == null || person == null) return;
            bool pending = person.status == "pending";
            Label("PersonTime").Text = pending ? "Einladungslink gültig bis " + When(person.until) :
                person.changed == null ? "Die Einladung ist abgelaufen." : "Letzte Entscheidung am " + When(person.changed);
            Button("RenewButton").Visibility = pending || person.status == "expired" ? Visibility.Visible : Visibility.Collapsed;
            Button("WithdrawButton").Visibility = person.status == "accepted" ? Visibility.Visible : Visibility.Collapsed;
            Button("WithdrawalLinkButton").Visibility = person.status == "accepted" && connection.publicOrigin != null ? Visibility.Visible : Visibility.Collapsed;
            Button("MobileButton").Visibility = connection.publicOrigin == null ? Visibility.Visible : Visibility.Collapsed;
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
            Label("QrHint").Text = connection.publicOrigin == null ? "Am PC öffnen. Für Mobilgeräte kann unter Verbindung eine HTTPS-Adresse hinterlegt werden." :
                "Zeigen Sie den QR-Code der ausgewählten Person oder öffnen Sie den Link direkt.";
        }

        private void Renew()
        {
            var item = Selected();
            var person = (Person)Control<ComboBox>("PersonList").SelectedItem;
            if (item == null || person == null) return;
            if (person.status == "pending" && MessageBox.Show(Window, "Der bisherige Link wird ungültig. Neue Einladung erstellen?",
                "Einladung erneuern", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            backend.Post<CaseRow>("/cases/" + item.id + "/renew", new { person = person.number });
            qrKey = null;
            Refresh(item.id, true);
            Message("Die neue Einladung ist verfügbar.");
        }

        private void Withdraw()
        {
            var item = Selected();
            var person = (Person)Control<ComboBox>("PersonList").SelectedItem;
            if (item == null || person == null || person.status != "accepted") return;
            if (MessageBox.Show(Window, "Hat Person " + person.number + " ihre Einwilligung widerrufen? Dokju hebt die Freigabe auf. Informieren Sie gegebenenfalls den KI-Anbieter.",
                "Widerruf erfassen", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            backend.Post<CaseRow>("/cases/" + item.id + "/withdraw", new { person = person.number });
            Refresh(item.id, true);
            Message("Widerruf erfasst. Die Freigabe ist aufgehoben.");
        }

        private void CopyWithdrawalLink()
        {
            var item = Selected();
            var person = (Person)Control<ComboBox>("PersonList").SelectedItem;
            if (item == null || person == null || person.status != "accepted") return;
            if (MessageBox.Show(Window, "Diesen persönlichen Link ausschließlich an Person " + person.number + " weitergeben.",
                "Widerrufslink kopieren", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
            var link = backend.Get<Invitation>("/cases/" + item.id + "/withdrawal-link?person=" + person.number).link;
            Clipboard.SetText(link);
            Message("Widerrufslink kopiert.");
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

        public void Smoke(bool liveUpdate)
        {
            Window.ContentRendered += (sender, args) => Window.Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    var check = updates.StartupCheck;
                    if (check == null) throw new IOException("Die Updateprüfung wurde beim Öffnen nicht ausgelöst.");
                    await check;
                    if (!updates.LastCheckSucceeded) throw new IOException(updates.CheckMessage);
                    if (!liveUpdate && !Equals(Button("UpdateButton").Content, "Update verfügbar")) throw new IOException("Das Update wurde beim Öffnen nicht angezeigt.");
                    var item = backend.Post<CaseRow>("/cases", new
                    {
                        title = "Beratungsgespräch", reference = "AZ-42", count = 1, controller = "Verwaltung",
                        provider = "Protokolldienst", contact = "datenschutz@example.invalid", audioDays = 2, proofDays = 90
                    });
                    Reveal(item.id);
                    var filter = Control<ComboBox>("CaseFilter");
                    filter.SelectedIndex = 2;
                    if (Control<ListBox>("CaseList").Items.Count != 0) throw new IOException("Statusfilter ist fehlerhaft.");
                    filter.SelectedIndex = 0;
                    var search = Control<TextBox>("SearchBox");
                    search.Text = "AZ-42";
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
                    ShowConnectionFailure(new IOException("Verbindung unterbrochen."));
                    if (Button("ExportButton").IsEnabled || Control<Border>("QrCard").Visibility != Visibility.Collapsed ||
                        Label("DecisionState").Text != "Status derzeit nicht verfügbar") throw new IOException("Der veraltete Status ist weiterhin sichtbar.");
                    nextReconnect = DateTime.MinValue;
                    PollStatus();
                    if (disconnected || !Button("ExportButton").IsEnabled || qrLink == null || !Label("ConnectionText").Text.Contains(backend.Origin))
                        throw new IOException("Die Verbindung wurde nicht wiederhergestellt.");
                    backend.Dispose();
                    PollStatus();
                    if (disconnected || !backend.Available) throw new IOException("Der lokale Dienst wurde nicht neu gestartet.");
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

        private sealed class StartupUpdateFixture : System.Net.Http.HttpMessageHandler
        {
            protected override System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellation)
            {
                cancellation.ThrowIfCancellationRequested();
                var segments = request.RequestUri.Segments;
                var repository = segments[2] + segments[3].TrimEnd('/');
                var commit = new string('a', 40);
                var tag = Installation.TagFor(999999999, commit);
                var json = Backend.Json.Serialize(new[] { new
                {
                    tag_name = tag,
                    assets = new[] { new { name = Installation.Package, state = "uploaded", size = 123,
                        digest = "sha256:" + new string('b', 64), browser_download_url = "https://github.com/" + repository + "/releases/download/" + tag + "/" + Installation.Package } }
                } });
                return System.Threading.Tasks.Task.FromResult(new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(json) });
            }
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
        // Retain the address field for older versions that read the same configuration.
        public string address { get; set; }
        public int port { get; set; }
        public string publicOrigin { get; set; }
        public Connection() { address = "127.0.0.1"; port = 8787; }
    }
    public sealed class Dashboard
    {
        public CaseRow[] cases { get; set; }
        public Profile defaults { get; set; }
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
        public bool editable { get; set; }
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
    public sealed class CaseEdit { public bool changed { get; set; } }
    public sealed class ExportResult { public string pdf { get; set; } public object proof { get; set; } }
    public sealed class Verification { public bool valid { get; set; } public bool? document { get; set; } }
}
