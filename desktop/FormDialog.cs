using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Dokju
{
    internal sealed class FormDialog
    {
        private readonly Window window;
        private readonly StackPanel fields = new StackPanel();
        private readonly TextBlock error;
        private Control first;
        public Action Accept { get; set; }

        public FormDialog(Window owner, string title, string subtitle)
        {
            window = new Window
            {
                Owner = owner, Title = title + " | Dokju", WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 500, SizeToContent = SizeToContent.Height, MaxHeight = Math.Max(410, SystemParameters.WorkArea.Height - 70),
                ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Background = owner.Background,
                Foreground = owner.Foreground, FontFamily = owner.FontFamily, FontSize = 14
            };
            var layout = new DockPanel { Margin = new Thickness(24, 19, 24, 15) };
            window.Content = layout;
            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 11) };
            DockPanel.SetDock(header, Dock.Top);
            layout.Children.Add(header);
            header.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 27 });
            header.Children.Add(new TextBlock
            {
                Text = subtitle, TextWrapping = TextWrapping.Wrap, Foreground = Color("#53667A"), Margin = new Thickness(0, 3, 0, 0)
            });
            var footer = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            DockPanel.SetDock(footer, Dock.Bottom);
            layout.Children.Add(footer);
            error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Color("#9C2F24"), Visibility = Visibility.Collapsed };
            footer.Children.Add(error);
            Buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 7, 0, 0) };
            footer.Children.Add(Buttons);
            layout.Children.Add(new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        }

        private StackPanel Buttons { get; set; }

        private static SolidColorBrush Color(string value)
        {
            return new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(value));
        }

        private void AddField(string name, Control control)
        {
            fields.Children.Add(new Label { Content = name, Target = control, Padding = new Thickness(0, 0, 0, 3), FontWeight = FontWeights.SemiBold });
            control.Margin = new Thickness(0, 0, 5, 8);
            fields.Children.Add(control);
            if (first == null) first = control;
        }

        public TextBox Field(string name, string value, int limit)
        {
            var input = new TextBox { Text = value, MaxLength = limit, Padding = new Thickness(8, 6, 8, 6), BorderBrush = Color("#C6D2DD") };
            AddField(name, input);
            return input;
        }

        public ComboBox Select(string name, string[] options, string value)
        {
            var input = new ComboBox { ItemsSource = options, SelectedItem = value, MinHeight = 31 };
            if (input.SelectedIndex < 0) input.SelectedIndex = 0;
            AddField(name, input);
            return input;
        }

        public void Note(string value)
        {
            fields.Children.Add(new TextBlock
            {
                Text = value, TextWrapping = TextWrapping.Wrap, Foreground = Color("#53667A"), FontSize = 12, Margin = new Thickness(0, 3, 6, 3)
            });
        }

        public bool Show(string caption)
        {
            var cancel = new Button { Content = "Abbrechen", IsCancel = true, Style = (Style)window.Owner.Resources["ButtonBase"], Margin = new Thickness(0, 0, 8, 0) };
            var save = new Button
            {
                Content = caption, IsDefault = true, Style = (Style)window.Owner.Resources["Primary"]
            };
            save.Click += (sender, args) =>
            {
                try
                {
                    if (Accept != null) Accept();
                    window.DialogResult = true;
                }
                catch (Exception exception)
                {
                    error.Text = exception.Message;
                    error.Visibility = Visibility.Visible;
                }
            };
            Buttons.Children.Add(cancel);
            Buttons.Children.Add(save);
            window.Loaded += (sender, args) => { if (first != null) first.Focus(); };
            return window.ShowDialog() == true;
        }
    }
}
