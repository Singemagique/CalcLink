using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using CalcLink.App.Helpers;
using CalcLink.App.Services;
using CalcLink.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace CalcLink.App.ViewModels;

/// <summary>
/// Screen capture with live mode. Pixels are upscaled by an integer factor ourselves (nearest
/// neighbour) and displayed 1:1 with physical pixels, so LCD pixels stay crisp at any DPI.
/// </summary>
public sealed partial class ScreenViewModel : ObservableObject
{
    public static readonly string[] ZoomOptions = ["Fit", "1×", "2×", "3×"];

    /// <summary>Live view never asks for frames faster than this, even on a fast link.</summary>
    private static readonly TimeSpan MinFrameInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Safety net for a calculator that stops answering (a serial TI-83 Plus grab takes several seconds).</summary>
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(20);

    /// <summary>How often Live view looks again while another operation has the link.</summary>
    private static readonly TimeSpan BusyRetryInterval = TimeSpan.FromMilliseconds(250);

    private readonly OperationService _operations;
    private readonly DialogService _dialogs;
    private readonly PickerService _pickers;
    private readonly NotificationService _notifications;
    private ScreenImage? _image;
    private CancellationTokenSource? _liveCts;
    private double? _frameSeconds; // smoothed capture time on the current connection
    private double _viewportWidth = 640, _viewportHeight = 480, _rasterScale = 1.0;
    private int _pixelFactor;

    public ScreenViewModel(ConnectionService connection, OperationService operations, DialogService dialogs,
        PickerService pickers, NotificationService notifications)
    {
        Connection = connection;
        _operations = operations;
        _dialogs = dialogs;
        _pickers = pickers;
        _notifications = notifications;

        connection.SessionChanged += (_, _) =>
        {
            IsLive = false;
            _frameSeconds = null; // another calculator (or link) has another speed
            UpdateState();
        };
        operations.BusyChanged += (_, _) => UpdateState();
        ZoomIndex = 0;
    }

    public ConnectionService Connection { get; }

    public IReadOnlyList<string> Zooms => ZoomOptions;

    [ObservableProperty]
    public partial WriteableBitmap? Bitmap { get; private set; }

    [ObservableProperty]
    public partial double DisplayWidth { get; private set; }

    [ObservableProperty]
    public partial double DisplayHeight { get; private set; }

    [ObservableProperty]
    public partial bool IsLive { get; set; }

    [ObservableProperty]
    public partial int ZoomIndex { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    /// <summary>A screen grab (single capture or live frame) is in flight; only one runs at a time.</summary>
    [ObservableProperty]
    public partial bool IsCapturing { get; private set; }

    public bool HasImage => _image is not null;

    private bool ScreenSupported => Connection.Supports(CalcFeatures.Screen);

    /// <summary>Single capture: not while another operation, another capture or Live view is running.</summary>
    public bool CanCapture => ScreenSupported && !_operations.IsBusy && !IsCapturing && !IsLive;

    /// <summary>Live can always be turned off, and turned on whenever a single capture could start.</summary>
    public bool CanToggleLive => IsLive || (ScreenSupported && !_operations.IsBusy && !IsCapturing);

    /// <summary>Progress ring next to Capture: single captures only (Live reports its rate in the status line).</summary>
    public bool ShowCaptureProgress => IsCapturing && !IsLive;

    public string CaptureTip => Connection.FeatureTip(CalcFeatures.Screen, "Capture the calculator screen");

    /// <summary>Honest about speed: once a frame has been timed on this connection, say what it achieves.</summary>
    public string LiveTip => _frameSeconds is { } s
        ? $"Capture continuously. This calculator manages {RateText(s)}."
        : "Capture continuously, as fast as the calculator can send its screen. Older models on a serial cable take several seconds per frame.";

    partial void OnIsLiveChanged(bool value)
    {
        _liveCts?.Cancel();
        _liveCts = null;
        if (value)
        {
            if (!ScreenSupported || _operations.IsBusy || IsCapturing)
            {
                IsLive = false;
                return;
            }
            var cts = new CancellationTokenSource();
            _liveCts = cts;
            _ = RunLiveAsync(cts);
        }
        UpdateState();
    }

    partial void OnIsCapturingChanged(bool value) => UpdateState();

    partial void OnZoomIndexChanged(int value) => Render();

    public void StopLive() => IsLive = false;

    /// <summary>Called by the page when its viewport size or DPI changes.</summary>
    public void UpdateViewport(double width, double height, double rasterScale)
    {
        _viewportWidth = Math.Max(1, width);
        _viewportHeight = Math.Max(1, height);
        _rasterScale = rasterScale <= 0 ? 1 : rasterScale;
        Render();
    }

    private void UpdateState()
    {
        if (IsLive && !ScreenSupported)
        {
            IsLive = false; // comes back here through OnIsLiveChanged
            return;
        }
        OnPropertyChanged(nameof(CanCapture));
        OnPropertyChanged(nameof(CanToggleLive));
        OnPropertyChanged(nameof(ShowCaptureProgress));
        OnPropertyChanged(nameof(CaptureTip));
        OnPropertyChanged(nameof(LiveTip));
        CaptureCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Single capture. Runs through <see cref="OperationService"/> so it shares the app-wide busy state,
    /// shows in the transfer panel with Cancel, and reports an error once, in a dialog.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCapture))]
    private async Task CaptureAsync()
    {
        if (Connection.Session is not { } session || IsCapturing)
        {
            return;
        }
        IsCapturing = true;
        try
        {
            var watch = Stopwatch.StartNew();
            var (outcome, image) = await _operations.RunAsync("Capturing the screen",
                (_, ct) => GrabAsync(session, ct));
            if (outcome == OperationOutcome.Succeeded && image is not null)
            {
                RecordFrameTime(watch.Elapsed);
                Show(image);
                StatusText = string.Create(CultureInfo.CurrentCulture,
                    $"{image.Width} × {image.Height}   ·   captured {DateTime.Now:T} in {watch.Elapsed.TotalSeconds:0.0} s");
            }
        }
        finally
        {
            IsCapturing = false;
        }
    }

    /// <summary>
    /// Live view: one frame at a time, at most one per <see cref="MinFrameInterval"/>. It stays outside
    /// <see cref="OperationService"/> (a dialog per failed frame would be unusable), pauses while an
    /// operation has the link, and on the first error stops and posts a single notification.
    /// </summary>
    private async Task RunLiveAsync(CancellationTokenSource cts)
    {
        var ct = cts.Token;
        string before = StatusText;
        int frames = 0;
        try
        {
            StatusText = "Live view: waiting for the first frame…";
            while (!ct.IsCancellationRequested)
            {
                if (_operations.IsBusy || IsCapturing || Connection.Session is not { } session)
                {
                    await Task.Delay(BusyRetryInterval, ct);
                    continue;
                }

                var watch = Stopwatch.StartNew();
                ScreenImage image;
                IsCapturing = true;
                try
                {
                    image = await GrabAsync(session, ct);
                }
                finally
                {
                    IsCapturing = false;
                }
                if (ct.IsCancellationRequested || !ReferenceEquals(session, Connection.Session))
                {
                    break; // stopped or disconnected while the frame was in flight: drop it
                }
                frames++;
                RecordFrameTime(watch.Elapsed);
                Show(image);
                StatusText = $"{image.Width} × {image.Height}   ·   live, {RateText(_frameSeconds!.Value)}   ·   {DateTime.Now:T}";

                var wait = MinFrameInterval - watch.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, ct);
                }
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // Turned off, page left, or disconnected: nothing to report.
        }
        catch (Exception ex)
        {
            IsLive = false;
            // A lost link is already reported by the shell ("Connection lost"); don't add a second message.
            if (!(ex is CalcLinkException c && CalcErrorCodes.IsDisconnect(c.Code)))
            {
                _notifications.Post("Live view stopped",
                    Format.IsCancellation(ex) ? "The calculator stopped sending its screen." : Format.Error(ex),
                    InfoBarSeverity.Warning);
            }
        }
        finally
        {
            if (ReferenceEquals(_liveCts, cts))
            {
                // The loop ended on its own (for example the session went away): reflect that in the toggle.
                _liveCts = null;
                IsLive = false;
            }
            cts.Dispose();
            StatusText = frames > 0 && _image is { } img && _frameSeconds is { } s
                ? $"{img.Width} × {img.Height}   ·   live view stopped (it ran at {RateText(s)})"
                : before;
        }
    }

    /// <summary>One validated screen grab with a timeout. A timeout is reported as an error, not a cancellation.</summary>
    private static async Task<ScreenImage> GrabAsync(ICalculatorSession session, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CaptureTimeout);
        ScreenImage image;
        try
        {
            image = await session.CaptureScreenAsync(timeout.Token);
        }
        catch (Exception ex) when (Format.IsCancellation(ex) && !ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The calculator did not send its screen within {CaptureTimeout.TotalSeconds:0} seconds. " +
                "Make sure it is switched on and showing the home screen.", ex);
        }
        if (image.Width <= 0 || image.Height <= 0 || image.Bgra.Length < image.Width * image.Height * 4)
        {
            throw new CalcLinkException(-1, "The calculator returned an invalid screen image.");
        }
        return image;
    }

    private void Show(ScreenImage image)
    {
        bool sizeChanged = _image is null || _image.Width != image.Width || _image.Height != image.Height;
        _image = image;
        if (sizeChanged)
        {
            _pixelFactor = 0; // force a new bitmap
        }
        Render();
        OnPropertyChanged(nameof(HasImage));
        CopyCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
    }

    private void RecordFrameTime(TimeSpan elapsed)
    {
        double s = Math.Max(0.001, elapsed.TotalSeconds);
        _frameSeconds = _frameSeconds is { } old ? 0.7 * old + 0.3 * s : s;
        OnPropertyChanged(nameof(LiveTip));
    }

    /// <summary>"about 3 fps" at one frame a second or better, otherwise "a frame every 4.2 s".</summary>
    private static string RateText(double secondsPerFrame)
    {
        double effective = Math.Max(secondsPerFrame, MinFrameInterval.TotalSeconds);
        return effective <= 1.0
            ? string.Create(CultureInfo.CurrentCulture, $"about {1 / effective:0.#} fps")
            : string.Create(CultureInfo.CurrentCulture, $"a frame every {effective:0.#} s");
    }

    /// <summary>Upscales the last capture by an integer factor (nearest neighbour) into <see cref="Bitmap"/>.</summary>
    private void Render()
    {
        if (_image is not { } img)
        {
            return;
        }

        // Integer factor in *physical* pixels: explicit zooms are DIP-based, Fit is the largest
        // integer factor that fits the viewport.
        int factor = ZoomIndex is >= 1 and <= 3
            ? (int)Math.Round(ZoomIndex * _rasterScale)
            : (int)Math.Floor(Math.Min(_viewportWidth * _rasterScale / img.Width, _viewportHeight * _rasterScale / img.Height));
        factor = Math.Clamp(factor, 1, 16);

        int w = img.Width * factor, h = img.Height * factor;
        var bitmap = Bitmap;
        if (bitmap is null || factor != _pixelFactor || bitmap.PixelWidth != w || bitmap.PixelHeight != h)
        {
            bitmap = new WriteableBitmap(w, h);
        }
        _pixelFactor = factor;

        byte[] scaled = Upscale(img, factor);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(scaled, 0, w * h * 4);
        }
        bitmap.Invalidate();

        if (!ReferenceEquals(bitmap, Bitmap))
        {
            Bitmap = bitmap;
        }
        DisplayWidth = w / _rasterScale;
        DisplayHeight = h / _rasterScale;
    }

    private static byte[] Upscale(ScreenImage img, int factor)
    {
        int srcStride = img.Width * 4;
        int dstStride = srcStride * factor;
        var dst = new byte[dstStride * img.Height * factor];
        var src = img.Bgra;
        for (int y = 0; y < img.Height; y++)
        {
            int rowStart = y * factor * dstStride;
            int d = rowStart;
            int s = y * srcStride;
            for (int x = 0; x < img.Width; x++, s += 4)
            {
                for (int k = 0; k < factor; k++, d += 4)
                {
                    dst[d] = src[s];
                    dst[d + 1] = src[s + 1];
                    dst[d + 2] = src[s + 2];
                    dst[d + 3] = 255; // LCD pixels are opaque
                }
            }
            for (int r = 1; r < factor; r++)
            {
                System.Buffer.BlockCopy(dst, rowStart, dst, rowStart + r * dstStride, dstStride);
            }
        }
        return dst;
    }

    private async Task<InMemoryRandomAccessStream> EncodePngAsync(int factor)
    {
        var img = _image!;
        byte[] pixels = Upscale(img, factor);
        var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            (uint)(img.Width * factor), (uint)(img.Height * factor), 96, 96, pixels);
        await encoder.FlushAsync();
        stream.Seek(0);
        return stream;
    }

    /// <summary>Copy/save use the current zoom factor (at least 1×) so the result matches what is shown.</summary>
    private int ExportFactor => Math.Max(1, ZoomIndex is >= 1 and <= 3 ? ZoomIndex : 1);

    [RelayCommand(CanExecute = nameof(HasImage))]
    private async Task CopyAsync()
    {
        try
        {
            var stream = await EncodePngAsync(ExportFactor);
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
            Clipboard.SetContent(package);
            Clipboard.Flush();
            _notifications.Post("Copied", "The screenshot is on the clipboard.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Copy failed", ex);
        }
    }

    [RelayCommand(CanExecute = nameof(HasImage))]
    private async Task SaveAsync()
    {
        try
        {
            string name = $"{Connection.ModelName} screen {DateTime.Now:yyyy-MM-dd HHmmss}".Replace(' ', '_');
            var path = await _pickers.PickSaveFileAsync(name,
                [new KeyValuePair<string, IList<string>>("PNG image", [".png"])]);
            if (path is null)
            {
                return;
            }
            using var png = await EncodePngAsync(ExportFactor);
            var bytes = new byte[png.Size];
            using (var reader = new DataReader(png.GetInputStreamAt(0)))
            {
                await reader.LoadAsync((uint)png.Size);
                reader.ReadBytes(bytes);
            }
            await File.WriteAllBytesAsync(path, bytes);
            _notifications.Post("Saved", $"Screenshot saved to {path}.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Saving the screenshot failed", ex);
        }
    }
}
