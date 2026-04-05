using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using NurMarketKassa.Services;

namespace NurMarketKassa.Views;

public partial class WeighedProductDialog : Window
{
    private readonly ScaleReaderService? _scale;
    private readonly DispatcherTimer _timer;

    public string? QuantityNormalized { get; private set; }

    public WeighedProductDialog(
        string productTitle,
        string pricePerKgLine,
        ScaleReaderService? scale,
        string? initialKg = null,
        string okButtonText = "В чек",
        string? windowTitle = null)
    {
        InitializeComponent();
        _scale = scale;
        if (!string.IsNullOrEmpty(windowTitle))
            Title = windowTitle;
        OkButton.Content = okButtonText;
        TitleBlock.Text = $"Взвесить: {productTitle}";
        PriceBlock.Text = string.IsNullOrEmpty(pricePerKgLine) ? "" : $"Цена за кг: {pricePerKgLine}";

        if (!string.IsNullOrEmpty(initialKg))
            WeightBox.Text = initialKg;
        else if (_scale?.LastWeight is { } w && w > 0)
            WeightBox.Text = FormatWeight(w);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => RefreshLiveScale();
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    private void WeightBox_GotFocus(object sender, RoutedEventArgs e) => TouchKeyboard.TryShow();

    private void RefreshLiveScale()
    {
        var w = _scale?.LastWeight;
        LiveScaleText.Text = w is > 0 ? $"{FormatWeight(w.Value)} кг" : "—";
    }

    private static string FormatWeight(double w) =>
        w.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');

    private void FromScale_Click(object sender, RoutedEventArgs e)
    {
        if (_scale is null)
        {
            MessageBox.Show("Весы не настроены — включите COM в «Настройки кассы».", "Весы", MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var w = _scale.LastWeight;
        if (w is null or <= 0)
        {
            MessageBox.Show("Нет веса с весов (поставьте товар и подождите).", "Весы", MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        WeightBox.Text = FormatWeight(w.Value);
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => TryCloseOk();

    private void WeightBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            TryCloseOk();
        }
    }

    private void TryCloseOk()
    {
        var err = OrderDiscountHelper.ValidateQuantity(WeightBox.Text);
        if (err != null)
        {
            MessageBox.Show(err, "Вес", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        QuantityNormalized = OrderDiscountHelper.NormalizeDecimal(WeightBox.Text);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
