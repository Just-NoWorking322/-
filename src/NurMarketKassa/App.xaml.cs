using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NurMarketKassa.Configuration;
using NurMarketKassa.Services;

namespace NurMarketKassa;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = null!;
    public static NurMarketApiClient Api { get; private set; } = null!;
    public static CartSession Cart { get; } = new();
    public static string? PosCashboxId { get; set; }

    /// <summary>Человекочитаемое имя кассы из списка касс API (не UUID).</summary>
    public static string? PosCashboxDisplayName { get; set; }

    public static string? ActiveShiftId { get; set; }

    private HttpClient? _http;

    static App()
    {
        EventManager.RegisterClassHandler(
            typeof(UIElement),
            UIElement.PreviewGotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(TouchKeyboard.OnPreviewKeyboardFocus),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(TextBox),
            UIElement.PreviewTouchDownEvent,
            new EventHandler<TouchEventArgs>(TouchKeyboard.OnTextInputPreviewTouchDown),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(PasswordBox),
            UIElement.PreviewTouchDownEvent,
            new EventHandler<TouchEventArgs>(TouchKeyboard.OnTextInputPreviewTouchDown),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(TextBox),
            Mouse.PreviewMouseDownEvent,
            new MouseButtonEventHandler(TouchKeyboard.OnTextInputPreviewMouseDown),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(PasswordBox),
            Mouse.PreviewMouseDownEvent,
            new MouseButtonEventHandler(TouchKeyboard.OnTextInputPreviewMouseDown),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(TextBox),
            Stylus.PreviewStylusDownEvent,
            new StylusDownEventHandler(TouchKeyboard.OnTextInputPreviewStylusDown),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(PasswordBox),
            Stylus.PreviewStylusDownEvent,
            new StylusDownEventHandler(TouchKeyboard.OnTextInputPreviewStylusDown),
            handledEventsToo: true);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            PosLogger.Log(
                $"DispatcherUnhandledException: {args.Exception.GetType().FullName}: {args.Exception.Message} | {args.Exception.StackTrace}",
                "ERROR");
            try
            {
                MessageBox.Show(
                    "Ошибка интерфейса:\n\n" + args.Exception.Message + "\n\n" + args.Exception.GetType().FullName,
                    "Nur Market — Касса",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
                /* ignore */
            }

            args.Handled = true;
        };

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Settings = AppSettings.Load();
        UserPreferences.LoadFromDiskAndMergeDefaults(Settings);
        AutostartHelper.SyncFromPreference(UserPreferences.Instance.Autostart);
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(55) };
        Api = new NurMarketApiClient(_http, Settings);

        EventManager.RegisterClassHandler(typeof(TextBox), UIElement.GotFocusEvent, new RoutedEventHandler(OnInputFocused),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(PasswordBox), UIElement.GotFocusEvent, new RoutedEventHandler(OnInputFocused),
            handledEventsToo: true);

        base.OnStartup(e);
        new Views.LoginWindow().Show();
    }

    /// <summary>Автозапуск osk.exe при фокусе в поле (в фоне, без блокировки UI).</summary>
    private void OnInputFocused(object sender, RoutedEventArgs e)
    {
        var kind = sender?.GetType().Name ?? "?";
        _ = Task.Run(() =>
        {
            var useTabTip = Environment.GetEnvironmentVariable("DESKTOP_MARKET_USE_TABTIP")?.Trim()
                is "1" or "true" or "yes" or "on";
            if (useTabTip)
                return;

            Process[]? list = null;
            try
            {
                list = Process.GetProcessesByName("osk");
                if (list.Length > 0)
                {
                    TouchKeyboard.ScheduleClassicOskResizeSoon();
                    return;
                }

                var osk = Path.Combine(Environment.SystemDirectory, "osk.exe");
                if (!File.Exists(osk))
                {
                    PosLogger.Log($"OSK не найден: {osk}", "OSK");
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = osk,
                    UseShellExecute = true,
                    WorkingDirectory = Environment.SystemDirectory,
                });
                TouchKeyboard.ScheduleClassicOskResizeSoon();
                PosLogger.Log($"Запущен osk.exe (GotFocus), элемент: {kind}", "OSK");
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Ошибка запуска OSK: {ex.Message}", "ERROR");
            }
            finally
            {
                if (list is not null)
                {
                    foreach (var p in list)
                        p.Dispose();
                }
            }
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Cart.Dispose();
        Api.Dispose();
        _http?.Dispose();
        base.OnExit(e);
    }

}
