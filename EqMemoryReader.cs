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
    private int? _preferredProcessId;

    public Process? Process => _process;
    public bool IsAttached => _process is { HasExited: false } && _handle != IntPtr.Zero;
    public bool ManualSelection => _preferredProcessId.HasValue;
    public int? PreferredProcessId => _preferredProcessId;
    public string LastAttachStatus { get; private set; } = "Not attached";
    public string LastReadStatus { get; private set; } = "No target read yet";

    public IReadOnlyList<EqProcessCandidate> GetCandidates()
    {
        var result = new List<EqProcessCandidate>();
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName("eqgame");
        }
        catch
        {
            return result;
        }

        foreach (var process in processes)
        {
            try
            {
                if (process.HasExited) continue;
                result.Add(new EqProcessCandidate(process.Id, process.MainWindowTitle));
            }
            catch { }
            finally
            {
                process.Dispose();
            }
        }

        return result.OrderBy(p => p.ProcessId).ToArray();
    }

    public bool SelectProcess(int processId)
    {
        _preferredProcessId = processId;
        Detach();
        return EnsureAttached();
    }

    public void UseAutoDetect()
    {
        _preferredProcessId = null;
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
                    LastAttachStatus = "Selected EQ process has exited";
                    return false;
                }

                if (!selected.ProcessName.Equals("eqgame", StringComparison.OrdinalIgnoreCase))
                {
                    selected.Dispose();
                    LastAttachStatus = "Selected process is not eqgame.exe";
                    return false;
                }

                var attached = TryAttach(selected, manual: true);
                if (!attached) selected.Dispose();
                return attached;
            }
            catch (ArgumentException)
            {
                LastAttachStatus = "Selected EQ process is no longer running";
                return false;
            }
            catch (Exception ex)
            {
                LastAttachStatus = $"Could not open selected EQ: {ex.Message}";
                return false;
            }
        }

        Process[] candidates;
        try
        {
            candidates = Process.GetProcessesByName("eqgame")
                .Where(p => !p.HasExited)
                .OrderByDescending(p => p.MainWindowHandle != IntPtr.Zero)
                .ToArray();
        }
        catch
        {
            LastAttachStatus = "Could not enumerate eqgame.exe";
            return false;
        }

        if (candidates.Length == 0)
        {
            LastAttachStatus = "No running eqgame.exe found";
            return false;
        }

        for (var i = 0; i < candidates.Length; i++)
        {
            var candidate = candidates[i];
            if (TryAttach(candidate, manual: false))
            {
                for (var j = i + 1; j < candidates.Length; j++) candidates[j].Dispose();
                return true;
            }
            candidate.Dispose();
        }

        return false;
    }

    private bool TryAttach(Process candidate, bool manual)
    {
        var handle = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, candidate.Id);
        if (handle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            LastAttachStatus = error == 5
                ? "Access denied opening EQ — try running the overlay as Administrator"
                : $"Could not open EQ process (Windows error {error})";
            return false;
        }

        _process = candidate;
        _handle = handle;
        LastAttachStatus = $"Attached to eqgame.exe PID {candidate.Id}" + (manual ? " (manual)" : " (auto)");
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

            // Type 2/3 are corpse-ish/non-live entities in this client. Keep players/NPCs only.
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
            LastReadStatus = "Lost access to EQ process";
            Detach();
            return null;
        }
        catch
        {
            // A zoning/client transition can invalidate pointers for a frame. Retry next tick.
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

        // EQ spawn names commonly end with a numeric uniqueness suffix (a_goblin00).
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
        var name = Encoding.ASCII.GetString(buffer, EntityName, 30).Split('\0')[0];
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr baseAddress, byte[] buffer, int size, out IntPtr bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
