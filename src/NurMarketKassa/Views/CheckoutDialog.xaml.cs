using System.Globalization;
using System.Windows;
using NurMarketKassa.Services;

namespace NurMarketKassa.Views;

public partial class CheckoutDialog : Window
{
    private readonly double _totalDue;

    public string PaymentMethodKey { get; private set; } = "cash";
    public string CashReceivedForApi { get; private set; } = "";
    public bool WantPrintReceipt { get; private set; }

    public CheckoutDialog(double totalDue)
    {
        _totalDue = totalDue;
        InitializeComponent();
        TotalLabel.Text = $"К оплате: {totalDue.ToString("0.00", CultureInfo.InvariantCulture)} сом";
        CashReceivedBox.Text = totalDue.ToString("0.00", CultureInfo.InvariantCulture);
        SyncCashFieldVisibility();
        CashReceivedBox.Focus();
        CashReceivedBox.SelectAll();
    }

    private void PayMethod_Changed(object sender, RoutedEventArgs e) => SyncCashFieldVisibility();

    private void SyncCashFieldVisibility()
    {
        // Checked срабатывает во время InitializeComponent раньше, чем назначен CashReceivedBox (ниже в XAML).
        if (CashReceivedBox is null || RbCash is null)
            return;
        var cash = RbCash.IsChecked == true;
        CashReceivedBox.IsEnabled = cash;
        CashReceivedBox.Opacity = cash ? 1 : 0.5;
    }

    private void Pay_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        ErrorText.Text = "";

        PaymentMethodKey = RbTransfer.IsChecked == true ? "transfer" : "cash";
        WantPrintReceipt = PrintReceiptCheck.IsChecked == true;

        if (PaymentMethodKey == "cash")
        {
            var err = CheckoutValidation.ValidateCashReceived(CashReceivedBox.Text, _totalDue);
            if (err != null)
            {
                ErrorText.Text = err;
                ErrorText.Visibility = Visibility.Visible;
                return;
            }

            CashReceivedForApi = CheckoutValidation.NormalizeDecimal(CashReceivedBox.Text);
        }
        else
            CashReceivedForApi = "0.00";

        /* DialogResult сам закрывает окно; второй Close() даёт InvalidOperationException и вылет приложения */
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
