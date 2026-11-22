using System.Runtime.InteropServices.WindowsRuntime;
using CalcLink.App.Helpers;
using CalcLink.App.Services;
using CalcLink.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
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

    private readonly OperationService _operations;
    private readonly DialogService _dialogs;
    private readonly PickerService _pickers;
    private readonly NotificationService _notifications;
    private readonly DispatcherQueueTimer _liveTimer;
    private ScreenImage? _image;
    private bool _capturing;
    private double _viewportWidth = 640, _viewportHeight = 480, _rasterScale = 1.0;
    private int _pixelFactor;

    public ScreenViewModel(ConnectionService connection, OperationService operations, DialogService dialogs,
        PickerService pickers, NotificationService notifications, WindowService window)
    {
        Connection = connection;
        _operations = operations;
        _dialogs = dialogs;
        _pickers = pickers;
        _notifications = notifications;

        _liveTimer = window.DispatcherQueue.CreateTimer();
        _liveTimer.Interval = TimeSpan.FromMilliseconds(333); // ~3 fps
        _liveTimer.Tick += OnLiveTick;

        connection.SessionChanged += (_, _) =>
        {
            IsLive = false;
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

    public bool HasImage => _image is not null;

    public bool CanCapture => Connection.Supports(CalcFeatures.Screen) && !_operations.IsBusy;

    public string CaptureTip => Connection.FeatureTip(CalcFeatures.Screen, "Capture the calculator screen");

    partial void OnIsLiveChanged(bool value)
    {
        if (value && CanCapture)
        {
            _liveTimer.Start();
            OnLiveTick(_liveTimer, null);
        }
        else
        {
            _liveTimer.Stop();
            if (value)
            {
                IsLive = false;
            }
        }
    }

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
        if (!CanCapture && !_operations.IsBusy)
        {
            IsLive = false;
        }
        OnPropertyChanged(nameof(CanCapture));
        OnPropertyChanged(nameof(CaptureTip));
        CaptureCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
    }

    private async void OnLiveTick(DispatcherQueueTimer sender, object? args)
    {
        // Skip this tick if the previous capture is still in flight, or a transfer is running.
        if (_capturing || _operations.IsBusy || Connection.Session is null)
        {
            return;
        }
        try
        {
            await CaptureCoreAsync();
        }
        catch (Exception ex) when (!Format.IsCancellation(ex))
        {
            IsLive = false;
            _notifications.Post("Live capture stopped", Format.Error(ex), InfoBarSeverity.Warning);
        }
        catch (Exception)
        {
            IsLive = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCapture))]
    private async Task CaptureAsync()
    {
        if (_capturing)
        {
            return;
        }
        try
        {
            await CaptureCoreAsync();
        }
        catch (Exception ex) when (Format.IsCancellation(ex))
        {
            _notifications.Post("Cancelled", "Screen capture was cancelled.", InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Screen capture failed", ex);
        }
    }

    private async Task CaptureCoreAsync()
    {
        if (Connection.Session is not { } session)
        {
            return;
        }
        _capturing = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var image = await session.CaptureScreenAsync(cts.Token);
            if (image.Width <= 0 || image.Height <= 0 || image.Bgra.Length < image.Width * image.Height * 4)
            {
                throw new CalcLinkException(-1, "The calculator returned an invalid screen image.");
            }
            bool sizeChanged = _image is null || _image.Width != image.Width || _image.Height != image.Height;
            _image = image;
            if (sizeChanged)
            {
                _pixelFactor = 0; // force a new bitmap
            }
            Render();
            StatusText = $"{image.Width} × {image.Height}   ·   captured {DateTime.Now:T}";
            OnPropertyChanged(nameof(HasImage));
            CopyCommand.NotifyCanExecuteChanged();
            SaveCommand.NotifyCanExecuteChanged();
        }
        finally
        {
            _capturing = false;
        }
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
