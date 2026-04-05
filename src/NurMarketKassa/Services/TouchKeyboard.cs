using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace NurMarketKassa.Services;

/// <summary>
/// Клавиатура Windows: по умолчанию классическая <c>osk.exe</c> (видна на десктопе без переменных среды).
/// Для сенсорной TabTip: <c>DESKTOP_MARKET_USE_TABTIP=1</c>.
/// Масштаб OSK: <c>DESKTOP_MARKET_OSK_SCALE</c> (например 0.75), по умолчанию 0.80.
/// Обрезка окна OSK (правый блок навигации и верхняя пустая полоса): <c>SetWindowRgn</c> —
/// доли <c>DESKTOP_MARKET_OSK_CLIP_TOP</c> / <c>DESKTOP_MARKET_OSK_CLIP_RIGHT</c> (0–0.5), по умолчанию 0.18 и 0.30;
/// отключить: <c>DESKTOP_MARKET_OSK_NO_CLIP=1</c>.
/// </summary>
public static class TouchKeyboard
{
    private static readonly string[] TabTipCandidates = BuildTabTipPaths();
    private static DispatcherTimer? _followUpTimer;
    private static DispatcherTimer? _resizeOskTimer;

    private static string[] BuildTabTipPaths()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var list = new List<string>
        {
            Path.Combine(win, "System32", "TabTip.exe"),
            Path.Combine(Environment.SystemDirectory, "TabTip.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
                "Microsoft Shared", "ink", "TabTip.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                "Microsoft Shared", "ink", "TabTip.exe"),
            @"C:\Program Files\Common Files\Microsoft Shared\ink\TabTip.exe",
            @"C:\Program Files\Common Files\microsoft shared\ink\TabTip.exe",
        };

        var w6432 = Environment.GetEnvironmentVariable("CommonProgramW6432");
        if (!string.IsNullOrEmpty(w6432))
            list.Add(Path.Combine(w6432, "Microsoft Shared", "ink", "TabTip.exe"));

        var env = Environment.GetEnvironmentVariable("DESKTOP_MARKET_TABTIP_EXE")?.Trim();
        if (!string.IsNullOrEmpty(env))
            list.Insert(0, env);

        var oskFirst = Environment.GetEnvironmentVariable("DESKTOP_MARKET_KEYBOARD_OSK_FIRST")?.Trim()
            is "1" or "true" or "yes" or "on";
        if (oskFirst)
        {
            var osk = Path.Combine(Environment.SystemDirectory, "osk.exe");
            if (File.Exists(osk))
                list.Insert(0, osk);
        }

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Показать клавиатуру для текущего <see cref="Keyboard.FocusedElement"/> (после фокуса).</summary>
    public static void TryShow() => EnqueueShow(immediate: true, focusAnchor: null);

    /// <summary>После запуска osk.exe извне — отложенное уменьшение окна (тот же таймер, что и при фокусе).</summary>
    public static void ScheduleClassicOskResizeSoon() => ScheduleResizeClassicOskSoon();

    /// <summary>Клик по полю: сначала фокус, потом клавиатура.</summary>
    public static void FocusAndShow(object sender)
    {
        switch (sender)
        {
            case TextBox tb:
                tb.Focusable = true;
                tb.Focus();
                Keyboard.Focus(tb);
                EnqueueShow(immediate: true, focusAnchor: tb);
                return;
            case PasswordBox pb:
                pb.Focusable = true;
                pb.Focus();
                Keyboard.Focus(pb);
                EnqueueShow(immediate: true, focusAnchor: pb);
                return;
            default:
                return;
        }
    }

    private static void EnqueueShow(bool immediate, IInputElement? focusAnchor)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null)
        {
            if (immediate)
                RunShowPipeline(focusAnchor);
            return;
        }

        var anchor = focusAnchor;
        d.BeginInvoke(() => RunShowPipeline(anchor),
            immediate ? DispatcherPriority.Input : DispatcherPriority.ApplicationIdle);
    }

    private static void RunShowPipeline(IInputElement? focusAnchor)
    {
        TryShowCore(focusAnchor);
        ScheduleFollowUp();
    }

    /// <summary>Только развернуть уже созданное окно TabTip — без повторного запуска TabTip.exe (иначе Windows гасит клавиатуру).</summary>
    private static void ScheduleFollowUp()
    {
        var d = Application.Current?.Dispatcher;
        if (d is null)
            return;

        d.BeginInvoke(
            () =>
            {
                _followUpTimer?.Stop();
                _followUpTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
                _followUpTimer.Tick += (_, _) =>
                {
                    _followUpTimer.Stop();
                    _followUpTimer = null;
                    TryUnhideTipWindow();
                };
                _followUpTimer.Start();
            },
            DispatcherPriority.Background);
    }

    private static void TryShowCore(IInputElement? focusAnchor)
    {
        try
        {
            if (!IsTextInputTarget(focusAnchor) && !IsTextInputTarget(Keyboard.FocusedElement))
                return;

            var hwnd = TryFocusHwnd(focusAnchor);
            TryComToggleTip(hwnd);

            var oskPath = Path.Combine(Environment.SystemDirectory, "osk.exe");
            var useTabTip = Environment.GetEnvironmentVariable("DESKTOP_MARKET_USE_TABTIP")?.Trim()
                is "1" or "true" or "yes" or "on";

            if (File.Exists(oskPath) && !useTabTip)
            {
                TryEnsureClassicOsk();
                return;
            }

            foreach (var p in TabTipCandidates)
            {
                if (string.IsNullOrEmpty(p) || !File.Exists(p))
                    continue;
                if (string.Equals(Path.GetFileName(p), "osk.exe", StringComparison.OrdinalIgnoreCase))
                {
                    TryStartProcess(p);
                    return;
                }

                TryStartProcess(p);
                TryUnhideTipWindow();
                return;
            }

            if (File.Exists(oskPath))
                TryStartProcess(oskPath);
        }
        catch
        {
            /* ignore */
        }
    }

    private static void TryEnsureClassicOsk()
    {
        var oskPath = Path.Combine(Environment.SystemDirectory, "osk.exe");
        if (!File.Exists(oskPath))
            return;
        try
        {
            if (Process.GetProcessesByName("osk").Length > 0)
            {
                ScheduleResizeClassicOskSoon();
                return;
            }
        }
        catch
        {
            /* ignore */
        }

        TryStartProcess(oskPath);
        ScheduleResizeClassicOskSoon();
    }

    private static void ScheduleResizeClassicOskSoon()
    {
        var d = Application.Current?.Dispatcher;
        if (d is null)
            return;
        d.BeginInvoke(
            () =>
            {
                _resizeOskTimer?.Stop();
                _resizeOskTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(480) };
                _resizeOskTimer.Tick += (_, _) =>
                {
                    _resizeOskTimer?.Stop();
                    _resizeOskTimer = null;
                    TryResizeClassicOskWindowIfFound();
                };
                _resizeOskTimer.Start();
            },
            DispatcherPriority.Background);
    }

    private static double GetOskScale()
    {
        var s = Environment.GetEnvironmentVariable("DESKTOP_MARKET_OSK_SCALE")?.Trim();
        if (string.IsNullOrEmpty(s))
            return 0.80;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v is > 0.35 and < 1.0)
            return v;
        return 0.80;
    }

    private static bool IsOskClipDisabled() =>
        Environment.GetEnvironmentVariable("DESKTOP_MARKET_OSK_NO_CLIP")?.Trim()
            is "1" or "true" or "yes" or "on";

    private static double GetOskClipTopFraction()
    {
        var s = Environment.GetEnvironmentVariable("DESKTOP_MARKET_OSK_CLIP_TOP")?.Trim();
        if (string.IsNullOrEmpty(s))
            return 0.18;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v is >= 0 and <= 0.55)
            return v;
        return 0.18;
    }

    private static double GetOskClipRightFraction()
    {
        var s = Environment.GetEnvironmentVariable("DESKTOP_MARKET_OSK_CLIP_RIGHT")?.Trim();
        if (string.IsNullOrEmpty(s))
            return 0.30;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v is >= 0 and <= 0.55)
            return v;
        return 0.30;
    }

    private static void TryResizeClassicOskWindowIfFound()
    {
        try
        {
            var hwnd = FindLargestVisibleOskWindow();
            if (hwnd == IntPtr.Zero)
                return;
            if (IsIconic(hwnd))
                ShowWindow(hwnd, SwRestore);
            if (!GetWindowRect(hwnd, out var r))
                return;
            var w = r.Right - r.Left;
            var h = r.Bottom - r.Top;
            if (w < 80 || h < 80)
                return;
            var scale = GetOskScale();
            var nw = Math.Max(280, (int)(w * scale));
            var nh = Math.Max(140, (int)(h * scale));
            var x = r.Left + (w - nw) / 2;
            var y = r.Bottom - nh;
            SetWindowPos(hwnd, IntPtr.Zero, x, y, nw, nh, SwpNoZOrder | SwpNoActivate);
            TryApplyClassicOskWindowClip(hwnd);
            ScheduleClassicOskClipRetry(hwnd);
        }
        catch
        {
            /* ignore */
        }
    }

    /// <summary>OSK после ресайза иногда перерисовывает раскладку — повторяем обрезку.</summary>
    private static void ScheduleClassicOskClipRetry(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || IsOskClipDisabled())
            return;
        var d = Application.Current?.Dispatcher;
        if (d is null)
            return;
        d.BeginInvoke(
            () =>
            {
                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                t.Tick += (_, _) =>
                {
                    t.Stop();
                    if (IsWindow(hwnd) && IsWindowVisible(hwnd))
                        TryApplyClassicOskWindowClip(hwnd);
                };
                t.Start();
            },
            DispatcherPriority.Background);
    }

    private static void TryApplyClassicOskWindowClip(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero || IsOskClipDisabled())
            {
                if (hwnd != IntPtr.Zero)
                    SetWindowRgn(hwnd, IntPtr.Zero, true);
                return;
            }

            if (!GetWindowRect(hwnd, out var wr) || !GetClientRect(hwnd, out var cr))
                return;
            var clientW = cr.Right - cr.Left;
            var clientH = cr.Bottom - cr.Top;
            if (clientW < 120 || clientH < 80)
                return;

            var pt = new Point32();
            if (!ClientToScreen(hwnd, ref pt))
                return;

            var originX = pt.X - wr.Left;
            var originY = pt.Y - wr.Top;

            var topPx = (int)Math.Round(clientH * GetOskClipTopFraction());
            var rightPx = (int)Math.Round(clientW * GetOskClipRightFraction());
            topPx = Math.Clamp(topPx, 0, Math.Max(0, clientH - 100));
            rightPx = Math.Clamp(rightPx, 0, Math.Max(0, clientW - 220));

            var rgnLeft = originX;
            var rgnTop = originY + topPx;
            var rgnRight = originX + (clientW - rightPx);
            var rgnBottom = originY + clientH;
            if (rgnRight <= rgnLeft + 180 || rgnBottom <= rgnTop + 90)
                return;

            var hrgn = CreateRectRgn(rgnLeft, rgnTop, rgnRight, rgnBottom);
            if (hrgn == IntPtr.Zero)
                return;
            if (SetWindowRgn(hwnd, hrgn, true) == 0)
                DeleteObject(hrgn);
        }
        catch
        {
            /* ignore */
        }
    }

    private static IntPtr FindLargestVisibleOskWindow()
    {
        IntPtr best = IntPtr.Zero;
        var bestArea = 0;
        EnumWindows(
            (IntPtr h, IntPtr lParamUnused) =>
            {
                if (!IsWindowVisible(h))
                    return true;
                GetWindowThreadProcessId(h, out uint pid);
                try
                {
                    using var proc = Process.GetProcessById((int)pid);
                    if (!proc.ProcessName.Equals("osk", StringComparison.OrdinalIgnoreCase))
                        return true;
                    if (!GetWindowRect(h, out var r))
                        return true;
                    var area = Math.Abs((r.Right - r.Left) * (r.Bottom - r.Top));
                    if (area > bestArea)
                    {
                        bestArea = area;
                        best = h;
                    }
                }
                catch
                {
                    /* ignore */
                }

                return true;
            },
            IntPtr.Zero);
        return best;
    }

    private static bool IsTextInputTarget(IInputElement? el) =>
        el is TextBox or PasswordBox;

    private static void TryStartProcess(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                WorkingDirectory = Environment.SystemDirectory,
            });
        }
        catch
        {
            /* ignore */
        }
    }

    private static IntPtr TryFocusHwnd(IInputElement? focusAnchor)
    {
        try
        {
            if (focusAnchor is Visual v0 && PresentationSource.FromVisual(v0) is HwndSource hs0)
                return hs0.Handle;
        }
        catch
        {
            /* ignore */
        }

        try
        {
            if (Keyboard.FocusedElement is Visual v && PresentationSource.FromVisual(v) is HwndSource hs)
                return hs.Handle;
        }
        catch
        {
            /* ignore */
        }

        try
        {
            return GetForegroundWindow();
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static void TryComToggleTip(IntPtr hwnd)
    {
        try
        {
            var tip = (ITipInvocation)new TipInvocation();
            tip.Toggle(hwnd);
        }
        catch
        {
            /* нет COM */
        }
    }

    private const int SwRestore = 9;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private static void TryUnhideTipWindow()
    {
        try
        {
            var h = FindWindow("IPTip_Main_Window", null);
            if (h != IntPtr.Zero)
                ShowWindow(h, SwRestore);
        }
        catch
        {
            /* ignore */
        }
    }

    /// <summary>
    /// PreviewGotKeyboardFocus срабатывает до смены <see cref="Keyboard.FocusedElement"/> — передаём <see cref="KeyboardFocusChangedEventArgs.NewFocus"/>.
    /// </summary>
    internal static void OnPreviewKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is TextBox or PasswordBox)
            EnqueueShow(immediate: true, focusAnchor: e.NewFocus);
    }

    internal static void OnTextInputPreviewMouseDown(object sender, MouseButtonEventArgs e) =>
        FocusAndShow(sender);

    internal static void OnTextInputPreviewTouchDown(object? sender, TouchEventArgs e)
    {
        if (sender != null)
            FocusAndShow(sender);
    }

    internal static void OnTextInputPreviewStylusDown(object sender, StylusDownEventArgs e) =>
        FocusAndShow(sender);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out Rect32 lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref Point32 lpPoint);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect32 lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [ComImport, Guid("37c994e7-432b-4834-a2f7-dc106a0288b5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITipInvocation
    {
        void Toggle(IntPtr hwnd);
    }

    [ComImport, Guid("4ce819fa-9660-4545-9f31-7717af886ad8"), ClassInterface(ClassInterfaceType.None)]
    private class TipInvocation
    {
    }
}
