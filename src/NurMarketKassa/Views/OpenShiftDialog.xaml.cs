using System.Windows;

namespace NurMarketKassa.Views;

public partial class OpenShiftDialog : Window
{
    public string OpeningCash => OpeningCashBox.Text.Trim();

    public OpenShiftDialog()
    {
        InitializeComponent();
        OpeningCashBox.Focus();
        OpeningCashBox.SelectAll();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
