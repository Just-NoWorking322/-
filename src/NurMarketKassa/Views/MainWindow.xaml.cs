using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.Views;

public partial class MainWindow : Window
{
    private string _barcodeBuf = "";
    private long _barcodeLastTick;
    private const int BarcodeInterkeyMs = 220;
    private const int MinBarcodeLen = 4;
    private const int BarcodeMaxLen = 64;

    private static readonly SolidColorBrush BrushMuted = new(Color.FromRgb(0x9C, 0xA3, 0xAF));
    private static readonly SolidColorBrush BrushOk = new(Color.FromRgb(0x34, 0xD3, 0x99));
    private static readonly SolidColorBrush BrushWarn = new(Color.FromRgb(0xFB, 0xBF, 0x24));
    private static readonly SolidColorBrush ShiftOpenBg = new(Color.FromRgb(0x14, 0x3D, 0x2C));
    private static readonly SolidColorBrush ShiftOpenBorder = new(Color.FromRgb(0x16, 0x65, 0x34));
    private static readonly SolidColorBrush ShiftOpenText = new(Color.FromRgb(0x86, 0xEF, 0xAC));
    private static readonly SolidColorBrush ShiftWarnBg = new(Color.FromRgb(0x42, 0x27, 0x06));
    private static readonly SolidColorBrush ShiftWarnBorder = new(Color.FromRgb(0xB4, 0x53, 0x09));
    private static readonly SolidColorBrush ShiftWarnText = new(Color.FromRgb(0xFC, 0xD3, 0x4D));

    public ObservableCollection<CartLineRow> CartLines { get; } = new();

    private readonly ObservableCollection<CatalogProductTileVm> _tilesKg = new();
    private readonly ObservableCollection<CatalogProductTileVm> _tilesPiece = new();
    private readonly ObservableCollection<CatalogProductTileVm> _searchTiles = new();
    private readonly ProductThumbService _thumbService = new();
    private ScaleReaderService? _scaleService;
    private DispatcherTimer? _searchDebounceTimer;
    private DispatcherTimer? _scaleUiTimer;
    private DispatcherTimer? _toastTimer;
    private string _pendingSearchQuery = "";

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        CatalogItemsKg.ItemsSource = _tilesKg;
        CatalogItemsPiece.ItemsSource = _tilesPiece;
        CatalogSearchItems.ItemsSource = _searchTiles;

        _searchDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Math.Clamp(App.Settings.Catalog.SearchDebounceMs, 120, 2000)),
        };
        _searchDebounceTimer.Tick += SearchDebounce_Tick;

        var api = App.Api;
        UserTitleText.Text = TryUserLabel(api.UserPayload);
        BranchText.Text = FormatBranchLine(api.ActiveBranchId);
        RebindCartUi();
        UpdateShiftBanner();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ProfileStatusText.Text = "Загрузка профиля…";
        ProfileStatusText.Foreground = BrushMuted;

        try
        {
            var profile = await App.Api.GetProfileAsync().ConfigureAwait(true);
            if (profile.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                ProfileStatusText.Text = "Профиль: пустой ответ.";
                ProfileStatusText.Foreground = BrushWarn;
                return;
            }

            App.Api.ApplyBranchFromProfile(profile);
            UserTitleText.Text = TryUserLabel(profile);
            var branchId = App.Api.ActiveBranchId ?? TryBranchId(profile);
            BranchText.Text = FormatBranchLine(branchId);
            BranchText.ToolTip = string.IsNullOrEmpty(branchId) ? null : branchId;
            ProfileStatusText.Text = "Профиль загружен (GET /api/users/profile/).";
            ProfileStatusText.Foreground = BrushOk;
        }
        catch (ApiException ex)
        {
            ProfileStatusText.Text = $"Профиль: {ex.Message}";
            ProfileStatusText.Foreground = BrushWarn;
        }
        catch (HttpRequestException ex)
        {
            ProfileStatusText.Text = $"Профиль: {ex.Message}";
            ProfileStatusText.Foreground = BrushWarn;
        }
        catch (TaskCanceledException)
        {
            ProfileStatusText.Text = "Профиль: таймаут запроса.";
            ProfileStatusText.Foreground = BrushWarn;
        }

        await RefreshShiftStateAsync().ConfigureAwait(true);

        ApplyFullscreenPreference();
        _scaleService = new ScaleReaderService(UserPreferences.Instance.ToScaleSettings());
        _scaleService.Start();
        _scaleUiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _scaleUiTimer.Tick += (_, _) => UpdateScaleStatusLine();
        _scaleUiTimer.Start();
        UpdateScaleStatusLine();

        await LoadCatalogAsync().ConfigureAwait(true);
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _searchDebounceTimer?.Stop();
        _scaleUiTimer?.Stop();
        _toastTimer?.Stop();
        _scaleService?.Dispose();
        _scaleService = null;
    }

    private void UpdateScaleStatusLine()
    {
        var sp = UserPreferences.Instance;
        if (sp.ScaleEnabled)
        {
            var s = _scaleService?.Status ?? "—";
            var w = _scaleService?.LastWeight;
            var wtxt = w is > 0 ? $"{w.Value.ToString("0.###", CultureInfo.InvariantCulture)} кг" : "—";
            ScaleStatusText.Text =
                $"Весы {sp.ScaleComPort} @ {sp.ScaleBaudRate}: {s} · на весах: {wtxt}";
        }
        else
        {
            ScaleStatusText.Text = "Весы выключены. Включите в «Настройки кассы» и выберите COM-порт.";
        }
    }

    private async Task LoadCatalogAsync()
    {
        try
        {
            var raw = await App.Api
                .ProductsCatalogAsync(App.Settings.Catalog.QuickCatalogLimit, App.Settings.Catalog.CatalogMaxPages)
                .ConfigureAwait(true);
            var apiBase = App.Settings.ApiBaseUrl;
            _tilesKg.Clear();
            _tilesPiece.Clear();
            foreach (var el in raw)
            {
                var vm = ProductCatalogMapper.TryTile(el, apiBase);
                if (vm == null)
                    continue;
                if (vm.MustWeigh)
                    _tilesKg.Add(vm);
                else
                    _tilesPiece.Add(vm);
            }

            ShowToast($"Каталог: {_tilesKg.Count} весовых, {_tilesPiece.Count} штучных.");
            _ = HydrateThumbsAsync(_tilesKg.Concat(_tilesPiece).ToList());
        }
        catch (ApiException ex)
        {
            ShowToast($"Каталог: {ex.Message}", warn: true);
        }
        catch (HttpRequestException ex)
        {
            ShowToast(string.IsNullOrWhiteSpace(ex.Message) ? "Каталог: нет сети." : $"Каталог: {ex.Message}", warn: true);
        }
        catch (TaskCanceledException)
        {
            ShowToast("Каталог: таймаут.", warn: true);
        }
    }

    private async Task HydrateThumbsAsync(IReadOnlyList<CatalogProductTileVm> tiles)
    {
        var apiBase = App.Settings.ApiBaseUrl;
        foreach (var vm in tiles)
        {
            try
            {
                string? url = vm.ImageUrl;
                if (string.IsNullOrEmpty(url))
                {
                    var det = await App.Api.ProductsDetailAsync(vm.Id).ConfigureAwait(true);
                    if (det is { ValueKind: JsonValueKind.Object } d)
                        url = ProductImageUrl.TryGet(d, apiBase);
                }

                if (!string.IsNullOrEmpty(url))
                {
                    await _thumbService
                        .SetThumbAsync(Dispatcher, App.Api, apiBase, url, vm, default)
                        .ConfigureAwait(true);
                }
            }
            catch
            {
                /* превью необязательно */
            }
        }
    }

    private void CatalogSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _pendingSearchQuery = (CatalogSearchBox.Text ?? "").Trim();
        _searchDebounceTimer?.Stop();
        if (_pendingSearchQuery.Length < 2)
        {
            SearchOverlayPanel.Visibility = Visibility.Collapsed;
            return;
        }

        _searchDebounceTimer?.Start();
    }

    private async void SearchDebounce_Tick(object? sender, EventArgs e)
    {
        _searchDebounceTimer?.Stop();
        var q = _pendingSearchQuery;
        if (q.Length < 2)
            return;

        try
        {
            var items = await App.Api.ProductsSearchAsync(q, App.Settings.Catalog.SearchLimit).ConfigureAwait(true);
            var apiBase = App.Settings.ApiBaseUrl;
            _searchTiles.Clear();
            foreach (var el in items)
            {
                var vm = ProductCatalogMapper.TryTile(el, apiBase);
                if (vm != null)
                    _searchTiles.Add(vm);
            }

            SearchOverlayTitle.Text = $"Поиск «{q}» — {_searchTiles.Count} шт.";
            SearchOverlayPanel.Visibility = Visibility.Visible;
            _ = HydrateThumbsAsync(_searchTiles.ToList());
        }
        catch (ApiException ex)
        {
            ShowToast($"Поиск: {ex.Message}", warn: true);
        }
        catch (HttpRequestException ex)
        {
            ShowToast(string.IsNullOrWhiteSpace(ex.Message) ? "Поиск: нет сети." : ex.Message, warn: true);
        }
    }

    private async void RefreshCatalog_Click(object sender, RoutedEventArgs e) => await LoadCatalogAsync().ConfigureAwait(true);

    private void CatalogTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        /* списки привязаны к разным ItemsControl */
    }

    private async void CatalogProduct_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not CatalogProductTileVm vm)
            return;
        await PickProductFromCatalogAsync(vm).ConfigureAwait(true);
    }

    private async Task PickProductFromCatalogAsync(CatalogProductTileVm vm)
    {
        if (!App.Cart.CanRefresh)
        {
            ShowToast("Сначала нажмите «Начать продажу».", warn: true);
            return;
        }

        string? qty = null;
        if (vm.MustWeigh)
        {
            var dlg = new WeighedProductDialog(vm.Title, vm.PriceLine, _scaleService) { Owner = this };
            if (dlg.ShowDialog() != true || string.IsNullOrEmpty(dlg.QuantityNormalized))
                return;
            qty = dlg.QuantityNormalized;
        }

        SetScanBusy(true);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var resp = await App.Api.PosAddItemAsync(App.Cart.CartId!, vm.Id, qty).ConfigureAwait(true);
                    if (!CartResponseHelper.TryUpdateCartSession(resp, App.Cart))
                        await ReloadCartFromServerAsync().ConfigureAwait(true);
                    RebindCartUi();
                    CartMessageText.Text = "Товар добавлен.";
                    CartMessageText.Foreground = BrushOk;
                    CatalogSearchBox.Text = "";
                    SearchOverlayPanel.Visibility = Visibility.Collapsed;
                    ShowToast(vm.MustWeigh ? $"Добавлено {qty} кг" : "Товар добавлен в чек");
                    return;
                }
                catch (ApiException ex) when (attempt == 0 && CartResponseHelper.LooksLikeStaleCart(ex))
                {
                    try
                    {
                        await TryStartNewSaleAsync().ConfigureAwait(true);
                        RebindCartUi();
                        ShowToast("Корзина устарела — новая продажа, повторяем добавление.", warn: true);
                    }
                    catch (ApiException rex)
                    {
                        CartMessageText.Text = rex.Message;
                        CartMessageText.Foreground = BrushWarn;
                        return;
                    }
                }
            }

            ShowToast("Не удалось добавить товар после повтора.", warn: true);
        }
        catch (ApiException ex)
        {
            CartMessageText.Text = ex.Message;
            CartMessageText.Foreground = BrushWarn;
            ShowToast(ex.Message, warn: true);
        }
        catch (HttpRequestException ex)
        {
            var m = string.IsNullOrWhiteSpace(ex.Message) ? "Нет соединения с сервером." : ex.Message;
            CartMessageText.Text = m;
            CartMessageText.Foreground = BrushWarn;
            ShowToast(m, warn: true);
        }
        catch (TaskCanceledException)
        {
            ShowToast("Таймаут запроса.", warn: true);
        }
        finally
        {
            SetScanBusy(false);
        }
    }

    private void ShowToast(string message, bool warn = false)
    {
        ToastText.Text = message;
        ToastPanel.Background = warn
            ? new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09))
            : new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37));
        ToastPanel.Visibility = Visibility.Visible;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.4) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastPanel.Visibility = Visibility.Collapsed;
        };
        _toastTimer.Start();
    }

    private async void ApplyOrderDiscount_Click(object sender, RoutedEventArgs e)
    {
        if (!App.Cart.CanRefresh)
        {
            ShowToast("Нет активной корзины.", warn: true);
            return;
        }

        var pct = (OrderDiscountPctBox.Text ?? "").Trim();
        var sm = (OrderDiscountSumBox.Text ?? "").Trim();
        var pctActive = pct.Length > 0 && !OrderDiscountHelper.IsEmptyOrZeroLike(pct);
        var smActive = sm.Length > 0 && !OrderDiscountHelper.IsEmptyOrZeroLike(sm);
        if (pctActive && smActive)
        {
            ShowToast("Укажите только процент или только сумму скидки.", warn: true);
            return;
        }

        Dictionary<string, string> body;
        if (pctActive)
        {
            var err = OrderDiscountHelper.ValidatePercent(pct);
            if (err != null)
            {
                ShowToast(err, warn: true);
                return;
            }

            body = new Dictionary<string, string> { ["order_discount_percent"] = OrderDiscountHelper.NormalizeDecimal(pct) };
        }
        else if (smActive)
        {
            var err = OrderDiscountHelper.ValidateSum(sm);
            if (err != null)
            {
                ShowToast(err, warn: true);
                return;
            }

            body = new Dictionary<string, string> { ["order_discount_total"] = OrderDiscountHelper.NormalizeDecimal(sm) };
        }
        else
        {
            ShowToast("Введите % или сумму скидки (не ноль).", warn: true);
            return;
        }

        SetScanBusy(true);
        try
        {
            var resp = await App.Api.PosCartPatchAsync(App.Cart.CartId!, body).ConfigureAwait(true);
            if (!CartResponseHelper.TryUpdateCartSession(resp, App.Cart))
                await ReloadCartFromServerAsync().ConfigureAwait(true);
            RebindCartUi();
            ShowToast("Скидка на чек обновлена.");
        }
        catch (ApiException ex)
        {
            ShowToast(ex.Message, warn: true);
        }
        catch (HttpRequestException ex)
        {
            ShowToast(string.IsNullOrWhiteSpace(ex.Message) ? "Нет сети." : ex.Message, warn: true);
        }
        finally
        {
            SetScanBusy(false);
        }
    }

    private async void ClearOrderDiscount_Click(object sender, RoutedEventArgs e)
    {
        if (!App.Cart.CanRefresh)
            return;

        SetScanBusy(true);
        try
        {
            try
            {
                var resp = await App.Api
                    .PosCartPatchAsync(
                        App.Cart.CartId!,
                        new Dictionary<string, string>
                        {
                            ["order_discount_percent"] = "0",
                            ["order_discount_total"] = "0",
                        })
                    .ConfigureAwait(true);
                if (!CartResponseHelper.TryUpdateCartSession(resp, App.Cart))
                    await ReloadCartFromServerAsync().ConfigureAwait(true);
            }
            catch (ApiException)
            {
                var resp2 = await App.Api
                    .PosCartPatchAsync(App.Cart.CartId!, new Dictionary<string, string> { ["order_discount_percent"] = "0" })
                    .ConfigureAwait(true);
                if (!CartResponseHelper.TryUpdateCartSession(resp2, App.Cart))
                    await ReloadCartFromServerAsync().ConfigureAwait(true);
            }

            RebindCartUi();
            ShowToast("Скидка на чек сброшена.");
        }
        catch (ApiException ex)
        {
            ShowToast(ex.Message, warn: true);
        }
        finally
        {
            SetScanBusy(false);
        }
    }

    private void PrintSelfCheck_Click(object sender, RoutedEventArgs e)
    {
        var cfg = UserPreferences.Instance.ToReceiptPrinterSettings();
        if (string.IsNullOrWhiteSpace(cfg.DevicePath))
        {
            MessageBox.Show(
                "Укажите LPT-порт в «Настройки кассы».",
                "Принтер",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!cfg.Enabled)
        {
            MessageBox.Show(
                "Печать выключена в «Настройки кассы». Включите и укажите LPT.",
                "Принтер",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            EscPosSelfCheckPrinter.PrintSelfCheck(cfg);
            ShowToast("Тестовая страница отправлена на принтер.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Принтер: ошибка.\n\n" + ex.Message + "\n\nПроверьте LPT в настройках и кабель.",
                "Печать",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ScaleTest_Click(object sender, RoutedEventArgs e)
    {
        var sp = UserPreferences.Instance;
        if (!sp.ScaleEnabled)
        {
            MessageBox.Show(
                "Весы выключены. Включите в «Настройки кассы» и укажите COM-порт.",
                "Весы",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var st = _scaleService?.Status ?? "нет сервиса";
        var w = _scaleService?.LastWeight;
        var wtxt = w is > 0 ? $"{w.Value.ToString("0.###", CultureInfo.InvariantCulture)} кг" : "нет стабильного веса";
        MessageBox.Show(
            $"Порт: {sp.ScaleComPort} @ {sp.ScaleBaudRate}\nСтатус опроса: {st}\nТекущий вес: {wtxt}\n\nПоложите товар на платформу и повторите тест.",
            "Тест весов",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OfflineQueueInfo_Click(object sender, RoutedEventArgs e)
    {
        var n = OfflinePendingSalesStore.PendingCount;
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NurMarketKassa",
            "offline_sales_pending.json");
        MessageBox.Show(
            $"Записей в очереди выгрузки на сервер: {n}\n\nФайл данных:\n{path}\n\n(Синхронизация с API — отдельным шагом позже.)",
            "Оффлайн-продажи",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async void OfflineCheckout_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await OfflineCheckout_ClickCoreAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Оффлайн оплата", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task OfflineCheckout_ClickCoreAsync()
    {
        if (App.Api is null)
        {
            MessageBox.Show("API не инициализирован.", "Оффлайн оплата", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (!App.Cart.HasCart || !App.Cart.CanRefresh || CartLines.Count == 0)
        {
            MessageBox.Show("Добавьте товары в корзину.", "Оффлайн оплата", MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var total = CartDisplayHelper.TotalDue(App.Cart.Root);
        var dlg = new CheckoutDialog(total) { Owner = this };
        if (dlg.ShowDialog() != true)
            return;

        var paymentMethod = dlg.PaymentMethodKey;
        var wantPrintReceipt = dlg.WantPrintReceipt;
        var cashReceived = dlg.CashReceivedForApi;
        var snap = App.Cart.Root.GetRawText();

        OfflinePendingSalesStore.Append(
            new OfflineSaleEntry
            {
                PaymentMethod = paymentMethod ?? "",
                CashReceived = cashReceived,
                CartJson = snap,
            });

        var cfg = UserPreferences.Instance.ToReceiptPrinterSettings();
        if (wantPrintReceipt && UserPreferences.Instance.ReceiptEnabled && cfg.Enabled &&
            !string.IsNullOrWhiteSpace(cfg.DevicePath))
        {
            try
            {
                var txt = CartReceiptTextBuilder.BuildSimpleReceipt(snap, "ОФФЛАЙН (ожидает выгрузку)");
                EscPosTextReceiptPrinter.Print(cfg, txt);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Чек не напечатан: " + ex.Message, "Печать", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        var saleRestartErr = await TryRestartSaleSessionAfterCheckoutAsync().ConfigureAwait(true);
        if (saleRestartErr != null)
        {
            CartMessageText.Text = "Оффлайн чек сохранён. " + saleRestartErr;
            CartMessageText.Foreground = BrushWarn;
        }
        else
        {
            CartMessageText.Text = "Оффлайн чек сохранён. Продажа начата — добавьте товары.";
            CartMessageText.Foreground = BrushOk;
        }

        MessageBox.Show(
            "Оплата записана локально (очередь для выгрузки на сервер).\n\n" +
            $"В очереди сейчас: {OfflinePendingSalesStore.PendingCount} чек(ов).",
            "Оффлайн оплата",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void DeferCart_Click(object sender, RoutedEventArgs e)
    {
        if (!App.Cart.HasCart || !App.Cart.CanRefresh || CartLines.Count == 0)
        {
            ShowToast("Корзина пуста — нечего откладывать.", warn: true);
            return;
        }

        var def = $"Чек {DateTime.Now:dd.MM HH:mm}";
        var label = UiPrompts.PromptString(this, "Отложить покупку", "Название (для списка):", def);
        if (string.IsNullOrWhiteSpace(label))
            return;

        DeferredCartsStore.Add(
            new DeferredCartEntry
            {
                Label = label.Trim(),
                CartJson = App.Cart.Root.GetRawText(),
            });

        ShowToast($"Отложено: «{label.Trim()}».");
        _ = ClearCartAfterDeferAsync();
    }

    private async Task ClearCartAfterDeferAsync()
    {
        SetScanBusy(true);
        try
        {
            await TryStartNewSaleAsync().ConfigureAwait(true);
            RebindCartUi();
            CartMessageText.Text = "Продажа начата — добавьте товары.";
            CartMessageText.Foreground = BrushOk;
        }
        catch (Exception ex)
        {
            CartMessageText.Text = "Отложено. Начните продажу вручную: " + ex.Message;
            CartMessageText.Foreground = BrushWarn;
            App.Cart.Clear();
            RebindCartUi();
        }
        finally
        {
            SetScanBusy(false);
        }
    }

    private async void OpenDeferredCarts_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new DeferredCartsDialog { Owner = this };
        if (dlg.ShowDialog() != true || dlg.EntriesToRestore.Count == 0)
            return;

        SetScanBusy(true);
        try
        {
            await RestoreDeferredCartsAsync(dlg.EntriesToRestore.OrderBy(x => x.SavedAt).ToList())
                .ConfigureAwait(true);
        }
        finally
        {
            SetScanBusy(false);
        }
    }

    private async Task RestoreDeferredCartsAsync(IReadOnlyList<DeferredCartEntry> entries)
    {
        if (App.Api is null || entries.Count == 0)
            return;

        if (!App.Cart.HasCart || !App.Cart.CanRefresh)
        {
            try
            {
                await TryStartNewSaleAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Не удалось открыть новую продажу: " + ex.Message,
                    "Отложенные",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }
        }

        var restoredIds = new List<string>();
        try
        {
            foreach (var hold in entries)
            {
                using var doc = JsonDocument.Parse(
                    string.IsNullOrWhiteSpace(hold.CartJson) ? "{}" : hold.CartJson);
                foreach (var it in CartDisplayHelper.EnumerateItems(doc.RootElement))
                {
                    var pid = CartDisplayHelper.TryProductId(it);
                    if (string.IsNullOrEmpty(pid))
                        continue;
                    var weighed = CartDisplayHelper.LineMustWeigh(it);
                    var qty = CartDisplayHelper.LineQuantity(it);
                    var qtyStr = FormatQuantityForApi(qty, weighed);
                    var up = CartDisplayHelper.UnitPrice(it);
                    var upStr = CartDisplayHelper.FormatMoney(up);
                    var disc = CartDisplayHelper.OptionalDiscountTotalParam(it);
                    var resp = await App.Api
                        .PosAddItemAsync(App.Cart.CartId!, pid, qtyStr, upStr, disc)
                        .ConfigureAwait(true);
                    if (!CartResponseHelper.TryUpdateCartSession(resp, App.Cart))
                        await ReloadCartFromServerAsync().ConfigureAwait(true);
                }

                restoredIds.Add(hold.Id);
            }

            DeferredCartsStore.RemoveIds(restoredIds);
            RebindCartUi();
            ShowToast($"Загружено отложенных корзин: {entries.Count}.");
        }
        catch (ApiException ex)
        {
            MessageBox.Show(ex.Message, "Отложенные", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (HttpRequestException ex)
        {
            MessageBox.Show(
                string.IsNullOrWhiteSpace(ex.Message) ? "Нет сети." : ex.Message,
                "Отложенные",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void SyncDiscountFieldsFromCart()
    {
        if (!App.Cart.HasCart || App.Cart.Root.ValueKind != JsonValueKind.Object)
        {
            OrderDiscountPctBox.Text = "";
            OrderDiscountSumBox.Text = "";
            return;
        }

        var c = App.Cart.Root;
        OrderDiscountPctBox.Text = c.TryGetProperty("order_discount_percent", out var p) ? FormatDiscountScalar(p) : "";
        OrderDiscountSumBox.Text = c.TryGetProperty("order_discount_total", out var t) ? FormatDiscountMoney(t) : "";
    }

    private static string FormatDiscountScalar(JsonElement v)
    {
        var s = v.ValueKind switch
        {
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.String => v.GetString() ?? "",
            _ => "",
        };
        if (s.Length > 0 && OrderDiscountHelper.IsEmptyOrZeroLike(s))
            return "";
        return s;
    }

    private static string FormatDiscountMoney(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d))
            return Math.Abs(d) < 1e-9 ? "" : d.ToString("0.00", CultureInfo.InvariantCulture);
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString() ?? "";
            return OrderDiscountHelper.IsEmptyOrZeroLike(s) ? "" : s;
        }

        return "";
    }

    private async Task RefreshShiftStateAsync()
    {
        try
        {
            var list = await App.Api.ConstructionShiftsListAsync().ConfigureAwait(true);
            var openId = ShiftHelper.PickOpenShiftId(list, App.PosCashboxId);
            if (!string.IsNullOrEmpty(openId))
                App.ActiveShiftId = openId;
        }
        catch
        {
            /* список смен может быть недоступен — не сбрасываем уже известный id */
        }

        UpdateShiftBanner();
    }

    private void UpdateShiftBanner()
    {
        if (!string.IsNullOrEmpty(App.ActiveShiftId))
        {
            ShiftBannerBar.Background = ShiftOpenBg;
            ShiftBannerBar.BorderBrush = ShiftOpenBorder;
            ShiftStatusText.Foreground = ShiftOpenText;
            var desk = App.PosCashboxDisplayName ?? App.PosCashboxId ?? "—";
            ShiftStatusText.Text = $"Смена открыта. Касса: {desk}.";
            CloseShiftButton.IsEnabled = true;
        }
        else
        {
            ShiftBannerBar.Background = ShiftWarnBg;
            ShiftBannerBar.BorderBrush = ShiftWarnBorder;
            ShiftStatusText.Foreground = ShiftWarnText;
            ShiftStatusText.Text =
                "Смена не открыта на этой кассе — нажмите «Открыть смену» (иначе «Начать продажу» может вернуть ошибку).";
            CloseShiftButton.IsEnabled = false;
        }
    }

    private void SyncShiftFromCartIfAny()
    {
        if (!App.Cart.HasCart)
            return;
        var sid = CartDisplayHelper.TryShiftIdFromCart(App.Cart.Root);
        if (!string.IsNullOrEmpty(sid))
            App.ActiveShiftId = sid;
    }

    private async void OpenShift_Click(object sender, RoutedEventArgs e)
    {
        OpenShiftButton.IsEnabled = false;
        CloseShiftButton.IsEnabled = false;
        try
        {
            var cb = await EnsurePosCashboxIdAsync().ConfigureAwait(true);
            if (string.IsNullOrEmpty(cb))
            {
                MessageBox.Show(
                    "Не удалось определить кассу (список касс пуст или недоступен).",
                    "Смена",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var dlg = new OpenShiftDialog { Owner = this };
            if (dlg.ShowDialog() != true)
                return;

            var opening = string.IsNullOrWhiteSpace(dlg.OpeningCash) ? "0.00" : dlg.OpeningCash;
            var resp = await App.Api.ConstructionShiftOpenAsync(cb, opening).ConfigureAwait(true);
            var sid = CartDisplayHelper.TryShiftIdFromOpenResponse(resp);
            if (!string.IsNullOrEmpty(sid))
                App.ActiveShiftId = sid;
            else
                await RefreshShiftStateAsync().ConfigureAwait(true);

            UpdateShiftBanner();
            MessageBox.Show("Смена открыта.", "Смена", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (ApiException ex)
        {
            MessageBox.Show(ex.Message, "Смена", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (HttpRequestException ex)
        {
            MessageBox.Show(
                string.IsNullOrWhiteSpace(ex.Message) ? "Нет соединения с сервером." : ex.Message,
                "Смена",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (TaskCanceledException)
        {
            MessageBox.Show("Таймаут запроса.", "Смена", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            OpenShiftButton.IsEnabled = true;
            CloseShiftButton.IsEnabled = !string.IsNullOrEmpty(App.ActiveShiftId);
        }
    }

    private async void CloseShift_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(App.ActiveShiftId))
            return;

        var dlg = new CloseShiftDialog { Owner = this };
        if (dlg.ShowDialog() != true)
            return;

        OpenShiftButton.IsEnabled = false;
        CloseShiftButton.IsEnabled = false;
        try
        {
            await App.Api.ConstructionShiftCloseAsync(App.ActiveShiftId, dlg.ClosingCashOrNull).ConfigureAwait(true);
            App.ActiveShiftId = null;
            await RefreshShiftStateAsync().ConfigureAwait(true);
            MessageBox.Show("Смена закрыта.", "Смена", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (ApiException ex)
        {
            MessageBox.Show(ex.Message, "Смена", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (HttpRequestException ex)
        {
            MessageBox.Show(
                string.IsNullOrWhiteSpace(ex.Message) ? "Нет соединения с сервером." : ex.Message,
                "Смена",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (TaskCanceledException)
        {
            MessageBox.Show("Таймаут запроса.", "Смена", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            OpenShiftButton.IsEnabled = true;
            CloseShiftButton.IsEnabled = !string.IsNullOrEmpty(App.ActiveShiftId);
            UpdateShiftBanner();
        }
    }

    private async Task<string?> EnsurePosCashboxIdAsync()
    {
        var cb = App.PosCashboxId;
        if (!string.IsNullOrWhiteSpace(cb))
            return cb;
        var rawList = await App.Api.ConstructionCashboxesListAsync().ConfigureAwait(true);
        if (CartDisplayHelper.TryFirstCashbox(rawList, out var id, out var displayName))
        {
            cb = id;
            App.PosCashboxId = id;
            App.PosCashboxDisplayName = displayName;
        }

        return cb;
    }

    private async void StartSale_Click(object sender, RoutedEventArgs e)
    {
        CartMessageText.Text = "";
        StartSaleButton.IsEnabled = false;
        RefreshCartButton.IsEnabled = false;
        CheckoutButton.IsEnabled = false;
        ScanBarcodeButton.IsEnabled = false;
        BarcodeBox.IsEnabled = false;
        try
        {
            await TryStartNewSaleAsync().ConfigureAwait(true);
            RebindCartUi();
            CartMessageText.Text = "Продажа начата.";
            CartMessageText.Foreground = BrushOk;
        }
        catch (ApiException ex)
        {
            CartMessageText.Text = ex.Message;
            CartMessageText.Foreground = BrushWarn;
        }
        catch (HttpRequestException ex)
        {
            CartMessageText.Text = string.IsNullOrWhiteSpace(ex.Message) ? "Нет соединения с сервером." : ex.Message;
            CartMessageText.Foreground = BrushWarn;
        }
        catch (TaskCanceledException)
        {
            CartMessageText.Text = "Таймаут запроса.";
            CartMessageText.Foreground = BrushWarn;
        }
        finally
        {
            StartSaleButton.IsEnabled = true;
            RefreshCartButton.IsEnabled = App.Cart.CanRefresh;
            CheckoutButton.IsEnabled = App.Cart.CanRefresh && CartLines.Count > 0;
            ScanBarcodeButton.IsEnabled = App.Cart.CanRefresh;
            BarcodeBox.IsEnabled = App.Cart.CanRefresh;
        }
    }

    private async Task TryStartNewSaleAsync()
    {
        var cb = await EnsurePosCashboxIdAsync().ConfigureAwait(true);
        var cart = await App.Api.PosSalesStartAsync(string.IsNullOrWhiteSpace(cb) ? null : cb).ConfigureAwait(true);
        App.Cart.SetCart(cart);
    }

    /// <summary>После успешной оплаты: сброс локальной корзины и новая продажа на сервере (как «Начать продажу»).</summary>
    /// <returns>null при успехе; иначе краткий текст ошибки для пользователя.</returns>
    private async Task<string?> TryRestartSaleSessionAfterCheckoutAsync()
    {
        App.Cart.Clear();
        try
        {
            await TryStartNewSaleAsync().ConfigureAwait(true);
            RebindCartUi();
            return null;
        }
        catch (ApiException ex)
        {
            RebindCartUi();
            PosLogger.Log($"После оплаты: не удалось начать продажу (API): {ex.Message}", "PAYMENT");
            return ex.Message;
        }
        catch (HttpRequestException ex)
        {
            RebindCartUi();
            var t = string.IsNullOrWhiteSpace(ex.Message) ? "Нет соединения с сервером." : ex.Message;
            PosLogger.Log($"После оплаты: не удалось начать продажу (сеть): {t}", "PAYMENT");
            return t;
        }
        catch (TaskCanceledException)
        {
            RebindCartUi();
            PosLogger.Log("После оплаты: не удалось начать продажу (таймаут).", "PAYMENT");
            return "Таймаут запроса.";
        }
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var m = Keyboard.Modifiers;
        if (m.HasFlag(ModifierKeys.Control) || m.HasFlag(ModifierKeys.Alt) || m.HasFlag(ModifierKeys.Windows))
            return;

        if (Keyboard.FocusedElement is TextBoxBase or PasswordBox)
            return;

        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            if (_barcodeBuf.Length >= MinBarcodeLen)
            {
                e.Handled = true;
                var code = _barcodeBuf;
                _barcodeBuf = "";
                _ = RunScanAsync(code);
            }
            else
                _barcodeBuf = "";

            return;
        }

        var shift = m.HasFlag(ModifierKeys.Shift);
        var ch = KeyToBarcodeChar(e.Key, shift);
        if (ch == null)
            return;

        var now = Environment.TickCount64;
        var delta = now - _barcodeLastTick;
        if (delta < 0 || delta > BarcodeInterkeyMs)
            _barcodeBuf = "";
        _barcodeLastTick = now;

        _barcodeBuf += ch;
        if (_barcodeBuf.Length > BarcodeMaxLen)
            _barcodeBuf = _barcodeBuf.Substring(_barcodeBuf.Length - BarcodeMaxLen);

        e.Handled = true;
    }

    private static string? KeyToBarcodeChar(Key key, bool shift)
    {
        if (key is >= Key.D0 and <= Key.D9)
            return ((char)('0' + (key - Key.D0))).ToString();

        if (key is >= Key.NumPad0 and <= Key.NumPad9)
            return ((char)('0' + (key - Key.NumPad0))).ToString();

        if (key is >= Key.A and <= Key.Z)
        {
            var c = (char)('a' + (key - Key.A));
            if (shift)
                c = char.ToUpperInvariant(c);
            return c.ToString();
        }

        if (key == Key.Space)
            return " ";

        if (key == Key.OemMinus || key == Key.Subtract)
            return "-";

        if (key == Key.OemPeriod || key == Key.Decimal)
            return ".";

        return null;
    }

    /// <summary>null — печать ок; иначе текст предупреждения для пользователя.</summary>
    private async Task<string?> TryPrintReceiptAfterCheckoutAsync(JsonElement checkoutResponse, bool wantPrintReceipt)
    {
        if (!wantPrintReceipt)
            return null;

        PosLogger.Log("Печать чека после оплаты: запрос включён", "PRINTER");

        var cfg = UserPreferences.Instance.ToReceiptPrinterSettings();
        if (!cfg.Enabled)
        {
            PosLogger.Log("Печать: выключена в настройках кассы", "PRINTER");
            return "Печать: выключено в «Настройки кассы». Включите печать и укажите LPT.";
        }

        var txt = CheckoutResponseHelper.TryReceiptTextFromCheckout(checkoutResponse);
        if (string.IsNullOrWhiteSpace(txt))
        {
            var saleId = CheckoutResponseHelper.TrySaleId(checkoutResponse);
            if (!string.IsNullOrEmpty(saleId))
            {
                try
                {
                    PosLogger.Log($"GET receipt для sale_id={saleId}", "PRINTER");
                    var rec = await App.Api.PosSaleReceiptAsync(saleId).ConfigureAwait(true);
                    txt = CheckoutResponseHelper.TryReceiptTextFromSaleReceiptPayload(rec);
                }
                catch (ApiException ex)
                {
                    PosLogger.Log($"GET receipt ApiException: {ex.Message}", "ERROR");
                    return $"Печать: не удалось загрузить чек (GET receipt): {ex.Message}";
                }
                catch (HttpRequestException ex)
                {
                    PosLogger.Log($"GET receipt Http: {ex.Message}", "ERROR");
                    return $"Печать: сеть при загрузке чека: {ex.Message}";
                }
            }
        }

        if (string.IsNullOrWhiteSpace(txt))
        {
            PosLogger.Log("Печать: пустой текст чека после API", "PRINTER");
            return "Печать: нет текста чека; проверьте ответ API (print_receipt) или эндпоинт receipt.";
        }

        try
        {
            PosLogger.Log($"LPT печать: устройство={cfg.DevicePath}, символов текста={txt.Length}", "PRINTER");
            EscPosTextReceiptPrinter.Print(cfg, txt);
            PosLogger.Log("LPT: отправка завершена без исключения", "PRINTER");
            return null;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Печать LPT: {ex.Message}", "ERROR");
            return $"Печать: {ex.Message}";
        }
    }

    private async void Checkout_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Checkout_ClickCoreAsync();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Checkout_Click внешний catch: {ex.Message} | {ex.StackTrace}", "ERROR");
            try
            {
                MessageBox.Show(
                    "Сбой при оплате:\n\n" + ex.Message,
                    "Оплата",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
                /* ignore */
            }
        }
    }

    private async Task Checkout_ClickCoreAsync()
    {
        PosLogger.Log("Начало процесса оплаты", "PAYMENT");

        if (App.Api is null)
        {
            PosLogger.Log("Оплата: Api == null", "ERROR");
            MessageBox.Show("API-клиент не инициализирован.", "Оплата", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (!App.Cart.HasCart || !App.Cart.CanRefresh || CartLines.Count == 0)
        {
            PosLogger.Log("Оплата: нет корзины или пусто", "PAYMENT");
            MessageBox.Show("Добавьте товары в корзину.", "Оплата", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var total = CartDisplayHelper.TotalDue(App.Cart.Root);
        var dlg = new CheckoutDialog(total) { Owner = this };
        if (dlg.ShowDialog() != true)
        {
            PosLogger.Log("Оплата: диалог отменён", "PAYMENT");
            return;
        }

        var paymentMethod = dlg.PaymentMethodKey;
        var wantPrintReceipt = dlg.WantPrintReceipt;
        var cashReceived = dlg.CashReceivedForApi;

        PosLogger.Log(
            $"Диалог оплаты OK: method={paymentMethod}, cash_received={cashReceived}, print_receipt={wantPrintReceipt}, total={total}",
            "PAYMENT");

        var body = new Dictionary<string, string>
        {
            ["payment_method"] = paymentMethod ?? "",
            ["print_receipt"] = wantPrintReceipt ? "true" : "false",
            ["cash_received"] = cashReceived ?? "",
        };

        CartMessageText.Text = "";
        SetScanBusy(true);
        try
        {
            PosLogger.Log($"POST checkout cart_id={App.Cart.CartId}", "PAYMENT");
            var res = await App.Api.PosCheckoutAsync(App.Cart.CartId!, body).ConfigureAwait(true);
            PosLogger.Log("POST checkout: HTTP OK, разбор ответа", "PAYMENT");
            try
            {
                var msg = CheckoutResponseHelper.FormatSuccess(res);
                var printNote = await TryPrintReceiptAfterCheckoutAsync(res, wantPrintReceipt).ConfigureAwait(true);
                if (!string.IsNullOrEmpty(printNote))
                    msg += Environment.NewLine + printNote;
                else if (wantPrintReceipt && UserPreferences.Instance.ReceiptEnabled)
                    msg += Environment.NewLine + "Чек отправлен на принтер.";
                var saleRestartErr = await TryRestartSaleSessionAfterCheckoutAsync().ConfigureAwait(true);
                if (saleRestartErr != null)
                {
                    msg += Environment.NewLine + Environment.NewLine +
                           "Новая продажа не открыта автоматически: " + saleRestartErr + Environment.NewLine +
                           "Нажмите «Начать продажу».";
                    CartMessageText.Text = "Нажмите «Начать продажу», чтобы продолжить.";
                    CartMessageText.Foreground = BrushWarn;
                }
                else
                {
                    CartMessageText.Text = "Продажа начата — добавьте товары.";
                    CartMessageText.Foreground = BrushOk;
                }

                MessageBox.Show(msg, "Оплата", MessageBoxButton.OK, MessageBoxImage.Information);
                PosLogger.Log("Оплата: успешно завершена (новая продажа после чека)", "PAYMENT");
            }
            catch (Exception inner)
            {
                PosLogger.Log(
                    $"Оплата: ошибка после успешного ответа сервера: {inner.Message} | {inner.StackTrace}",
                    "ERROR");
                MessageBox.Show(
                    "Сервер ответил на оплату, но при разборе ответа, печати или обновлении экрана произошла ошибка.\n" +
                    "Если оплата прошла — проверьте продажу в CRM.\n\n" + inner.Message,
                    "Оплата",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                var saleRestartErr = await TryRestartSaleSessionAfterCheckoutAsync().ConfigureAwait(true);
                if (saleRestartErr != null)
                {
                    CartMessageText.Text = "Нажмите «Начать продажу», чтобы продолжить.";
                    CartMessageText.Foreground = BrushWarn;
                }
                else
                {
                    CartMessageText.Text = "Продажа начата — добавьте товары.";
                    CartMessageText.Foreground = BrushOk;
                }
            }

            await RefreshShiftStateAsync().ConfigureAwait(true);
        }
        catch (ApiException ex)
        {
            PosLogger.Log($"Оплата ApiException: {ex.Message}", "ERROR");
            MessageBox.Show(ex.Message, "Оплата", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (HttpRequestException ex)
        {
            PosLogger.Log($"Оплата HttpRequestException: {ex.Message}", "ERROR");
            MessageBox.Show(
                string.IsNullOrWhiteSpace(ex.Message) ? "Нет соединения с сервером." : ex.Message,
                "Оплата",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (JsonException ex)
        {
            PosLogger.Log($"Оплата JsonException: {ex.Message}", "ERROR");
            MessageBox.Show(
                "Сервер вернул ответ, который не удалось разобрать как JSON (оплата могла пройти или нет).\n\n" + ex.Message,
                "Оплата",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (OperationCanceledException)
        {
            PosLogger.Log("Оплата: отмена / таймаут", "ERROR");
            MessageBox.Show("Оплата прервана или истёк таймаут (до 90 с).", "Оплата", MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Оплата Exception: {ex.Message} | {ex.StackTrace}", "ERROR");
            MessageBox.Show(
                "Неожиданная ошибка при оплате:\n\n" + ex.Message,
                "Оплата",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetScanBusy(false);
        }
    }

    private async void RefreshCart_Click(object sender, RoutedEventArgs e)
    {
        if (!App.Cart.CanRefresh)
            return;

        CartMessageText.Text = "";
        RefreshCartButton.IsEnabled = false;
        CheckoutButton.IsEnabled = false;
        ScanBarcodeButton.IsEnabled = false;
        BarcodeBox.IsEnabled = false;
        try
        {
            await ReloadCartFromServerAsync().ConfigureAwait(true);
            RebindCartUi();
            CartMessageText.Text = "Корзина обновлена.";
            CartMessageText.Foreground = BrushOk;
        }
        catch (ApiException ex) when (ex.StatusCode == 404)
        {
            App.Cart.Clear();
            RebindCartUi();
            CartMessageText.Text = "Корзина на сервере не найдена — начните продажу заново.";
            CartMessageText.Foreground = BrushWarn;
        }
        catch (ApiException ex)
        {
            CartMessageText.Text = ex.Message;
            CartMessageText.Foreground = BrushWarn;
        }
        catch (HttpRequestException ex)
        {
            CartMessageText.Text = string.IsNullOrWhiteSpace(ex.Message) ? "Нет соединения с сервером." : ex.Message;
            CartMessageText.Foreground = BrushWarn;
        }
        catch (TaskCanceledException)
        {
            CartMessageText.Text = "Таймаут запроса.";
            CartMessageText.Foreground = BrushWarn;
        }
        finally
        {
            RefreshCartButton.IsEnabled = App.Cart.CanRefresh;
            CheckoutButton.IsEnabled = App.Cart.CanRefresh && CartLines.Count > 0;
            ScanBarcodeButton.IsEnabled = App.Cart.CanRefresh;
            BarcodeBox.IsEnabled = App.Cart.CanRefresh;
        }
    }

    private async Task ReloadCartFromServerAsync()
    {
        if (!App.Cart.CanRefresh)
            return;
        var c = await App.Api.PosCartGetAsync(App.Cart.CartId!).ConfigureAwait(true);
        App.Cart.SetCart(c);
    }

    private void BarcodeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        _ = RunScanAsync(BarcodeBox.Text);
    }

    private async void ScanBarcode_Click(object sender, RoutedEventArgs e) => await RunScanAsync(BarcodeBox.Text);

    private async Task RunScanAsync(string? rawCode)
    {
        var code = (rawCode ?? "").Trim();
        if (code.Length == 0)
            return;

        if (!App.Cart.CanRefresh)
        {
            CartMessageText.Text = "Сначала нажмите «Начать продажу».";
            CartMessageText.Foreground = BrushWarn;
            return;
        }

        CartMessageText.Text = "";
        SetScanBusy(true);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var resp = await App.Api.PosScanAsync(App.Cart.CartId!, code).ConfigureAwait(true);
                    if (!CartResponseHelper.TryUpdateCartSession(resp, App.Cart))
                        await ReloadCartFromServerAsync().ConfigureAwait(true);
                    RebindCartUi();
                    CartMessageText.Text = "Товар добавлен.";
                    CartMessageText.Foreground = BrushOk;
                    BarcodeBox.Text = "";
                    BarcodeBox.Focus();
                    return;
                }
                catch (ApiException ex) when (attempt == 0 && CartResponseHelper.LooksLikeStaleCart(ex))
                {
                    try
                    {
                        await TryStartNewSaleAsync().ConfigureAwait(true);
                        RebindCartUi();
                        CartMessageText.Text = "Корзина устарела — открыта новая продажа, повторяем скан.";
                        CartMessageText.Foreground = BrushOk;
                    }
                    catch (ApiException rex)
                    {
                        CartMessageText.Text = rex.Message;
                        CartMessageText.Foreground = BrushWarn;
                        return;
                    }
                }
            }

            CartMessageText.Text = "Не удалось отсканировать после повтора.";
            CartMessageText.Foreground = BrushWarn;
        }
        catch (ApiException ex)
        {
            CartMessageText.Text = ex.Message;
            CartMessageText.Foreground = BrushWarn;
        }
        catch (HttpRequestException ex)
        {
            CartMessageText.Text = string.IsNullOrWhiteSpace(ex.Message) ? "Нет соединения с сервером." : ex.Message;
            CartMessageText.Foreground = BrushWarn;
        }
        catch (TaskCanceledException)
        {
            CartMessageText.Text = "Скан: таймаут (проверьте сеть).";
            CartMessageText.Foreground = BrushWarn;
        }
        finally
        {
            SetScanBusy(false);
        }
    }

    private void SetScanBusy(bool busy)
    {
        var can = App.Cart.CanRefresh;
        var hasLines = CartLines?.Count > 0;
        if (ScanBarcodeButton != null)
            ScanBarcodeButton.IsEnabled = !busy && can;
        if (BarcodeBox != null)
            BarcodeBox.IsEnabled = !busy && can;
        if (StartSaleButton != null)
            StartSaleButton.IsEnabled = !busy;
        if (RefreshCartButton != null)
            RefreshCartButton.IsEnabled = !busy && can;
        if (CheckoutButton != null)
            CheckoutButton.IsEnabled = !busy && can && hasLines == true;
    }

    private async void CartQtyMinus_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not CartLineRow row)
            return;
        await AdjustLineQtyAsync(row, -1).ConfigureAwait(true);
    }

    private async void CartQtyPlus_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not CartLineRow row)
            return;
        await AdjustLineQtyAsync(row, 1).ConfigureAwait(true);
    }

    private async void CartLineDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not CartLineRow row)
            return;
        if (!App.Cart.CanRefresh || string.IsNullOrEmpty(row.ItemId))
            return;
        await DeleteLineAsync(row.ItemId).ConfigureAwait(true);
    }

    private async void CartWeigh_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not CartLineRow row || !row.WeighedLine)
            return;
        if (!App.Cart.CanRefresh || string.IsNullOrEmpty(row.ItemId))
            return;

        var initial = FormatQtySubline(row.Qty, true);
        var dlg = new WeighedProductDialog(
            row.Title,
            row.PricePerKgHint,
            _scaleService,
            initialKg: initial,
            okButtonText: "Применить",
            windowTitle: "Изменить вес") { Owner = this };

        if (dlg.ShowDialog() != true || string.IsNullOrEmpty(dlg.QuantityNormalized))
            return;

        SetScanBusy(true);
        try
        {
            var resp = await App.Api
                .PosCartItemPatchAsync(
                    App.Cart.CartId!,
                    row.ItemId,
                    new Dictionary<string, string> { ["quantity"] = dlg.QuantityNormalized })
                .ConfigureAwait(true);
            if (!CartResponseHelper.TryUpdateCartSession(resp, App.Cart))
                await ReloadCartFromServerAsync().ConfigureAwait(true);
            CartMessageText.Text = "";
            RebindCartUi();
        }
        catch (ApiException ex)
        {
            CartMessageText.Text = ex.Message;
            CartMessageText.Foreground = BrushWarn;
        }
        catch (HttpRequestException ex)
        {
            CartMessageText.Text = string.IsNullOrWhiteSpace(ex.Message) ? "Нет соединения с сервером." : ex.Message;
            CartMessageText.Foreground = BrushWarn;
        }
        catch (TaskCanceledException)
        {
            CartMessageText.Text = "Таймаут запроса.";
            CartMessageText.Foreground = BrushWarn;
        }
        finally
        {
            SetScanBusy(false);
        }
    }

    private async Task AdjustLineQtyAsync(CartLineRow row, int direction)
    {
        if (!App.Cart.CanRefresh || string.IsNullOrEmpty(row.ItemId))
            return;

        var step = row.WeighedLine ? 0.1 : 1.0;
        var q = Math.Round(row.Qty + direction * step, 4);
        if (q <= 0)
        {
            await DeleteLineAsync(row.ItemId).ConfigureAwait(true);
            return;
        }

        var qtyStr = FormatQuantityForApi(q, row.WeighedLine);
        SetScanBusy(true);
        try
        {
            var resp = await App.Api
                .PosCartItemPatchAsync(
                    App.Cart.CartId!,
                    row.ItemId,
                    new Dictionary<string, string> { ["quantity"] = qtyStr })
                .ConfigureAwait(true);
            if (!CartResponseHelper.TryUpdateCartSession(resp, App.Cart))
                await ReloadCartFromServerAsync().ConfigureAwait(true);
            CartMessageText.Text = "";
            RebindCartUi();
        }
        catch (ApiException ex)
        {
            CartMessageText.Text = ex.Message;
            CartMessageText.Foreground = BrushWarn;
        }
        catch (HttpRequestException ex)
        {
            CartMessageText.Text = string.IsNullOrWhiteSpace(ex.Message) ? "Нет соединения с сервером." : ex.Message;
            CartMessageText.Foreground = BrushWarn;
        }
        catch (TaskCanceledException)
        {
            CartMessageText.Text = "Таймаут запроса.";
            CartMessageText.Foreground = BrushWarn;
        }
        finally
        {
            SetScanBusy(false);
        }
    }

    private async Task DeleteLineAsync(string itemId)
    {
        SetScanBusy(true);
        try
        {
            await App.Api.PosCartItemDeleteAsync(App.Cart.CartId!, itemId).ConfigureAwait(true);
            await ReloadCartFromServerAsync().ConfigureAwait(true);
            CartMessageText.Text = "";
            RebindCartUi();
        }
        catch (ApiException ex)
        {
            CartMessageText.Text = ex.Message;
            CartMessageText.Foreground = BrushWarn;
        }
        catch (HttpRequestException ex)
        {
            CartMessageText.Text = string.IsNullOrWhiteSpace(ex.Message) ? "Нет соединения с сервером." : ex.Message;
            CartMessageText.Foreground = BrushWarn;
        }
        finally
        {
            SetScanBusy(false);
        }
    }

    private static string FormatQuantityForApi(double q, bool weighed)
    {
        if (weighed)
        {
            var s = q.ToString("0.####", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
            return string.IsNullOrEmpty(s) ? "0" : s;
        }

        return Math.Round(q, 0).ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatQtySubline(double qty, bool weighed)
    {
        if (weighed)
        {
            var s = qty.ToString("0.###", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
            return string.IsNullOrEmpty(s) ? "0" : s;
        }

        return Math.Round(qty, 0).ToString(CultureInfo.InvariantCulture);
    }

    private void RebindCartUi()
    {
        CartLines.Clear();
        if (!App.Cart.HasCart)
        {
            CartTotalText.Text = "";
            RefreshCartButton.IsEnabled = false;
            CheckoutButton.IsEnabled = false;
            ScanBarcodeButton.IsEnabled = false;
            BarcodeBox.IsEnabled = false;
            SyncDiscountFieldsFromCart();
            UpdateShiftBanner();
            return;
        }

        var root = App.Cart.Root;
        foreach (var it in CartDisplayHelper.EnumerateItems(root))
        {
            var iid = CartDisplayHelper.TryItemId(it);
            if (string.IsNullOrEmpty(iid))
                continue;

            var weighed = CartDisplayHelper.LineMustWeigh(it);
            var qtyVal = CartDisplayHelper.LineQuantity(it);
            var up = CartDisplayHelper.UnitPrice(it);
            var unit = weighed ? "кг" : "шт";
            var sub = $"{FormatQtySubline(qtyVal, weighed)} {unit} × {CartDisplayHelper.FormatMoney(up)} сом";
            var priceKg = weighed ? $"{CartDisplayHelper.FormatMoney(up)} сом" : "";

            CartLines.Add(new CartLineRow
            {
                ItemId = iid,
                Qty = qtyVal,
                WeighedLine = weighed,
                Title = CartDisplayHelper.ItemName(it),
                SubLine = sub,
                LineTotal = CartDisplayHelper.LineTotal(it),
                PricePerKgHint = priceKg,
            });
        }

        var total = CartDisplayHelper.TotalDue(root);
        CartTotalText.Text = $"Итого: {CartDisplayHelper.FormatMoney(total)} сом";
        RefreshCartButton.IsEnabled = App.Cart.CanRefresh;
        CheckoutButton.IsEnabled = App.Cart.CanRefresh && CartLines.Count > 0;
        ScanBarcodeButton.IsEnabled = App.Cart.CanRefresh;
        BarcodeBox.IsEnabled = App.Cart.CanRefresh;
        SyncShiftFromCartIfAny();
        SyncDiscountFieldsFromCart();
        UpdateShiftBanner();
    }

    public void ApplyHardwareAndUiPreferences()
    {
        ApplyFullscreenPreference();
        _scaleService?.Dispose();
        _scaleService = new ScaleReaderService(UserPreferences.Instance.ToScaleSettings());
        _scaleService.Start();
        UpdateScaleStatusLine();
    }

    private void ApplyFullscreenPreference()
    {
        if (UserPreferences.Instance.Fullscreen)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = WindowState.Normal;
            if (Width < 400)
                Width = 1280;
            if (Height < 300)
                Height = 840;
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new PosSettingsWindow { Owner = this };
        dlg.ShowDialog();
    }

    private static string FormatBranchLine(string? branchId) =>
        string.IsNullOrEmpty(branchId)
            ? "Филиал не выбран"
            : "Филиал подключён (запросы к API с branch)";

    private static string? TryBranchId(JsonElement user)
    {
        if (user.ValueKind != JsonValueKind.Object)
            return null;

        if (user.TryGetProperty("primary_branch_id", out var pb) && pb.ValueKind == JsonValueKind.String)
        {
            var s = pb.GetString();
            if (!string.IsNullOrEmpty(s))
                return s;
        }

        if (user.TryGetProperty("branch_ids", out var bids) && bids.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in bids.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (!string.IsNullOrEmpty(s))
                        return s;
                }
            }
        }

        return null;
    }

    private static string TryUserLabel(JsonElement user)
    {
        if (user.ValueKind != JsonValueKind.Object)
            return "Пользователь";

        foreach (var key in new[] { "full_name", "name", "email", "username" })
        {
            if (user.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String)
            {
                var t = p.GetString();
                if (!string.IsNullOrWhiteSpace(t))
                    return t!;
            }
        }

        return "Пользователь";
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        App.Api.ClearSession();
        App.Cart.Clear();
        App.PosCashboxId = null;
        App.PosCashboxDisplayName = null;
        App.ActiveShiftId = null;
        var login = new LoginWindow();
        Application.Current.MainWindow = login;
        login.Show();
        Close();
    }
}
