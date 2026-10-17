using System.Windows;
using System.Windows.Controls;

namespace WeChatBridge.Windows;

public partial class TextImportWindow : Window
{
    public TextImportWindow(string text = "")
    {
        InitializeComponent();
        BodyInput.Text = text;
        Loaded += (_, _) => BodyInput.Focus();
    }

    public string ChatText => BodyInput.Text;
    public string ChatName => ChatNameInput.Text;

    private void BodyInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ImportButton is not null) ImportButton.IsEnabled = !string.IsNullOrWhiteSpace(BodyInput.Text);
    }

    private void Import_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
