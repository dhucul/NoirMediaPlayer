using System.Windows;
using System.Windows.Input;

namespace NoirMediaPlayer;

public partial class OpenLocationWindow : Window
{
    public OpenLocationWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            LocationTextBox.Focus();
            LocationTextBox.SelectAll();
        };
    }

    public string MediaLocation => LocationTextBox.Text;

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(LocationTextBox.Text.Trim(), UriKind.Absolute, out _))
        {
            LocationTextBox.SelectAll();
            LocationTextBox.Focus();
            return;
        }

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
            Open_Click(this, new RoutedEventArgs());
        }
        else if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
    }
}
