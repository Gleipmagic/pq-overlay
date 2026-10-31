using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace EQMobOverlay;

public sealed class EqMemoryReader : IDisposable
{
    // Addresses and Entity offsets correspond to the current Project Quarm client layout used by Zeal.
    private const uint SelfPointerAddress = 0x007F94CC;
    private const uint TargetPointerAddress = 0x007F94EC;

    private const int EntityName = 0x0001;
    private const int EntityZoneId = 0x0044;
    private const int EntityPosition = 0x0048;
    private const int EntitySpawnId = 0x0094;
    private const int EntityHpMax = 0x0098;
    private const int EntityHpCurrent = 0x009C;
    private const int EntityType = 0x00A8;
    private const int EntityLevel = 0x00AD;
    private const int EntityReadSize = 0x00B0;

    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;

    private Process? _process;
    private IntPtr _handle;
    private IntPtr _windowHandle;
    private int? _preferredProcessId;
    private long _preferredWindowHandle;

    public Process? Process => _process;
    public IntPtr WindowHandle => _windowHandle != IntPtr.Zero ? _windowHandle : (_process?.MainWindowHandle ?? IntPtr.Zero);
    public bool IsAttached => _process is { HasExited: false } && _handle != IntPtr.Zero;
    public bool ManualSelection => _preferredProcessId.HasValue;
    public int? PreferredProcessId => _preferredProcessId;
    public string LastAttachStatus { get; private set; } = "Not attached";
    public string LastReadStatus { get; private set; } = "No target read yet";

    // Manual selection intentionally lists every visible top-level application window.
    // Some Quarm launchers/wrappers do not expose the client to .NET as a process named exactly "eqgame".
    public IReadOnlyList<EqProcessCandidate> GetCandidates()
    {
        var result = new List<EqProcessCandidate>();
        var seen = new HashSet<(int, long)>();
        var ownPid = Environment.ProcessId;

        EnumWindows((hwnd, _) =>
        {
            try
            {
                if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd)) return true;
                var titleLength = GetWindowTextLength(hwnd);
                var titleBuilder = new StringBuilder(Math.Max(1, titleLength + 1));
                GetWindowText(hwnd, titleBuilder, titleBuilder.Capacity);
                var title = titleBuilder.ToString().Trim();
                if (string.IsNullOrWhiteSpace(title)) title = "(untitled window)";

                GetWindowThreadProcessId(hwnd, out var rawPid);
                var pid = unchecked((int)rawPid);
                if (pid <= 0 || pid == ownPid) return true;

                using var process = Process.GetProcessById(pid);
                if (process.HasExited) return true;
                var key = (pid, hwnd.ToInt64());
                if (!seen.Add(key)) return true;

                string processName;
                try { processName = process.ProcessName; }
                catch { processName = "unknown"; }

                result.Add(new EqProcessCandidate(pid, processName, title, hwnd.ToInt64()));
            }
            catch
            {
                // Some protected/system windows cannot be inspected. Ignore them.
            }
            return true;
        }, IntPtr.Zero);

        return result
            .OrderByDescending(CandidateScore)
            .ThenBy(c => c.WindowTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.ProcessId)
            .ToArray();
    }

    public bool SelectProcess(EqProcessCandidate candidate)
    {
        _preferredProcessId = candidate.ProcessId;
        _preferredWindowHandle = candidate.WindowHandle;
        Detach();
        return EnsureAttached();
    }

    public bool SelectProcessByPid(int processId)
    {
        _preferredProcessId = processId;
        _preferredWindowHandle = FindBestWindowForProcess(processId).ToInt64();
        Detach();
        LastAttachStatus = $"Manual PID selected: {processId}";
        return EnsureAttached();
    }

    public void UseAutoDetect()
    {
        _preferredProcessId = null;
        _preferredWindowHandle = 0;
        Detach();
        LastAttachStatus = "Auto detect enabled";
    }

    public bool EnsureAttached()
    {
        if (IsAttached)
        {
            if (!_preferredProcessId.HasValue || _process!.Id == _preferredProcessId.Value)
                return true;
            Detach();
        }

        if (_preferredProcessId.HasValue)
        {
            try
            {
                var selected = Process.GetProcessById(_preferredProcessId.Value);
                if (selected.HasExited)
                {
                    selected.Dispose();
                    LastAttachStatus = "Selected process has exited";
                    return false;
                }

                var selectedWindow = new IntPtr(_preferredWindowHandle);
                if (selectedWindow == IntPtr.Zero) selectedWindow = FindBestWindowForProcess(selected.Id);
                var attached = TryAttach(selected, manual: true, selectedWindow);
                if (!attached) selected.Dispose();
                return attached;
            }
            catch (ArgumentException)
            {
                LastAttachStatus = "Selected process is no longer running";
                return false;
            }
            catch (Exception ex)
            {
                LastAttachStatus = $"Could not open selected process: {ex.Message}";
                return false;
            }
        }

        // First try visible windows that strongly resemble EverQuest/Project Quarm.
        var likely = GetCandidates().Where(c => CandidateScore(c) > 0).ToArray();
        foreach (var candidateInfo in likely)
        {
            try
            {
                var candidate = Process.GetProcessById(candidateInfo.ProcessId);
                if (candidate.HasExited)
                {
                    candidate.Dispose();
                    continue;
                }
                if (TryAttach(candidate, manual: false, new IntPtr(candidateInfo.WindowHandle))) return true;
                candidate.Dispose();
            }
            catch { }
        }

        // Broader process fallback. Some Quarm setups have unusual/untitled windows, so score
        // every running process by process name and any main-window title before giving up.
        Process[] allProcesses;
        try
        {
            allProcesses = Process.GetProcesses();
        }
        catch
        {
            LastAttachStatus = "Could not enumerate running processes";
            return false;
        }

        var fallback = new List<(Process Process, int Score, IntPtr Window)>();
        foreach (var candidate in allProcesses)
        {
            try
            {
                if (candidate.Id == Environment.ProcessId || candidate.HasExited)
                {
                    candidate.Dispose();
                    continue;
                }

                var name = candidate.ProcessName ?? string.Empty;
                string title;
                try { title = candidate.MainWindowTitle ?? string.Empty; } catch { title = string.Empty; }
                var score = 0;
                if (name.Equals("eqgame", StringComparison.OrdinalIgnoreCase)) score += 100;
                else if (name.Contains("eq", StringComparison.OrdinalIgnoreCase)) score += 20;
                if (name.Contains("quarm", StringComparison.OrdinalIgnoreCase)) score += 80;
                if (title.Contains("EverQuest", StringComparison.OrdinalIgnoreCase)) score += 90;
                if (title.Contains("Quarm", StringComparison.OrdinalIgnoreCase)) score += 80;

                if (score > 0)
                    fallback.Add((candidate, score, FindBestWindowForProcess(candidate.Id)));
                else
                    candidate.Dispose();
            }
            catch
            {
                candidate.Dispose();
            }
        }

        foreach (var item in fallback.OrderByDescending(x => x.Score))
        {
            if (TryAttach(item.Process, manual: false, item.Window))
            {
                foreach (var other in fallback)
                    if (!ReferenceEquals(other.Process, item.Process)) other.Process.Dispose();
                return true;
            }
            item.Process.Dispose();
        }

        LastAttachStatus = "No EQ client auto-detected — select a window or enter the PID manually";
        return false;
    }

    private static IntPtr FindBestWindowForProcess(int processId)
    {
        IntPtr first = IntPtr.Zero;
        IntPtr visible = IntPtr.Zero;
        IntPtr titledVisible = IntPtr.Zero;

        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var rawPid);
            if (unchecked((int)rawPid) != processId) return true;

            if (first == IntPtr.Zero) first = hwnd;
            if (IsWindowVisible(hwnd) && visible == IntPtr.Zero) visible = hwnd;
            if (IsWindowVisible(hwnd) && GetWindowTextLength(hwnd) > 0 && titledVisible == IntPtr.Zero)
                titledVisible = hwnd;
            return true;
        }, IntPtr.Zero);

        if (titledVisible != IntPtr.Zero) return titledVisible;
        if (visible != IntPtr.Zero) return visible;
        return first;
    }

    private static int CandidateScore(EqProcessCandidate candidate)
    {
        var process = candidate.ProcessName ?? string.Empty;
        var title = candidate.WindowTitle ?? string.Empty;
        var score = 0;
        if (process.Equals("eqgame", StringComparison.OrdinalIgnoreCase)) score += 100;
        else if (process.Contains("eqgame", StringComparison.OrdinalIgnoreCase)) score += 80;
        if (title.Contains("EverQuest", StringComparison.OrdinalIgnoreCase)) score += 90;
        if (title.Contains("Project Quarm", StringComparison.OrdinalIgnoreCase)) score += 80;
        else if (title.Contains("Quarm", StringComparison.OrdinalIgnoreCase)) score += 60;
        return score;
    }

    private bool TryAttach(Process candidate, bool manual, IntPtr selectedWindow)
    {
        var handle = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, candidate.Id);
        if (handle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            LastAttachStatus = error == 5
                ? "Access denied opening selected process — try running the overlay as Administrator"
                : $"Could not open selected process (Windows error {error})";
            return false;
        }

        _process = candidate;
        _handle = handle;
        _windowHandle = selectedWindow != IntPtr.Zero ? selectedWindow : candidate.MainWindowHandle;
        string processName;
        try { processName = candidate.ProcessName; } catch { processName = "process"; }
        LastAttachStatus = $"Attached to {processName} PID {candidate.Id}" + (manual ? " (manual)" : " (auto)");
        return true;
    }

    public EqTargetSnapshot? ReadTarget()
    {
        if (!EnsureAttached()) return null;

        try
        {
            uint selfPtr = ReadUInt32(SelfPointerAddress);
            uint targetPtr = ReadUInt32(TargetPointerAddress);
            if (selfPtr == 0)
            {
                LastReadStatus = "Player pointer unavailable";
                return null;
            }
            if (targetPtr == 0)
            {
                LastReadStatus = "No target selected";
                return null;
            }

            var self = ReadEntity(selfPtr);
            var target = ReadEntity(targetPtr);
            if (self is null || target is null || target.Value.StructType != 0x03)
            {
                LastReadStatus = "Target pointer could not be decoded";
                return null;
            }

            if (target.Value.Type > 1)
            {
                LastReadStatus = "Target is not a live NPC/player";
                return null;
            }

            var cleaned = CleanEqName(target.Value.Name);
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                LastReadStatus = "Target name was empty";
                return null;
            }

            LastReadStatus = $"Reading target: {cleaned}";
            return new EqTargetSnapshot(
                target.Value.Name,
                cleaned,
                target.Value.ZoneId,
                target.Value.SpawnId,
                target.Value.Level,
                target.Value.HpCurrent,
                target.Value.HpMax,
                target.Value.Position,
                self.Value.Position,
                self.Value.Position.Distance2D(target.Value.Position));
        }
        catch (Win32Exception)
        {
            LastReadStatus = "Lost access to selected process";
            Detach();
            return null;
        }
        catch
        {
            LastReadStatus = "Target read failed; retrying";
            return null;
        }
    }

    public static string NormalizeNpcName(string name)
    {
        var value = CleanEqName(name).TrimStart('#').Replace('_', ' ').Trim();
        return string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    }

    public static string CleanEqName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var name = raw.TrimEnd('\0', ' ');
        var end = name.Length;
        while (end > 0 && char.IsDigit(name[end - 1])) end--;
        name = name[..end];
        return name.Replace('_', ' ').Trim();
    }

    private EntityData? ReadEntity(uint address)
    {
        var buffer = ReadBytes(address, EntityReadSize);
        if (buffer.Length != EntityReadSize) return null;

        byte structType = buffer[0];
        var name = Encoding.ASCII.GetString(buffer, EntityName, 64).Split('\0')[0];
        uint zoneId = BitConverter.ToUInt32(buffer, EntityZoneId);
        float x = BitConverter.ToSingle(buffer, EntityPosition + 0);
        float y = BitConverter.ToSingle(buffer, EntityPosition + 4);
        float z = BitConverter.ToSingle(buffer, EntityPosition + 8);
        ushort spawnId = BitConverter.ToUInt16(buffer, EntitySpawnId);
        int hpMax = BitConverter.ToInt32(buffer, EntityHpMax);
        int hpCurrent = BitConverter.ToInt32(buffer, EntityHpCurrent);
        byte type = buffer[EntityType];
        byte level = buffer[EntityLevel];

        return new EntityData(structType, name, zoneId, new Vec3(x, y, z), spawnId, hpMax, hpCurrent, type, level);
    }

    private uint ReadUInt32(uint address)
    {
        var bytes = ReadBytes(address, 4);
        return BitConverter.ToUInt32(bytes, 0);
    }

    private byte[] ReadBytes(uint address, int count)
    {
        if (_handle == IntPtr.Zero) throw new Win32Exception();
        var buffer = new byte[count];
        if (!ReadProcessMemory(_handle, new IntPtr(unchecked((int)address)), buffer, count, out var read) || read.ToInt64() != count)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return buffer;
    }

    private void Detach()
    {
        if (_handle != IntPtr.Zero)
        {
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
        _process?.Dispose();
        _process = null;
        _windowHandle = IntPtr.Zero;
    }

    public void Dispose() => Detach();

    private readonly record struct EntityData(
        byte StructType,
        string Name,
        uint ZoneId,
        Vec3 Position,
        ushort SpawnId,
        int HpMax,
        int HpCurrent,
        byte Type,
        byte Level);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr baseAddress, byte[] buffer, int size, out IntPtr bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
