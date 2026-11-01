using System.Windows.Controls;

namespace LesTool.Views.Pages;

public partial class PlaceholderPage : UserControl
{
    public PlaceholderPage(string title, string message)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
    }
}
