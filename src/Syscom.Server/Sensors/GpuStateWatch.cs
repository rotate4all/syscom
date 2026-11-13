#if LHM
using System.Runtime.InteropServices;

namespace Syscom.Server.Sensors;

/// <summary>
/// Keeps SYSCOM away from a graphics card while Windows switches it off and on again, as it does for a moment while a
/// graphics driver is installed (or the card is disabled or restarted). NVIDIA's NVML library, which
/// LibreHardwareMonitor reads the card's power draw from, crashes the process when it's called around then: an access
/// violation in native code, which .NET can't catch.
/// <para>
/// Three signals, strongest first:
/// <list type="bullet">
/// <item>Windows asks the programs that have a card open before it removes the card, and waits for their answer.
/// SYSCOM keeps each card open for that (an administrator or the service can; a normal account can't). On the question
/// it stops new reads, waits for the read in progress to finish, closes the card and lets Windows go on.</item>
/// <item>Each card's device state, read right before the cards are read: Windows marks a card about 50 ms before it
/// starts switching it off, and the driver is unloaded a second or so later.</item>
/// <item>Windows' reports of a card's "display adapter" interface going away or coming back, so a card that went off
/// and came back between two reads is noticed too.</item>
/// </list>
/// <see cref="LhmSensorSource"/> reads the cards only between <see cref="TryBeginRead"/> and <see cref="EndRead"/>,
/// pauses them when <see cref="Changes"/> moves or a card isn't running, and opens them afresh once they're back.
/// </para>
/// </summary>
internal sealed unsafe class GpuStateWatch : IDisposable
{
    private static readonly Guid DisplayAdapterInterface = new("5b45201d-f2f2-4f3b-85bb-30ff1f953599");
    private const string DisplayClass = "{4d36e968-e325-11ce-bfc1-08002be10318}";
    // How long Windows is kept waiting for a read in progress (they take well under a second).
    private static readonly TimeSpan ReadWait = TimeSpan.FromSeconds(5);

    private readonly object _readGate = new();
    private readonly List<OpenCard> _cards = new();
    private volatile bool _removalAsked;
    private long _changes;
    private nint _interfaceRegistration;
    private GCHandle _self;

    private GpuStateWatch() { }

    /// <summary>How many times a card went away, came back or was asked for, since the watch started.</summary>
    public long Changes => Interlocked.Read(ref _changes);

    /// <summary>Whether Windows asked to remove a card since the last <see cref="Resume"/>.</summary>
    public bool RemovalAsked => _removalAsked;

    /// <summary>How many cards SYSCOM has open to be asked before they're removed (none without administrator rights).</summary>
    public int CardsOpen { get { lock (_cards) return _cards.Count(c => c.Handle != 0); } }

    /// <summary>Starts watching, or returns null where Windows doesn't offer the notifications.</summary>
    public static GpuStateWatch? TryStart()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var watch = new GpuStateWatch();
        watch._self = GCHandle.Alloc(watch);
        var filter = new NotifyFilter { Size = (uint)sizeof(NotifyFilter), FilterType = FilterTypeDeviceInterface, Target = 0 };
        filter.ClassGuid = DisplayAdapterInterface;
        nint registration;
        if (CM_Register_Notification(&filter, GCHandle.ToIntPtr(watch._self), &OnInterfaceChange, &registration) != 0)
        {
            watch._self.Free();
            return null;
        }
        watch._interfaceRegistration = registration;
        watch.OpenCards();
        return watch;
    }

    /// <summary>
    /// Call before reading the cards; false while Windows is removing one (from its question until <see cref="Resume"/>).
    /// When true, call <see cref="EndRead"/> afterwards: Windows waits for that before switching the card off.
    /// </summary>
    public bool TryBeginRead()
    {
        Monitor.Enter(_readGate);
        if (!_removalAsked) return true;
        Monitor.Exit(_readGate);
        return false;
    }

    public void EndRead() => Monitor.Exit(_readGate);

    /// <summary>After the cards were found again: open them anew to be asked next time, and allow reads.</summary>
    public void Resume()
    {
        _removalAsked = false;
        OpenCards();
    }

    /// <summary>
    /// The graphics cards Windows knows (display adapters on PCI: the vendors' drivers, not Microsoft's basic display
    /// or virtual adapters), by device instance ID, and whether each is running.
    /// </summary>
    public static Dictionary<string, bool> Adapters()
    {
        var adapters = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in List(GetIdListFilterClass | GetIdListFilterPresent, DisplayClass))
            if (id.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase))
                adapters[id] = IsRunning(id);
        return adapters;
    }

    /// <summary>
    /// Started, and no removal coming. Windows first asks whether the card may go (query-remove: "pending", then
    /// "active"), about 50 ms before it starts switching it off, and the driver is unloaded a second or so later.
    /// </summary>
    private static bool IsRunning(string instanceId) =>
        CM_Locate_DevNodeW(out var node, instanceId, 0) == 0
        && CM_Get_DevNode_Status(out var status, out _, node, 0) == 0
        && (status & DnStarted) != 0
        && (status & (DnHasProblem | DnWillBeRemoved | DnQueryRemovePending | DnQueryRemoveActive)) == 0;

    /// <summary>
    /// Opens each card's display adapter interface and asks Windows to tell before removing it. Cards whose earlier
    /// handle was closed for a removal are registered afresh. Never called from a notification callback.
    /// </summary>
    private void OpenCards()
    {
        lock (_cards)
        {
            foreach (var card in _cards.Where(c => c.Handle == 0).ToList())
            {
                card.Unregister();
                _cards.Remove(card);
            }
            var cls = DisplayAdapterInterface;
            foreach (var path in InterfaceList(&cls))
            {
                if (!path.Contains(@"PCI#", StringComparison.OrdinalIgnoreCase) || _cards.Any(c => c.Path == path)) continue;
                if (OpenCard.TryOpen(this, path) is { } card) _cards.Add(card);
            }
        }
    }

    /// <summary>
    /// Windows wants to remove a card: no new reads, wait for the one in progress, close the card so the removal can
    /// go on (an open handle would refuse it), and answer. Runs on Windows' notification thread.
    /// </summary>
    private void OnRemovalAsked(OpenCard card)
    {
        _removalAsked = true;
        Interlocked.Increment(ref _changes);
        if (Monitor.TryEnter(_readGate, ReadWait)) Monitor.Exit(_readGate);
        card.CloseHandle();
    }

    [UnmanagedCallersOnly]
    private static uint OnInterfaceChange(nint registration, nint context, int action, nint data, uint size)
    {
        if (action is ActionInterfaceArrival or ActionInterfaceRemoval
            && GCHandle.FromIntPtr(context).Target is GpuStateWatch watch)
            Interlocked.Increment(ref watch._changes);
        return 0;
    }

    [UnmanagedCallersOnly]
    private static uint OnCardEvent(nint registration, nint context, int action, nint data, uint size)
    {
        if (GCHandle.FromIntPtr(context).Target is not OpenCard card) return 0;
        switch (action)
        {
            case ActionQueryRemove:
                card.Watch.OnRemovalAsked(card);
                break;
            case ActionQueryRemoveFailed or ActionRemovePending or ActionRemoveComplete:
                Interlocked.Increment(ref card.Watch._changes);
                card.CloseHandle(); // opened again by Resume
                break;
        }
        return 0; // ERROR_SUCCESS: the removal may go on
    }

    public void Dispose()
    {
        var registration = Interlocked.Exchange(ref _interfaceRegistration, 0);
        if (registration == 0) return;
        CM_Unregister_Notification(registration); // waits for a callback in progress
        lock (_cards)
        {
            foreach (var card in _cards) card.Unregister();
            _cards.Clear();
        }
        _self.Free();
    }

    /// <summary>One card's display adapter interface, open so Windows asks SYSCOM before removing the card.</summary>
    private sealed class OpenCard
    {
        private nint _handle;
        private nint _registration;
        private GCHandle _self;

        private OpenCard(GpuStateWatch watch, string path) { Watch = watch; Path = path; }

        public GpuStateWatch Watch { get; }
        public string Path { get; }
        public nint Handle => Interlocked.CompareExchange(ref _handle, 0, 0);

        public static OpenCard? TryOpen(GpuStateWatch watch, string path)
        {
            // No access rights asked for: the handle is only there to be told about removals.
            var handle = CreateFileW(path, 0, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
            if (handle == -1) return null; // access denied without administrator rights
            var card = new OpenCard(watch, path) { _handle = handle };
            card._self = GCHandle.Alloc(card);
            var filter = new NotifyFilter { Size = (uint)sizeof(NotifyFilter), FilterType = FilterTypeDeviceHandle, Target = handle };
            nint registration;
            if (CM_Register_Notification(&filter, GCHandle.ToIntPtr(card._self), &OnCardEvent, &registration) != 0)
            {
                card._self.Free();
                GpuStateWatch.CloseHandle(handle);
                return null;
            }
            card._registration = registration;
            return card;
        }

        public void CloseHandle()
        {
            var handle = Interlocked.Exchange(ref _handle, 0);
            if (handle != 0) GpuStateWatch.CloseHandle(handle);
        }

        /// <summary>Stops the notifications and closes the card. Not from within a callback (that would deadlock).</summary>
        public void Unregister()
        {
            var registration = Interlocked.Exchange(ref _registration, 0);
            if (registration != 0) CM_Unregister_Notification(registration);
            CloseHandle();
            if (_self.IsAllocated) _self.Free();
        }
    }

    // ---- cfgmgr32 (Configuration Manager) and kernel32, x64 layouts

    private const uint GetIdListFilterPresent = 0x00000100;
    private const uint GetIdListFilterClass = 0x00000200;
    private const int CrBufferSmall = 0x1A;
    private const uint DnStarted = 0x00000008;
    private const uint DnHasProblem = 0x00000400;
    private const uint DnQueryRemovePending = 0x00010000; // was DN_MF_PARENT
    private const uint DnQueryRemoveActive = 0x00020000;  // was DN_MF_CHILD
    private const uint DnWillBeRemoved = 0x00040000;
    private const int FilterTypeDeviceInterface = 0;
    private const int FilterTypeDeviceHandle = 1;
    private const int ActionInterfaceArrival = 0;
    private const int ActionInterfaceRemoval = 1;
    private const int ActionQueryRemove = 2;
    private const int ActionQueryRemoveFailed = 3;
    private const int ActionRemovePending = 4;
    private const int ActionRemoveComplete = 5;
    private const uint FileShareRead = 1;
    private const uint FileShareWrite = 2;
    private const uint OpenExisting = 3;

    private static List<string> List(uint flags, string filter)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out var length, filter, flags) != 0 || length <= 1) return new();
            var buffer = new char[length];
            int result;
            fixed (char* b = buffer) result = CM_Get_Device_ID_ListW(filter, b, length, flags);
            if (result == CrBufferSmall) continue; // a device appeared in between
            return result == 0 ? Split(buffer) : new();
        }
        return new();
    }

    private static List<string> InterfaceList(Guid* cls)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_Interface_List_SizeW(out var length, cls, null, 0) != 0 || length <= 1) return new();
            var buffer = new char[length];
            int result;
            fixed (char* b = buffer) result = CM_Get_Device_Interface_ListW(cls, null, b, length, 0);
            if (result == CrBufferSmall) continue;
            return result == 0 ? Split(buffer) : new();
        }
        return new();
    }

    private static List<string> Split(char[] multiString) =>
        new string(multiString).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>CM_NOTIFY_FILTER (416 bytes: the union holds a 200-character instance ID).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 416)]
    private struct NotifyFilter
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public uint Flags;
        [FieldOffset(8)] public int FilterType;
        [FieldOffset(16)] public Guid ClassGuid;  // FilterTypeDeviceInterface
        [FieldOffset(16)] public nint Target;     // FilterTypeDeviceHandle
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CM_Get_Device_ID_List_SizeW(out uint length, string filter, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CM_Get_Device_ID_ListW(string filter, char* buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CM_Get_Device_Interface_List_SizeW(out uint length, Guid* cls, char* deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CM_Get_Device_Interface_ListW(Guid* cls, char* deviceId, char* buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CM_Locate_DevNodeW(out uint node, string instanceId, uint flags);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern int CM_Register_Notification(NotifyFilter* filter, nint context,
        delegate* unmanaged<nint, nint, int, nint, uint, uint> callback, nint* registration);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern int CM_Unregister_Notification(nint registration);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern bool CloseHandle(nint handle);
}
#endif
