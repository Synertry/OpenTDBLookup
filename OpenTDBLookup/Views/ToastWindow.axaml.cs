using Avalonia.Controls;

namespace OpenTDBLookup.Views;

public partial class ToastWindow : Window
{
    public ToastWindow()
    {
        InitializeComponent();
    }

    public void SetContent(string title, string body)
    {
        TitleText.Text = title;
        BodyText.Text = body;
    }
}
