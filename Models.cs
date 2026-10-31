namespace EQMobOverlay;

public readonly record struct Vec3(float X, float Y, float Z)
{
    public float Distance2D(Vec3 other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}

public sealed record EqProcessCandidate(int ProcessId, string ProcessName, string WindowTitle, long WindowHandle)
{
    public override string ToString()
    {
        var process = string.IsNullOrWhiteSpace(ProcessName) ? "process" : ProcessName.Trim();
        var title = string.IsNullOrWhiteSpace(WindowTitle) ? "(no window title)" : WindowTitle.Trim();
        return $"{title}  —  {process}  (PID {ProcessId})";
    }
}

public sealed record OverlayStatus(
    bool Attached,
    int? ProcessId,
    bool ManualSelection,
    string ConnectionText,
    string TargetText,
    string DatabaseText,
    bool OverlayEnabled,
    bool OverlayVisible,
    bool KeepVisibleWhenInactive,
    bool Locked,
    double NormalOpacityPercent,
    double HoverOpacityPercent);

public sealed record EqTargetSnapshot(
    string RawName,
    string DisplayName,
    uint ZoneId,
    ushort SpawnId,
    byte Level,
    byte PlayerLevel,
    int HpCurrent,
    int HpMax,
    Vec3 Position,
    Vec3 PlayerPosition,
    float Distance)
{
    public double HpPercent => HpMax > 0 ? Math.Clamp(HpCurrent * 100.0 / HpMax, 0.0, 100.0) : 0.0;
}

public sealed record NpcInfo(
    long Id,
    string Name,
    long MaxHp,
    int MR,
    int CR,
    int DR,
    int FR,
    int PR);
