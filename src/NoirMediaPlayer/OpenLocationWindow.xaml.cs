using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using NoirMediaPlayer.Services;

namespace NoirMediaPlayer;

public partial class OpenLocationWindow : Window
{
    private string _mediaLocation = string.Empty;

    public OpenLocationWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            LocationTextBox.Focus();
            LocationTextBox.SelectAll();
        };
    }

    public string MediaLocation => _mediaLocation;

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!MediaSourceService.TryNormalizeNetworkLocation(LocationTextBox.Text, out var normalizedLocation))
        {
            LocationTextBox.SelectAll();
            LocationTextBox.Focus();
            return;
        }

        _mediaLocation = normalizedLocation;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (Keyboard.FocusedElement is ButtonBase)
            {
                return;
            }

            Open_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
    }
}
