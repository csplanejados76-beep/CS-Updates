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
    private const int Port = 27183;
    private readonly Button _start = new() { Text = "Connect / Start", Width = 150, Height = 36 };
    private readonly Button _stop = new() { Text = "Stop", Width = 90, Height = 36, Enabled = false };
    private readonly Label _status = new() { AutoSize = true, Text = "Ready. Connect the tablet by USB." };
    private readonly NumericUpDown _fps = new() { Minimum = 5, Maximum = 30, Value = 20, Width = 60 };
    private readonly NumericUpDown _quality = new() { Minimum = 30, Maximum = 90, Value = 55, Width = 60 };
    private readonly NumericUpDown _width = new() { Minimum = 640, Maximum = 2560, Increment = 160, Value = 1280, Width = 80 };
    private CancellationTokenSource? _cts;
    private TcpListener? _listener;

    public MainForm()
    {
        Text = "CS USB Display v0.1.0";
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
        try
        {
            SetStatus("Checking ADB device…");
            var adb = FindAdb();
            var devices = await RunProcessAsync(adb, "devices");
            var connected = devices.Split('\n').Any(x => x.TrimEnd().EndsWith("\tdevice", StringComparison.Ordinal));
            if (!connected)
                throw new InvalidOperationException("No authorized Android device found. Enable USB debugging and approve the computer on the tablet.");

            SetStatus("Creating USB tunnel…");
            await RunProcessAsync(adb, $"reverse tcp:{Port} tcp:{Port}");
            _ = RunProcessAsync(adb, "shell am start -n com.cs.usbdisplay/.MainActivity");

            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Start(1);
            _stop.Enabled = true;
            SetStatus("Waiting for tablet app over USB…");
            _ = Task.Run(() => AcceptAndStreamAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            SetStatus("Error: " + ex.Message);
            _start.Enabled = true;
            _stop.Enabled = false;
        }
    }

    private async Task AcceptAndStreamAsync(CancellationToken token)
    {
        try
        {
            using var client = await _listener!.AcceptTcpClientAsync(token);
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
                if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!IsDisposed) BeginInvoke(new Action(() => SetStatus("Connection ended: " + ex.Message)));
        }
        finally
        {
            if (!IsDisposed) BeginInvoke(new Action(() =>
            {
                _start.Enabled = true;
                _stop.Enabled = false;
            }));
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
        _cts?.Dispose();
        _cts = null;
        _listener = null;
        _start.Enabled = true;
        _stop.Enabled = false;
        SetStatus("Stopped.");
    }

    private static string FindAdb()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "platform-tools", "adb.exe");
        if (File.Exists(bundled)) return bundled;
        return "adb";
    }

    private static async Task<string> RunProcessAsync(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start " + file);
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim());
        return stdout;
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
