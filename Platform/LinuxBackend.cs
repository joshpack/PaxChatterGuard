using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using pxdChatterGuard.Core;

namespace pxdChatterGuard.Platform;

/// <summary>
/// Linux: the approach used by input remappers. Each keyboard's evdev device is grabbed exclusively, its events are
/// filtered, and the survivors are re-emitted through a virtual keyboard created with uinput. It works under X11, Wayland
/// and on the console alike. It needs permission to read /dev/input/event* and write /dev/uinput (see <see cref="SetupHint"/>).
/// If this program ever dies, the kernel releases the grab by itself and the real keyboard comes straight back.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed unsafe class LinuxBackend : IKeyboardBackend
{
    private const string VirtualName = "pxd Chatter Guard virtual keyboard";

    private const int O_RDONLY = 0, O_RDWR = 2, O_NONBLOCK = 0x800;
    private const ushort EV_SYN = 0, EV_KEY = 1, EV_MSC = 4, EV_LED = 0x11;
    private const ushort MSC_SCAN = 4;
    private const ulong EVIOCGRAB = 0x40044590;
    private const ulong UI_SET_EVBIT = 0x40045564, UI_SET_KEYBIT = 0x40045565, UI_SET_MSCBIT = 0x40045568, UI_SET_LEDBIT = 0x40045569;
    private const ulong UI_DEV_CREATE = 0x5501, UI_DEV_DESTROY = 0x5502;
    private const int EventSize = 24; // 64-bit struct input_event
    private const short POLLIN = 1;

    private ChatterEngine? _engine;
    private volatile bool _running;
    private Thread? _manager;
    private readonly object _workersLock = new();
    private readonly List<Worker> _workers = new();
    private IReadOnlyCollection<string>? _filter;
    private List<KeyboardDevice> _devices = new();
    private string? _firstProblem;

    public string Name => "Linux evdev/uinput filter";
    public bool IsRunning => _running;
    public event Action<string>? Notice;
    public IReadOnlyList<KeyboardDevice> Devices => _devices;

    public string? SetupHint =>
        "pxd Chatter Guard needs to read your keyboards and create a virtual one.\n" +
        "1) Add yourself to the input group:  sudo usermod -aG input $USER\n" +
        "2) Allow that group to use uinput:  echo 'KERNEL==\"uinput\", GROUP=\"input\", MODE=\"0660\"' | sudo tee /etc/udev/rules.d/99-uinput.rules\n" +
        "3) Load the module at boot:  echo uinput | sudo tee /etc/modules-load.d/uinput.conf && sudo modprobe uinput\n" +
        "4) Log out and back in, then press Retry.";

    public void SetDeviceFilter(IReadOnlyCollection<string>? names) => _filter = names;

    public string? Start(ChatterEngine engine)
    {
        if (_running)
            return null;
        if (!Environment.Is64BitProcess)
            return "The Linux filter needs a 64-bit system.";

        _engine = engine;
        _firstProblem = null;
        _running = true;
        Scan();

        List<Worker> started;
        lock (_workersLock)
            started = _workers.ToList();
        if (started.Count == 0)
        {
            _running = false;
            return _firstProblem ?? "No keyboards were found to filter.";
        }

        _manager = new Thread(ManagerMain) { IsBackground = true, Name = "Chatter devices" };
        _manager.Start();
        return null;
    }

    public void Stop()
    {
        if (!_running)
            return;
        _running = false;
        _manager?.Join(1500);

        List<Worker> all;
        lock (_workersLock)
            all = _workers.ToList();
        foreach (var w in all)
            w.Shutdown();
        lock (_workersLock)
            _workers.Clear();
    }

    public void Dispose() => Stop();

    // ---- Finding and following keyboards -------------------------------------------------------------------------

    private void ManagerMain()
    {
        while (_running)
        {
            for (var i = 0; i < 30 && _running; i++)
                Thread.Sleep(100);
            if (_running)
                Scan();
        }
    }

    private void Scan()
    {
        var found = new List<KeyboardDevice>();
        string[] paths;
        try
        {
            paths = Directory.GetFiles("/dev/input", "event*");
        }
        catch
        {
            return;
        }

        foreach (var path in paths.OrderBy(p => p, StringComparer.Ordinal))
        {
            var fd = open(path, O_RDONLY | O_NONBLOCK);
            if (fd < 0)
            {
                _firstProblem ??= "Couldn't open your keyboards. " + SetupHint;
                continue;
            }

            try
            {
                if (!LooksLikeKeyboard(fd))
                    continue;
                var name = DeviceName(fd);
                if (name == VirtualName)
                    continue;
                found.Add(new KeyboardDevice(name, path));
            }
            finally
            {
                close(fd);
            }
        }

        _devices = found;
        var wanted = _filter is { Count: > 0 } f ? f : null;

        lock (_workersLock)
        {
            // Drop workers whose device vanished, or that the user has since excluded.
            foreach (var w in _workers.Where(w => !w.Alive || !found.Any(d => d.Path == w.Path) || (wanted is not null && !wanted.Contains(w.Name))).ToList())
            {
                w.Shutdown();
                _workers.Remove(w);
            }

            foreach (var dev in found)
            {
                if (_workers.Any(w => w.Path == dev.Path))
                    continue;
                if (wanted is not null && !wanted.Contains(dev.Name))
                    continue;

                var worker = new Worker(this, dev);
                var error = worker.Start();
                if (error is null)
                {
                    _workers.Add(worker);
                    Notice?.Invoke($"Filtering {dev.Name}");
                }
                else
                    _firstProblem ??= error;
            }
        }
    }

    private static bool LooksLikeKeyboard(int fd)
    {
        var evBits = new byte[4];
        fixed (byte* p = evBits)
            if (ioctl(fd, IocRead('E', 0x20, evBits.Length), (IntPtr)p) < 0 || (evBits[0] & (1 << EV_KEY)) == 0)
                return false;

        // A real keyboard has letters, not just a few media or power keys.
        var keyBits = new byte[96];
        fixed (byte* p = keyBits)
            if (ioctl(fd, IocRead('E', 0x20 + EV_KEY, keyBits.Length), (IntPtr)p) < 0)
                return false;
        bool Has(int code) => (keyBits[code / 8] & (1 << (code % 8))) != 0;
        return Has(30) && Has(44) && Has(57) && Has(28); // A, Z, Space, Enter
    }

    private static string DeviceName(int fd)
    {
        var buf = new byte[256];
        fixed (byte* p = buf)
            if (ioctl(fd, IocRead('E', 0x06, buf.Length), (IntPtr)p) < 0)
                return "Keyboard";
        var len = Array.IndexOf(buf, (byte)0);
        return Encoding.UTF8.GetString(buf, 0, len < 0 ? buf.Length : len);
    }

    // ---- Self-test -------------------------------------------------------------------------------------------------

    /// <summary>Types a key on the virtual keyboard (used only by the self-test).</summary>
    private void InjectKey(int canonical, bool down)
    {
        var code = ToEvdev(canonical);
        if (code == 0)
            return;
        Worker? w;
        lock (_workersLock)
            w = _workers.FirstOrDefault();
        w?.Write(new[] { new Ev(0, EV_KEY, (ushort)code, down ? 1 : 0), new Ev(0, EV_SYN, 0, 0) });
    }

    public bool SendSelfTest(int key, int gapMs)
    {
        if (!_running || _engine is null)
            return false;

        _ = Task.Run(() =>
        {
            void Step(bool down)
            {
                var v = _engine.OnKey(new KeyEvent(key, down, NowMicros(), Injected: true, SelfTest: true));
                if (v == Verdict.Pass)
                    InjectKey(key, down);
            }

            Step(true);
            Thread.Sleep(30);
            Step(false);
            Thread.Sleep(Math.Max(1, gapMs));
            Step(true);
            Thread.Sleep(25);
            Step(false);
        });
        return true;
    }

    // ---- One grabbed keyboard ----------------------------------------------------------------------------------------

    private readonly record struct Ev(long Micros, ushort Type, ushort Code, int Value);

    private sealed class Worker
    {
        private readonly LinuxBackend _owner;
        private int _fd = -1, _ufd = -1;
        private Thread? _thread;
        private volatile bool _stop;

        public Worker(LinuxBackend owner, KeyboardDevice device)
        {
            _owner = owner;
            Path = device.Path;
            Name = device.Name;
        }

        public string Path { get; }
        public string Name { get; }
        public bool Alive { get; private set; } = true;

        /// <summary>Creates the virtual keyboard, then grabs the real one. Returns null on success.</summary>
        public string? Start()
        {
            _ufd = open("/dev/uinput", O_RDWR | O_NONBLOCK);
            if (_ufd < 0)
                return "Couldn't open /dev/uinput. " + _owner.SetupHint;

            if (!CreateVirtual())
            {
                close(_ufd);
                _ufd = -1;
                return "Couldn't create the virtual keyboard.";
            }

            _fd = open(Path, O_RDONLY);
            if (_fd < 0 || ioctl(_fd, EVIOCGRAB, (IntPtr)1) < 0)
            {
                // Someone else (or a permission) has the keyboard; don't leave a half-built filter behind.
                if (_fd >= 0)
                    close(_fd);
                _fd = -1;
                DestroyVirtual();
                return $"Couldn't take over \"{Name}\" (is another remapping tool using it?).";
            }

            _thread = new Thread(Loop) { IsBackground = true, Name = "Chatter " + Name };
            _thread.Start();
            return null;
        }

        private bool CreateVirtual()
        {
            if (ioctl(_ufd, UI_SET_EVBIT, (IntPtr)EV_KEY) < 0
                || ioctl(_ufd, UI_SET_EVBIT, (IntPtr)EV_SYN) < 0
                || ioctl(_ufd, UI_SET_EVBIT, (IntPtr)EV_MSC) < 0
                || ioctl(_ufd, UI_SET_EVBIT, (IntPtr)EV_LED) < 0)
                return false;

            ioctl(_ufd, UI_SET_MSCBIT, (IntPtr)MSC_SCAN);
            for (var led = 0; led <= 2; led++) // Num, Caps and Scroll Lock lights
                ioctl(_ufd, UI_SET_LEDBIT, (IntPtr)led);
            for (var code = 1; code < 256; code++)
                ioctl(_ufd, UI_SET_KEYBIT, (IntPtr)code);

            // The long-standing uinput_user_dev layout: name[80], input_id (4 x u16), ff_effects_max, then four abs[64] arrays.
            var dev = new byte[80 + 8 + 4 + 4 * 64 * 4];
            Encoding.UTF8.GetBytes(VirtualName).CopyTo(dev, 0);
            BitConverter.GetBytes((ushort)3).CopyTo(dev, 80);       // bus: USB
            BitConverter.GetBytes((ushort)0x1209).CopyTo(dev, 82);  // vendor
            BitConverter.GetBytes((ushort)0xC4A7).CopyTo(dev, 84);  // product
            BitConverter.GetBytes((ushort)1).CopyTo(dev, 86);       // version

            fixed (byte* p = dev)
                if (write(_ufd, p, dev.Length) != dev.Length)
                    return false;
            return ioctl(_ufd, UI_DEV_CREATE, IntPtr.Zero) >= 0;
        }

        private void DestroyVirtual()
        {
            if (_ufd < 0)
                return;
            ioctl(_ufd, UI_DEV_DESTROY, IntPtr.Zero);
            close(_ufd);
            _ufd = -1;
        }

        public void Shutdown()
        {
            _stop = true;
            _thread?.Join(1000);
            Cleanup();
        }

        private void Cleanup()
        {
            if (_fd >= 0)
            {
                ioctl(_fd, EVIOCGRAB, IntPtr.Zero); // give the real keyboard back
                close(_fd);
                _fd = -1;
            }

            DestroyVirtual();
            Alive = false;
        }

        private void Loop()
        {
            var buffer = new byte[EventSize * 64];
            var packet = new List<Ev>(16);
            var pollfds = stackalloc PollFd[2];

            try
            {
                while (!_stop && _owner._running)
                {
                    pollfds[0] = new PollFd { fd = _fd, events = POLLIN };
                    pollfds[1] = new PollFd { fd = _ufd, events = POLLIN };
                    var ready = poll(pollfds, 2, 200);
                    if (ready < 0)
                        break;
                    if (ready == 0)
                        continue;

                    if ((pollfds[0].revents & POLLIN) != 0)
                    {
                        long n;
                        fixed (byte* p = buffer)
                            n = read(_fd, p, buffer.Length);
                        if (n <= 0)
                            break; // unplugged

                        for (var off = 0; off + EventSize <= n; off += EventSize)
                        {
                            var ev = new Ev(
                                BitConverter.ToInt64(buffer, off) * 1_000_000L + BitConverter.ToInt64(buffer, off + 8),
                                BitConverter.ToUInt16(buffer, off + 16),
                                BitConverter.ToUInt16(buffer, off + 18),
                                BitConverter.ToInt32(buffer, off + 20));
                            packet.Add(ev);
                            if (ev.Type == EV_SYN && ev.Code == 0)
                            {
                                Forward(packet);
                                packet.Clear();
                            }
                        }
                    }

                    // The system asks the (virtual) keyboard to light its Caps/Num/Scroll lights; relay that to the real one.
                    if ((pollfds[1].revents & POLLIN) != 0)
                        RelayLeds(buffer);
                }
            }
            catch
            {
                // Fall through to cleanup: the real keyboard must always be handed back.
            }
            finally
            {
                Cleanup();
            }
        }

        private void RelayLeds(byte[] buffer)
        {
            long n;
            fixed (byte* p = buffer)
                n = read(_ufd, p, buffer.Length);
            for (var off = 0; n > 0 && off + EventSize <= n; off += EventSize)
            {
                if (BitConverter.ToUInt16(buffer, off + 16) != EV_LED)
                    continue;
                fixed (byte* p = buffer)
                    write(_fd, p + off, EventSize);
            }
        }

        private void Forward(List<Ev> packet)
        {
            var output = new List<Ev>(packet.Count);
            foreach (var ev in packet)
            {
                if (ev.Type == EV_KEY && ev.Value is 0 or 1 or 2)
                {
                    var key = ToCanonical(ev.Code);
                    if (key != 0)
                    {
                        // value 2 is auto-repeat; the engine treats a press with no release before it as exactly that.
                        var verdict = _owner._engine!.OnKey(new KeyEvent(key, ev.Value != 0, ev.Micros));
                        if (verdict != Verdict.Pass)
                        {
                            // The scan-code report that came just before this key event goes with it.
                            if (output.Count > 0 && output[^1].Type == EV_MSC && output[^1].Code == MSC_SCAN)
                                output.RemoveAt(output.Count - 1);
                            continue;
                        }
                    }
                }

                output.Add(ev);
            }

            if (output.Any(e => e.Type != EV_SYN))
                Write(output);
        }

        public void Write(IReadOnlyList<Ev> events)
        {
            if (_ufd < 0)
                return;
            var buf = new byte[events.Count * EventSize];
            for (var i = 0; i < events.Count; i++)
            {
                var o = i * EventSize;
                // The kernel stamps the time itself; only type, code and value matter.
                BitConverter.GetBytes((ushort)events[i].Type).CopyTo(buf, o + 16);
                BitConverter.GetBytes((ushort)events[i].Code).CopyTo(buf, o + 18);
                BitConverter.GetBytes(events[i].Value).CopyTo(buf, o + 20);
            }

            fixed (byte* p = buf)
                write(_ufd, p, buf.Length);
        }
    }

    // ---- Key code translation (evdev <-> the app's scancode-based ids) ---------------------------------------------------

    private static readonly (int Evdev, int Canonical)[] ExtendedKeys =
    {
        (96, 0xE01C), (97, 0xE01D), (98, 0xE035), (99, 0xE037), (100, 0xE038),
        (102, 0xE047), (103, 0xE048), (104, 0xE049), (105, 0xE04B), (106, 0xE04D),
        (107, 0xE04F), (108, 0xE050), (109, 0xE051), (110, 0xE052), (111, 0xE053),
        (125, 0xE05B), (126, 0xE05C), (127, 0xE05D),
    };

    /// <summary>The first 88 evdev key codes are the classic scancodes, so the common keys need no table.</summary>
    private static int ToCanonical(int evdev)
    {
        if (evdev is >= 1 and <= 88)
            return evdev;
        foreach (var (e, c) in ExtendedKeys)
            if (e == evdev)
                return c;
        return 0; // media keys, power button, etc.: never filtered
    }

    private static int ToEvdev(int canonical)
    {
        if (canonical is >= 1 and <= 88)
            return canonical;
        foreach (var (e, c) in ExtendedKeys)
            if (c == canonical)
                return e;
        return 0;
    }

    // evdev timestamps are wall-clock, so the engine's clock here is wall-clock microseconds too.
    private static long NowMicros() => (DateTime.UtcNow - DateTime.UnixEpoch).Ticks / 10;

    // ---- Native interop ------------------------------------------------------------------------------------------

    private static ulong IocRead(char type, int nr, int size) => (2UL << 30) | ((ulong)(uint)size << 16) | ((ulong)(uint)type << 8) | (ulong)(uint)nr;

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int fd;
        public short events;
        public short revents;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPStr)] string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern nint read(int fd, byte* buf, nint count);

    [DllImport("libc", SetLastError = true)]
    private static extern nint write(int fd, byte* buf, nint count);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, ulong request, IntPtr arg);

    [DllImport("libc", SetLastError = true)]
    private static extern int poll(PollFd* fds, ulong nfds, int timeoutMs);
}
