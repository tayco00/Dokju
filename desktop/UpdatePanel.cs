using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace Dokju
{
    internal sealed class UpdatePanel : IDisposable
    {
        private const string OfficialRepository = "tayco00/Dokju";
        private readonly Window owner;
        private readonly Button entry;
        private readonly string root;
        private readonly BuildIdentity build;
        private readonly DispatcherTimer timer;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly GitHubUpdates client;
        private string repository;
        private UpdateOffer offer;
        private Task checkTask;
        private bool checking;
        private bool installing;
        private bool disposed;
        private string checkMessage = "Noch nicht geprüft.";
        private event Action StateChanged;

        internal UpdatePanel(Window owner, Button entry, string data, GitHubUpdates client = null)
        {
            this.owner = owner;
            this.entry = entry;
            this.client = client ?? new GitHubUpdates();
            root = Installation.Root(data);
            build = BuildIdentity.Read(AppDomain.CurrentDomain.BaseDirectory);
            var settings = Path.Combine(root, "repository.txt");
            repository = build.repository;
            if (repository == null && File.Exists(settings) && new FileInfo(settings).Length <= 160)
            {
                var saved = File.ReadAllText(settings).Trim();
                if (Installation.RepositoryIsValid(saved)) repository = saved;
            }
            repository = repository ?? OfficialRepository;
            entry.Click += (sender, args) => Show();
            timer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
            timer.Tick += async (sender, args) => await Check();
            owner.Loaded += CheckAtStartup;
        }

        private async void CheckAtStartup(object sender, RoutedEventArgs args)
        {
            owner.Loaded -= CheckAtStartup;
            timer.Start();
            await Check();
        }

        private Task Check()
        {
            if (disposed || installing || repository == null) return Task.FromResult(0);
            if (checking) return checkTask;
            checkTask = CheckRepository(repository);
            return checkTask;
        }

        private async Task CheckRepository(string source)
        {
            checking = true;
            LastCheckSucceeded = false;
            checkMessage = "Dokju sucht nach Updates …";
            entry.Content = "Prüfe Updates …";
            NotifyState();
            try
            {
                var state = Installation.Versions(root);
                var baseline = Math.Max(build.sequence, Installation.ActiveSequence(root, state, source));
                offer = await client.Check(source, baseline, state.rejected, lifetime.Token);
                checkMessage = offer == null ? "Dokju ist auf dem neuesten verfügbaren Stand." : "Verfügbar · " + offer.Caption;
                LastCheckSucceeded = true;
            }
            catch (Exception error)
            {
                offer = null;
                checkMessage = ErrorText(error);
            }
            finally
            {
                checking = false;
                if (!disposed)
                {
                    entry.Content = offer == null ? "Updates" : "Update verfügbar";
                    NotifyState();
                }
            }
        }

        private void NotifyState() { if (StateChanged != null) StateChanged(); }

        internal Task StartupCheck { get { return checkTask; } }
        internal bool LastCheckSucceeded { get; private set; }
        internal string CheckMessage { get { return checkMessage; } }

        internal void Show(Action<Window> snapshot = null)
        {
            var window = new Window
            {
                Owner = owner, Title = "Updates | Dokju", Width = 470, SizeToContent = SizeToContent.Height,
                ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = owner.Background, Foreground = owner.Foreground, FontFamily = owner.FontFamily, FontSize = 14
            };
            var layout = new StackPanel { Margin = new Thickness(28) };
            window.Content = layout;
            layout.Children.Add(new TextBlock { Text = "Softwareupdates", FontSize = 29, FontWeight = FontWeights.SemiBold });
            layout.Children.Add(new TextBlock
            {
                Text = build.sequence > 0 ? "Installiert · Build " + build.sequence + " · " + build.commit.Substring(0, 8) : "Installiert · Lokaler Build",
                Margin = new Thickness(0, 5, 0, 22)
            });
            layout.Children.Add(new TextBlock { Text = "GitHub-Repository", FontWeight = FontWeights.SemiBold });
            var field = new TextBox
            {
                Text = repository ?? "", IsReadOnly = build.sequence > 0, MaxLength = 140,
                Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 6, 0, 5)
            };
            AutomationProperties.SetName(field, "GitHub-Repository");
            layout.Children.Add(field);
            var explanation = new TextBlock
            {
                Text = build.sequence > 0 ? "Dokju prüft beim Start automatisch. Sie entscheiden, wann ein verfügbares Update installiert wird." :
                    "Öffentliches Dokju-Repository im Format Konto/Repository.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16)
            };
            layout.Children.Add(explanation);
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
            AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
            layout.Children.Add(status);
            var bar = new ProgressBar { Height = 5, Maximum = 100, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 12) };
            layout.Children.Add(bar);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            layout.Children.Add(actions);
            var check = new Button { Content = "Jetzt prüfen", Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var install = new Button { Content = "Installieren und neu starten", Padding = new Thickness(14, 8, 14, 8) };
            check.Style = (Style)owner.Resources["ButtonBase"];
            install.Style = (Style)owner.Resources["Primary"];
            actions.Children.Add(check);
            actions.Children.Add(install);
            var openRelease = new Hyperlink(new Run("Downloadseite öffnen"));
            var download = new TextBlock { Margin = new Thickness(0, 14, 0, 0) };
            download.Inlines.Add(openRelease);
            layout.Children.Add(download);
            openRelease.Click += (sender, args) =>
            {
                try { Process.Start(new ProcessStartInfo("https://github.com/" + repository + "/releases/latest") { UseShellExecute = true }); }
                catch (System.ComponentModel.Win32Exception) { status.Text = "Der Standardbrowser konnte nicht geöffnet werden."; }
            };
            Action showState = () =>
            {
                if (installing) return;
                var matching = offer != null && String.Equals(field.Text.Trim(), offer.Repository, StringComparison.OrdinalIgnoreCase);
                check.IsEnabled = field.IsEnabled = !checking;
                install.IsEnabled = !checking && matching;
                status.Text = !String.Equals(field.Text.Trim(), repository, StringComparison.OrdinalIgnoreCase) ?
                    "Wählen Sie Jetzt prüfen, um dieses Repository zu verwenden." : checkMessage;
            };
            showState();
            StateChanged += showState;
            field.TextChanged += (sender, args) => showState();
            check.Click += async (sender, args) =>
            {
                if (checking || installing) return;
                var chosen = field.Text.Trim();
                if (!Installation.RepositoryIsValid(chosen)) { status.Text = "Bitte Konto/Repository eingeben."; return; }
                try
                {
                    if (!String.Equals(chosen, repository, StringComparison.OrdinalIgnoreCase))
                    {
                        Directory.CreateDirectory(root);
                        Installation.WriteAtomic(Path.Combine(root, "repository.txt"), Encoding.UTF8.GetBytes(chosen));
                        repository = chosen;
                        offer = null;
                    }
                    await Check();
                }
                catch (Exception error) { status.Text = ErrorText(error); }
            };
            install.Click += async (sender, args) =>
            {
                if (checking || installing || offer == null || !String.Equals(field.Text.Trim(), offer.Repository, StringComparison.OrdinalIgnoreCase)) return;
                installing = true;
                check.IsEnabled = install.IsEnabled = field.IsEnabled = false;
                bar.Value = 0;
                bar.Visibility = Visibility.Visible;
                status.Text = "Update wird heruntergeladen und geprüft …";
                var selected = offer;
                using (var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                {
                    bool restarting = false;
                    System.ComponentModel.CancelEventHandler closing = (s, e) =>
                    {
                        if (restarting) e.Cancel = true;
                        else operation.Cancel();
                    };
                    window.Closing += closing;
                    try
                    {
                        var progress = new Progress<int>(value => bar.Value = value);
                        await client.Stage(root, selected, progress, operation.Token);
                        operation.Token.ThrowIfCancellationRequested();
                        restarting = true;
                        status.Text = "Dokju bereitet den Neustart vor …";
                        await PrepareRestart(selected.Tag, operation.Token);
                        window.Closing -= closing;
                        window.Close();
                        owner.Close();
                    }
                    catch (OperationCanceledException) { checkMessage = "Der Download wurde beendet oder hat zu lange gedauert."; }
                    catch (Exception error) { checkMessage = ErrorText(error); }
                    finally
                    {
                        window.Closing -= closing;
                        installing = false;
                        bar.Visibility = Visibility.Collapsed;
                        showState();
                    }
                }
            };
            Exception snapshotError = null;
            if (snapshot != null) window.ContentRendered += (sender, args) =>
            {
                try { snapshot(window); }
                catch (Exception error) { snapshotError = error; }
                finally { window.Close(); }
            };
            try { window.ShowDialog(); }
            finally { StateChanged -= showState; }
            if (snapshotError != null) throw snapshotError;
        }

        private async Task PrepareRestart(string tag, CancellationToken cancellation)
        {
            var request = Guid.NewGuid();
            var signal = Installation.ActivationSignal(root, request);
            var executable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Installation.Launcher);
            try
            {
                using (var current = Process.GetCurrentProcess())
                using (var launcher = Installation.Launch(executable, "--activate " + tag + " " + current.Id.ToString(CultureInfo.InvariantCulture) + " " + request.ToString("N")))
                {
                    var wait = Stopwatch.StartNew();
                    while (!File.Exists(signal))
                    {
                        if (launcher.HasExited || wait.ElapsedMilliseconds > 6000)
                            throw new IOException("Der Neustart konnte nicht vorbereitet werden. Dokju bleibt geöffnet.");
                        await Task.Delay(100, cancellation);
                    }
                }
            }
            finally { if (File.Exists(signal)) File.Delete(signal); }
        }

        private static string ErrorText(Exception error)
        {
            if (error is OperationCanceledException) return "Die Verbindung zu GitHub hat zu lange gedauert. Versuchen Sie es erneut oder öffnen Sie die Downloadseite.";
            if (!(error is System.Net.Http.HttpRequestException)) return error.Message;
            var cause = error.InnerException as WebException;
            if (cause != null)
            {
                if (cause.Status == WebExceptionStatus.NameResolutionFailure || cause.Status == WebExceptionStatus.ProxyNameResolutionFailure)
                    return "Windows konnte die GitHub- oder Proxy-Adresse nicht auflösen. Öffnen Sie die Downloadseite im Browser.";
                if (cause.Status == WebExceptionStatus.TrustFailure || cause.Status == WebExceptionStatus.SecureChannelFailure)
                    return "Windows konnte die sichere Verbindung zu GitHub nicht herstellen. Öffnen Sie die Downloadseite im Browser.";
            }
            return "Dokju konnte GitHub über diese Verbindung nicht erreichen. Öffnen Sie die Downloadseite im Browser.";
        }

        public void Dispose()
        {
            disposed = true;
            owner.Loaded -= CheckAtStartup;
            timer.Stop();
            lifetime.Cancel();
            lifetime.Dispose();
        }
    }
}
