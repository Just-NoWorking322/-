using System.Windows;

namespace NurMarketKassa.Views;

public partial class CloseShiftDialog : Window
{
    /// <summary>null или пустая строка — не передаём closing_cash в API.</summary>
    public string? ClosingCashOrNull =>
        string.IsNullOrWhiteSpace(ClosingCashBox.Text) ? null : ClosingCashBox.Text.Trim();

    public CloseShiftDialog()
    {
        InitializeComponent();
        ClosingCashBox.Focus();
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
