using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;

namespace CSUsbDisplayHost;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

public sealed class MainForm : Form
{
    private const int DevicePort = 27183;
    private readonly Button _start = new() { Text = "Connect / Start", Width = 150, Height = 36 };
    private readonly Button _stop = new() { Text = "Stop", Width = 90, Height = 36, Enabled = false };
    private readonly Label _status = new() { AutoSize = true, Text = "Ready. Connect the tablet by USB." };
    private readonly NumericUpDown _fps = new() { Minimum = 5, Maximum = 30, Value = 20, Width = 60 };
    private readonly NumericUpDown _quality = new() { Minimum = 30, Maximum = 90, Value = 55, Width = 60 };
    private readonly NumericUpDown _width = new() { Minimum = 640, Maximum = 2560, Increment = 160, Value = 1280, Width = 80 };

    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
    private string? _adbPath;
    private string? _deviceSerial;

    public MainForm()
    {
        Text = "CS USB Display v0.1.1";
        Width = 540;
        Height = 250;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoScroll = true
        };

        panel.Controls.Add(_start);
        panel.Controls.Add(_stop);
        panel.SetFlowBreak(_stop, true);
        panel.Controls.Add(new Label { Text = "FPS", AutoSize = true, Margin = new Padding(4, 10, 4, 0) });
        panel.Controls.Add(_fps);
        panel.Controls.Add(new Label { Text = "JPEG quality", AutoSize = true, Margin = new Padding(16, 10, 4, 0) });
        panel.Controls.Add(_quality);
        panel.Controls.Add(new Label { Text = "Width", AutoSize = true, Margin = new Padding(16, 10, 4, 0) });
        panel.Controls.Add(_width);
        panel.SetFlowBreak(_width, true);
        _status.Margin = new Padding(4, 16, 4, 4);
        panel.Controls.Add(_status);
        Controls.Add(panel);

        _start.Click += async (_, _) => await StartAsync();
        _stop.Click += (_, _) => StopStreaming();
        FormClosing += (_, _) => StopStreaming();
    }

    private async Task StartAsync()
    {
        _start.Enabled = false;
        TcpListener? listener = null;

        try
        {
            SetStatus("Checking ADB device…");
            var adb = FindAdb();
            var devicesOutput = await RunProcessAsync(adb, "devices");
            var serial = ParseFirstAuthorizedDevice(devicesOutput);
            if (serial is null)
                throw new InvalidOperationException("No authorized Android device found. Enable USB debugging and approve the computer on the tablet.");

            _adbPath = adb;
            _deviceSerial = serial;

            // Let Windows choose a free local port instead of always binding 27183.
            // The tablet still connects to 27183; ADB maps that device port to this free host port.
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(1);
            var hostPort = ((IPEndPoint)listener.LocalEndpoint).Port;

            SetStatus($"Creating USB tunnel (tablet {DevicePort} → PC {hostPort})…");
            await RunProcessAllowFailureAsync(adb, $"-s {serial} reverse --remove tcp:{DevicePort}");
            await RunProcessAsync(adb, $"-s {serial} reverse tcp:{DevicePort} tcp:{hostPort}");

            _cts = new CancellationTokenSource();
            _listener = listener;
            _stop.Enabled = true;

            _ = RunProcessAllowFailureAsync(adb, $"-s {serial} shell am start -n com.cs.usbdisplay/.MainActivity");

            SetStatus($"Waiting for tablet over USB… PC port {hostPort}");
            _ = Task.Run(() => AcceptAndStreamAsync(listener, _cts.Token, adb, serial));
            listener = null; // ownership transferred to AcceptAndStreamAsync
        }
        catch (Exception ex)
        {
            try { listener?.Stop(); } catch { }
            await RemoveReverseAsync();
            SetStatus("Error: " + ex.Message);
            _start.Enabled = true;
            _stop.Enabled = false;
        }
    }

    private async Task AcceptAndStreamAsync(TcpListener listener, CancellationToken token, string adb, string serial)
    {
        try
        {
            using var client = await listener.AcceptTcpClientAsync(token);
            client.NoDelay = true;
            Invoke(new Action(() => SetStatus("USB connected — streaming screen.")));

            using var stream = new BufferedStream(client.GetStream(), 512 * 1024);
            var encoder = ImageCodecInfo.GetImageEncoders().First(x => x.FormatID == ImageFormat.Jpeg.Guid);

            while (!token.IsCancellationRequested && client.Connected)
            {
                var started = Stopwatch.GetTimestamp();
                var (targetWidth, quality, fps) = ReadSettings();

                using var source = CapturePrimaryScreen();
                var targetHeight = Math.Max(1, (int)Math.Round(source.Height * (targetWidth / (double)source.Width)));
                using var scaled = new Bitmap(targetWidth, targetHeight, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                    g.DrawImage(source, 0, 0, targetWidth, targetHeight);
                }

                using var ms = new MemoryStream(512 * 1024);
                using (var p = new EncoderParameters(1))
                {
                    p.Param[0] = new EncoderParameter(Encoder.Quality, quality);
                    scaled.Save(ms, encoder, p);
                }

                var bytes = ms.GetBuffer();
                var count = checked((int)ms.Length);
                var header = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(header, count);

                await stream.WriteAsync(header, token);
                await stream.WriteAsync(bytes.AsMemory(0, count), token);
                await stream.FlushAsync(token);

                var elapsed = Stopwatch.GetElapsedTime(started);
                var delay = TimeSpan.FromSeconds(1.0 / fps) - elapsed;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!IsDisposed)
                BeginInvoke(new Action(() => SetStatus("Connection ended: " + ex.Message)));
        }
        finally
        {
            try { listener.Stop(); } catch { }
            await RunProcessAllowFailureAsync(adb, $"-s {serial} reverse --remove tcp:{DevicePort}");

            if (!IsDisposed)
            {
                BeginInvoke(new Action(() =>
                {
                    if (ReferenceEquals(_listener, listener))
                    {
                        _listener = null;
                        _cts?.Dispose();
                        _cts = null;
                        _adbPath = null;
                        _deviceSerial = null;
                    }

                    _start.Enabled = true;
                    _stop.Enabled = false;
                }));
            }
        }
    }

    private static Bitmap CapturePrimaryScreen()
    {
        var bounds = Screen.PrimaryScreen?.Bounds ?? throw new InvalidOperationException("Primary screen not found.");
        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    private void StopStreaming()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }

        var adb = _adbPath;
        var serial = _deviceSerial;
        if (!string.IsNullOrWhiteSpace(adb) && !string.IsNullOrWhiteSpace(serial))
            _ = RunProcessAllowFailureAsync(adb, $"-s {serial} reverse --remove tcp:{DevicePort}");

        _start.Enabled = true;
        _stop.Enabled = false;
        SetStatus("Stopped.");
    }

    private async Task RemoveReverseAsync()
    {
        if (!string.IsNullOrWhiteSpace(_adbPath) && !string.IsNullOrWhiteSpace(_deviceSerial))
            await RunProcessAllowFailureAsync(_adbPath, $"-s {_deviceSerial} reverse --remove tcp:{DevicePort}");
    }

    private static string? ParseFirstAuthorizedDevice(string adbDevicesOutput)
    {
        foreach (var line in adbDevicesOutput.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.EndsWith("\tdevice", StringComparison.Ordinal))
                return trimmed[..trimmed.IndexOf('\t')];
        }
        return null;
    }

    private static string FindAdb()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "platform-tools", "adb.exe");
        if (File.Exists(bundled)) return bundled;
        return "adb";
    }

    private static async Task<string> RunProcessAsync(string file, string args)
    {
        var (exitCode, stdout, stderr) = await RunProcessCoreAsync(file, args);
        if (exitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim());
        return stdout;
    }

    private static async Task RunProcessAllowFailureAsync(string file, string args)
    {
        try { await RunProcessCoreAsync(file, args); } catch { }
    }

    private static async Task<(int exitCode, string stdout, string stderr)> RunProcessCoreAsync(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start " + file);
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private (int width, long quality, int fps) ReadSettings()
    {
        if (InvokeRequired)
        {
            return ((int width, long quality, int fps))Invoke(
                new Func<(int width, long quality, int fps)>(ReadSettings)
            );
        }

        return ((int)_width.Value, (long)_quality.Value, (int)_fps.Value);
    }

    private void SetStatus(string text) => _status.Text = text;
}
