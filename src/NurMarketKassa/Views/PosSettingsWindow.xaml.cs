using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using NurMarketKassa.Services;

namespace NurMarketKassa.Views;

public partial class PosSettingsWindow : Window
{
    public PosSettingsWindow()
    {
        InitializeComponent();
        var p = UserPreferences.Instance;
        ScaleEnabledCheck.IsChecked = p.ScaleEnabled;
        ScaleBaudBox.Text = p.ScaleBaudRate.ToString();
        ScaleHexBox.Text = p.ScaleRequestHex ?? "";
        ScalePollBox.Text = p.ScalePollMs.ToString();

        ReceiptEnabledCheck.IsChecked = p.ReceiptEnabled;
        ReceiptLptBox.Text = p.ReceiptDevicePath;
        ReceiptEscRBox.Text = p.ReceiptEscR?.ToString() ?? "";
        ReceiptRetryBox.Text = p.ReceiptRetryCount.ToString();

        FullscreenCheck.IsChecked = p.Fullscreen;
        AutostartCheck.IsChecked = p.Autostart || AutostartHelper.IsEnabled();

        var ports = SerialPort.GetPortNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        if (!ports.Contains(p.ScaleComPort, StringComparer.OrdinalIgnoreCase))
            ports.Insert(0, p.ScaleComPort);
        ScaleComCombo.ItemsSource = ports;
        ScaleComCombo.Text = p.ScaleComPort;

        SelectComboByTag(ReceiptEncCombo, p.ReceiptEncoding.ToLowerInvariant());
        var tableTag = p.ReceiptEscPosTable?.ToString() ?? "";
        foreach (ComboBoxItem? it in ReceiptTableCombo.Items)
        {
            if (it?.Tag?.ToString() == tableTag)
            {
                ReceiptTableCombo.SelectedItem = it;
                break;
            }
        }

        if (ReceiptTableCombo.SelectedItem == null && ReceiptTableCombo.Items.Count > 0)
            ReceiptTableCombo.SelectedIndex = 0;
    }

    private static void SelectComboByTag(ComboBox box, string value)
    {
        foreach (ComboBoxItem? it in box.Items)
        {
            if (string.Equals(it?.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = it;
                return;
            }
        }

        if (box.Items.Count > 0)
            box.SelectedIndex = 0;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var p = UserPreferences.Instance;
        p.ScaleEnabled = ScaleEnabledCheck.IsChecked == true;
        p.ScaleComPort = string.IsNullOrWhiteSpace(ScaleComCombo.Text) ? "COM2" : ScaleComCombo.Text.Trim();
        if (!int.TryParse(ScaleBaudBox.Text.Trim(), out var baud) || baud <= 0)
            baud = 9600;
        p.ScaleBaudRate = baud;
        p.ScaleRequestHex = string.IsNullOrWhiteSpace(ScaleHexBox.Text) ? null : ScaleHexBox.Text.Trim();
        if (!int.TryParse(ScalePollBox.Text.Trim(), out var poll) || poll < 0)
            poll = 0;
        p.ScalePollMs = poll;

        p.ReceiptEnabled = ReceiptEnabledCheck.IsChecked == true;
        p.ReceiptDevicePath = string.IsNullOrWhiteSpace(ReceiptLptBox.Text) ? "LPT1" : ReceiptLptBox.Text.Trim();
        if (ReceiptEncCombo.SelectedItem is ComboBoxItem encIt && encIt.Tag is string encTag)
            p.ReceiptEncoding = encTag;
        else
            p.ReceiptEncoding = "cp866";

        p.ReceiptEscPosTable = null;
        if (ReceiptTableCombo.SelectedItem is ComboBoxItem tIt)
        {
            var tagStr = tIt.Tag?.ToString();
            if (!string.IsNullOrEmpty(tagStr) && int.TryParse(tagStr, out var tb))
                p.ReceiptEscPosTable = tb;
        }

        var escR = ReceiptEscRBox.Text.Trim();
        p.ReceiptEscR = string.IsNullOrEmpty(escR) ? null : int.TryParse(escR, out var er) ? er : null;

        if (!int.TryParse(ReceiptRetryBox.Text.Trim(), out var retry) || retry < 1)
            retry = 3;
        p.ReceiptRetryCount = retry;

        p.Fullscreen = FullscreenCheck.IsChecked == true;
        p.Autostart = AutostartCheck.IsChecked == true;

        p.SaveToDisk();
        AutostartHelper.SyncFromPreference(p.Autostart);

        if (Owner is MainWindow mw)
            mw.ApplyHardwareAndUiPreferences();

        DialogResult = true;
    }
}
