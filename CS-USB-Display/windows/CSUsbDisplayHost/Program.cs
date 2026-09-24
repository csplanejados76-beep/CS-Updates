using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CSUsbDisplayHost;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var autoStart = args.Any(x => x.Equals("--auto", StringComparison.OrdinalIgnoreCase));
        Application.Run(new MainForm(autoStart));
    }
}

public sealed class MainForm : Form
{
    private const int DevicePort = 27183;
    private const byte PacketVideo = 1;
    private const byte PacketAudio = 2;
    private const byte PacketMic = 3;

    private readonly Button _start = new() { Text = "Connect / Start", Width = 150, Height = 36 };
    private readonly Button _stop = new() { Text = "Stop", Width = 90, Height = 36, Enabled = false };
    private readonly Button _refreshDisplays = new() { Text = "Refresh displays", Width = 120, Height = 30 };
    private readonly ComboBox _display = new() { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(700, 0), Text = "Ready. Connect the tablet by USB." };
    private readonly NumericUpDown _fps = new() { Minimum = 5, Maximum = 30, Value = 20, Width = 60 };
    private readonly NumericUpDown _quality = new() { Minimum = 30, Maximum = 90, Value = 55, Width = 60 };
    private readonly NumericUpDown _width = new() { Minimum = 640, Maximum = 2560, Increment = 160, Value = 1280, Width = 80 };
    private readonly CheckBox _sendAudio = new() { Text = "PC audio → tablet", Checked = true, AutoSize = true };
    private readonly CheckBox _receiveMic = new() { Text = "Tablet mic → Windows", Checked = true, AutoSize = true };

    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
    private string? _adbPath;
    private string? _deviceSerial;
    private bool _starting;

    public MainForm(bool autoStart)
    {
        Text = "CS USB Display v0.2.1 — Extended + Audio + Mic";
        Width = 760;
        Height = 310;
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

        panel.Controls.Add(new Label { Text = "Windows display", AutoSize = true, Margin = new Padding(4, 8, 4, 0) });
        panel.Controls.Add(_display);
        panel.Controls.Add(_refreshDisplays);
        panel.SetFlowBreak(_refreshDisplays, true);

        panel.Controls.Add(new Label { Text = "FPS", AutoSize = true, Margin = new Padding(4, 10, 4, 0) });
        panel.Controls.Add(_fps);
        panel.Controls.Add(new Label { Text = "JPEG quality", AutoSize = true, Margin = new Padding(16, 10, 4, 0) });
        panel.Controls.Add(_quality);
        panel.Controls.Add(new Label { Text = "Width", AutoSize = true, Margin = new Padding(16, 10, 4, 0) });
        panel.Controls.Add(_width);
        panel.SetFlowBreak(_width, true);

        panel.Controls.Add(_sendAudio);
        panel.Controls.Add(_receiveMic);
        panel.SetFlowBreak(_receiveMic, true);

        _status.Margin = new Padding(4, 18, 4, 4);
        panel.Controls.Add(_status);
        Controls.Add(panel);

        RefreshDisplays();
        _refreshDisplays.Click += (_, _) => RefreshDisplays();
        _start.Click += async (_, _) => await StartAsync();
        _stop.Click += (_, _) => StopStreaming();
        FormClosing += (_, _) => StopStreaming();

        if (autoStart)
            Shown += async (_, _) => await StartAsync();
    }

    private void RefreshDisplays()
    {
        var current = (_display.SelectedItem as DisplayChoice)?.DeviceName;
        _display.Items.Clear();

        foreach (var screen in Screen.AllScreens)
        {
            var tag = screen.Primary ? "PRIMARY" : "EXTENDED";
            _display.Items.Add(new DisplayChoice(screen.DeviceName, screen.Bounds, $"{screen.DeviceName} — {screen.Bounds.Width}x{screen.Bounds.Height} — {tag}"));
        }

        var choices = _display.Items.Cast<DisplayChoice>().ToList();
        var selected = choices.FirstOrDefault(x => x.DeviceName == current)
            ?? choices.FirstOrDefault(x => !x.Label.Contains("PRIMARY", StringComparison.Ordinal))
            ?? choices.FirstOrDefault();

        if (selected is not null)
            _display.SelectedItem = selected;
    }

    private async Task StartAsync()
    {
        if (_starting || _cts is not null) return;
        _starting = true;
        _start.Enabled = false;
        TcpListener? listener = null;

        try
        {
            RefreshDisplays();
            if (_display.Items.Count < 2)
                throw new InvalidOperationException("Somente a tela principal foi detectada. Instale/ative o Virtual Display Driver e execute o modo Estender antes de conectar.");

            SetStatus("Checking ADB device…");
            var adb = FindAdb();
            var devicesOutput = await RunProcessAsync(adb, "devices");
            var serial = ParseFirstAuthorizedDevice(devicesOutput);
            if (serial is null)
                throw new InvalidOperationException("No authorized Android device found. Enable USB debugging and approve this computer on the tablet.");

            _adbPath = adb;
            _deviceSerial = serial;

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

            // Capture stable references before clearing the local ownership variable.
            // Lambdas capture variables by reference, so using 'listener' directly here
            // could race with 'listener = null' and produce a NullReferenceException.
            var activeListener = listener;
            var activeCts = _cts;
            _ = Task.Run(() => AcceptAndStreamAsync(activeListener, activeCts.Token, adb, serial));

            listener = null;
        }
        catch (Exception ex)
        {
            try { listener?.Stop(); } catch { }
            await RemoveReverseAsync();
            WriteDiagnostic("StartAsync", ex);
            SetStatus("Error: " + ex.Message);
            _start.Enabled = true;
            _stop.Enabled = false;
        }
        finally
        {
            _starting = false;
        }
    }

    private async Task AcceptAndStreamAsync(TcpListener listener, CancellationToken token, string adb, string serial)
    {
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var sessionToken = sessionCts.Token;

        try
        {
            using var client = await listener.AcceptTcpClientAsync(sessionToken);
            client.NoDelay = true;
            using var stream = client.GetStream();

            var (sendAudio, receiveMic) = ReadAudioOptions();
            using var micSink = receiveMic ? TryCreateVirtualMicSink() : null;

            var micText = !receiveMic
                ? "mic off"
                : micSink is null
                    ? "mic endpoint not found"
                    : "mic ready";
            Invoke(new Action(() => SetStatus($"USB connected — extended display + audio; {micText}.")));

            var channel = Channel.CreateBounded<Packet>(new BoundedChannelOptions(12)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

            using var loopback = sendAudio ? TryCreateLoopbackStreamer(channel.Writer) : null;

            var sender = SendPacketsAsync(stream, channel.Reader, sessionToken);
            var video = ProduceVideoAsync(channel.Writer, sessionToken);
            var mic = ReceiveMicAsync(stream, micSink, sessionToken);

            var finished = await Task.WhenAny(sender, video, mic);
            sessionCts.Cancel();
            channel.Writer.TryComplete();

            try { await Task.WhenAll(sender, video, mic); } catch (OperationCanceledException) { }
            await finished;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            WriteDiagnostic("AcceptAndStreamAsync", ex);
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

    private async Task ProduceVideoAsync(ChannelWriter<Packet> writer, CancellationToken token)
    {
        var encoder = ImageCodecInfo.GetImageEncoders().First(x => x.FormatID == ImageFormat.Jpeg.Guid);

        while (!token.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();
            var (targetWidth, quality, fps, bounds) = ReadVideoSettings();

            using var source = CaptureScreen(bounds);
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

            writer.TryWrite(new Packet(PacketVideo, ms.ToArray()));

            var elapsed = Stopwatch.GetElapsedTime(started);
            var delay = TimeSpan.FromSeconds(1.0 / fps) - elapsed;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, token);
        }
    }

    private static async Task SendPacketsAsync(NetworkStream stream, ChannelReader<Packet> reader, CancellationToken token)
    {
        await foreach (var packet in reader.ReadAllAsync(token))
        {
            var header = new byte[5];
            header[0] = packet.Type;
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1, 4), packet.Payload.Length);
            await stream.WriteAsync(header, token);
            await stream.WriteAsync(packet.Payload, token);
        }
    }

    private static async Task ReceiveMicAsync(NetworkStream stream, VirtualMicSink? micSink, CancellationToken token)
    {
        var header = new byte[5];

        while (!token.IsCancellationRequested)
        {
            await stream.ReadExactlyAsync(header, token);
            var type = header[0];
            var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1, 4));
            if (length < 0 || length > 2_000_000)
                throw new InvalidDataException($"Invalid incoming USB packet: {length} bytes.");

            var payload = new byte[length];
            await stream.ReadExactlyAsync(payload, token);

            if (type == PacketMic && micSink is not null)
                micSink.AddMonoPcm16(payload);
        }
    }

    private LoopbackStreamer? TryCreateLoopbackStreamer(ChannelWriter<Packet> writer)
    {
        try
        {
            return new LoopbackStreamer(writer);
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
                BeginInvoke(new Action(() => SetStatus("Video connected; Windows audio capture unavailable: " + ex.Message)));
            return null;
        }
    }

    private VirtualMicSink? TryCreateVirtualMicSink()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator
                .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .ToList();

            var device = devices.FirstOrDefault(d =>
                    d.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
                ?? devices.FirstOrDefault(d =>
                    d.FriendlyName.Contains("Voicemeeter Input", StringComparison.OrdinalIgnoreCase));

            return device is null ? null : new VirtualMicSink(device);
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap CaptureScreen(Rectangle bounds)
    {
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

    private (int width, long quality, int fps, Rectangle bounds) ReadVideoSettings()
    {
        if (InvokeRequired)
        {
            return ((int width, long quality, int fps, Rectangle bounds))Invoke(
                new Func<(int width, long quality, int fps, Rectangle bounds)>(ReadVideoSettings)
            );
        }

        var choice = _display.SelectedItem as DisplayChoice
            ?? throw new InvalidOperationException("No Windows display selected.");

        return ((int)_width.Value, (long)_quality.Value, (int)_fps.Value, choice.Bounds);
    }

    private (bool sendAudio, bool receiveMic) ReadAudioOptions()
    {
        if (InvokeRequired)
        {
            return ((bool sendAudio, bool receiveMic))Invoke(
                new Func<(bool sendAudio, bool receiveMic)>(ReadAudioOptions)
            );
        }

        return (_sendAudio.Checked, _receiveMic.Checked);
    }

    private void SetStatus(string text) => _status.Text = text;

    private static void WriteDiagnostic(string area, Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CS USB Display"
            );
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "host.log");
            File.AppendAllText(
                file,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {area}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}"
            );
        }
        catch
        {
            // Diagnostics must never interrupt the display session.
        }
    }

    private sealed record Packet(byte Type, byte[] Payload);

    private sealed class DisplayChoice
    {
        public string DeviceName { get; }
        public Rectangle Bounds { get; }
        public string Label { get; }

        public DisplayChoice(string deviceName, Rectangle bounds, string label)
        {
            DeviceName = deviceName;
            Bounds = bounds;
            Label = label;
        }

        public override string ToString() => Label;
    }

    private sealed class LoopbackStreamer : IDisposable
    {
        private readonly WasapiLoopbackCapture _capture;
        private readonly ChannelWriter<Packet> _writer;

        public LoopbackStreamer(ChannelWriter<Packet> writer)
        {
            _writer = writer;
            _capture = new WasapiLoopbackCapture();
            _capture.DataAvailable += OnDataAvailable;
            _capture.StartRecording();
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            try
            {
                var pcm = ConvertToStereoPcm16(e.Buffer, e.BytesRecorded, _capture.WaveFormat);
                if (pcm.Length == 0) return;

                var payload = new byte[4 + pcm.Length];
                BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0, 4), _capture.WaveFormat.SampleRate);
                Buffer.BlockCopy(pcm, 0, payload, 4, pcm.Length);
                _writer.TryWrite(new Packet(PacketAudio, payload));
            }
            catch
            {
                // Keep video alive if the system mix format changes unexpectedly.
            }
        }

        private static byte[] ConvertToStereoPcm16(byte[] source, int count, WaveFormat format)
        {
            var channels = Math.Max(1, format.Channels);
            var bytesPerSample = Math.Max(1, format.BitsPerSample / 8);
            var frameSize = Math.Max(bytesPerSample * channels, format.BlockAlign);
            var frames = count / frameSize;
            var output = new byte[frames * 4];

            for (var frame = 0; frame < frames; frame++)
            {
                var baseOffset = frame * frameSize;
                var left = ReadSample(source, baseOffset, bytesPerSample);
                var right = channels > 1
                    ? ReadSample(source, baseOffset + bytesPerSample, bytesPerSample)
                    : left;

                var l = (short)Math.Clamp((int)Math.Round(left * 32767f), short.MinValue, short.MaxValue);
                var r = (short)Math.Clamp((int)Math.Round(right * 32767f), short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(frame * 4, 2), l);
                BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(frame * 4 + 2, 2), r);
            }

            return output;
        }

        private static float ReadSample(byte[] buffer, int offset, int bytesPerSample)
        {
            if (bytesPerSample == 4)
                return Math.Clamp(BitConverter.ToSingle(buffer, offset), -1f, 1f);
            if (bytesPerSample == 2)
                return BitConverter.ToInt16(buffer, offset) / 32768f;
            return 0f;
        }

        public void Dispose()
        {
            try { _capture.StopRecording(); } catch { }
            _capture.DataAvailable -= OnDataAvailable;
            _capture.Dispose();
        }
    }

    private sealed class VirtualMicSink : IDisposable
    {
        private readonly BufferedWaveProvider _buffer;
        private readonly WasapiOut _output;

        public VirtualMicSink(MMDevice device)
        {
            _buffer = new BufferedWaveProvider(new WaveFormat(48000, 16, 2))
            {
                BufferDuration = TimeSpan.FromSeconds(1),
                DiscardOnBufferOverflow = true
            };

            _output = new WasapiOut(device, AudioClientShareMode.Shared, false, 50);
            _output.Init(_buffer);
            _output.Play();
        }

        public void AddMonoPcm16(byte[] mono)
        {
            if (mono.Length < 2) return;
            var stereo = new byte[(mono.Length / 2) * 4];
            var dst = 0;

            for (var src = 0; src + 1 < mono.Length; src += 2)
            {
                stereo[dst++] = mono[src];
                stereo[dst++] = mono[src + 1];
                stereo[dst++] = mono[src];
                stereo[dst++] = mono[src + 1];
            }

            _buffer.AddSamples(stereo, 0, dst);
        }

        public void Dispose()
        {
            try { _output.Stop(); } catch { }
            _output.Dispose();
        }
    }
}
