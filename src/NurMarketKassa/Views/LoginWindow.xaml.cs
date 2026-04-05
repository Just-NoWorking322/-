using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using NurMarketKassa.Services;

namespace NurMarketKassa.Views;

public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
        ApiUrlText.Text = FormatApiHostForDisplay(App.Settings.ApiBaseUrl);
        var prefs = UserPreferences.Instance;
        EmailBox.Text = prefs.LastLoginEmail;
        if (!string.IsNullOrEmpty(prefs.LastLoginPassword))
            PasswordBox.Password = prefs.LastLoginPassword;
        Loaded += (_, _) =>
        {
            if (!UserPreferences.Instance.Fullscreen)
                return;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        };
    }

    private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            _ = TryLoginAsync();
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e) => await TryLoginAsync();

    private async Task TryLoginAsync()
    {
        ErrorText.Visibility = Visibility.Collapsed;
        ErrorText.Text = "";

        var email = EmailBox.Text.Trim();
        var password = PasswordBox.Password;
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
        {
            ShowError("Введите email и пароль.");
            return;
        }

        LoginButton.IsEnabled = false;
        try
        {
            await App.Api.LoginAsync(email, password);
            var up = UserPreferences.Instance;
            up.LastLoginEmail = email;
            up.LastLoginPassword = password;
            up.SaveToDisk();
            var main = new MainWindow();
            Application.Current.MainWindow = main;
            main.Show();
            Close();
        }
        catch (ApiException ex)
        {
            ShowError(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            ShowError(string.IsNullOrWhiteSpace(ex.Message) ? "Нет соединения с сервером." : ex.Message);
        }
        catch (TaskCanceledException)
        {
            ShowError("Превышено время ожидания ответа сервера.");
        }
        finally
        {
            LoginButton.IsEnabled = true;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private static string FormatApiHostForDisplay(string? baseUrl)
    {
        var u = (baseUrl ?? "").Trim();
        if (u.Length == 0)
            return "";
        if (u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return u[8..].TrimEnd('/');
        if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return u[7..].TrimEnd('/');
        return u;
    }
}
