using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Dokju
{
    internal sealed class UpdatePanel : IDisposable
    {
        private readonly Window owner;
        private readonly Button entry;
        private readonly string root;
        private readonly BuildIdentity build;
        private readonly DispatcherTimer timer;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly GitHubUpdates client = new GitHubUpdates();
        private string repository;
        private UpdateOffer offer;
        private bool busy;

        internal UpdatePanel(Window owner, Button entry, string data)
        {
            this.owner = owner;
            this.entry = entry;
            root = Installation.Root(data);
            build = BuildIdentity.Read(AppDomain.CurrentDomain.BaseDirectory);
            var settings = Path.Combine(root, "repository.txt");
            repository = build.repository;
            if (repository == null && File.Exists(settings) && new FileInfo(settings).Length <= 160)
            {
                var saved = File.ReadAllText(settings).Trim();
                if (Installation.RepositoryIsValid(saved)) repository = saved;
            }
            entry.Click += (sender, args) => Show();
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            timer.Tick += async (sender, args) =>
            {
                timer.Interval = TimeSpan.FromHours(6);
                if (repository == null || busy) return;
                try { await Check(); }
                catch (Exception) { }
            };
            timer.Start();
        }

        private async Task Check()
        {
            busy = true;
            try
            {
                var state = Installation.Versions(root);
                var baseline = Math.Max(build.sequence, Installation.ActiveSequence(root, state, repository));
                offer = await client.Check(repository, baseline, state.rejected, lifetime.Token);
                entry.Content = offer == null ? "Updates" : "Update verfügbar";
            }
            finally { busy = false; }
        }

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
                Text = "Öffentliches Dokju-Repository im Format Konto/Repository. Beim Installieren wird Dokju kurz neu gestartet.",
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
            Action showState = () =>
            {
                var matching = offer != null && String.Equals(field.Text.Trim(), offer.Repository, StringComparison.OrdinalIgnoreCase);
                install.IsEnabled = !busy && matching;
                status.Text = matching ? "Verfügbar · " + offer.Caption : (repository == null ? "Noch kein Update-Repository hinterlegt." : "Neue Versionen können hier geprüft werden.");
            };
            showState();
            field.TextChanged += (sender, args) => showState();
            check.Click += async (sender, args) =>
            {
                if (busy) { status.Text = "Die automatische Prüfung läuft bereits."; return; }
                var chosen = field.Text.Trim();
                if (!Installation.RepositoryIsValid(chosen)) { status.Text = "Bitte Konto/Repository eingeben."; return; }
                check.IsEnabled = install.IsEnabled = field.IsEnabled = false;
                status.Text = "GitHub wird geprüft …";
                try
                {
                    Directory.CreateDirectory(root);
                    Installation.WriteAtomic(Path.Combine(root, "repository.txt"), Encoding.UTF8.GetBytes(chosen));
                    repository = chosen;
                    offer = null;
                    entry.Content = "Updates";
                    await Check();
                    showState();
                    if (offer == null) status.Text = "Keine neuere veröffentlichte Version gefunden.";
                }
                catch (Exception error) { status.Text = ErrorText(error); }
                finally { check.IsEnabled = field.IsEnabled = true; }
            };
            install.Click += async (sender, args) =>
            {
                if (busy || offer == null) return;
                busy = true;
                check.IsEnabled = install.IsEnabled = field.IsEnabled = false;
                bar.Value = 0;
                bar.Visibility = Visibility.Visible;
                status.Text = "Update wird heruntergeladen und geprüft …";
                var selected = offer;
                using (var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                {
                    System.ComponentModel.CancelEventHandler closing = (s, e) => operation.Cancel();
                    window.Closing += closing;
                    try
                    {
                        var progress = new Progress<int>(value => bar.Value = value);
                        await client.Stage(root, selected, progress, operation.Token);
                        operation.Token.ThrowIfCancellationRequested();
                        await PrepareRestart(selected.Tag, operation.Token);
                        window.Close();
                        owner.Close();
                    }
                    catch (OperationCanceledException) { status.Text = "Der Download wurde beendet oder hat zu lange gedauert."; }
                    catch (Exception error) { status.Text = ErrorText(error); }
                    finally
                    {
                        window.Closing -= closing;
                        busy = false;
                        bar.Visibility = Visibility.Collapsed;
                        check.IsEnabled = field.IsEnabled = install.IsEnabled = true;
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
            window.ShowDialog();
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
            return error is OperationCanceledException ? "GitHub hat nicht rechtzeitig geantwortet." :
                error is System.Net.Http.HttpRequestException ? "GitHub ist gerade nicht erreichbar. Bitte Verbindung prüfen und erneut versuchen." : error.Message;
        }

        public void Dispose()
        {
            timer.Stop();
            lifetime.Cancel();
            lifetime.Dispose();
        }
    }
}
