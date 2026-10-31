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

public sealed record EqTargetSnapshot(
    string RawName,
    string DisplayName,
    uint ZoneId,
    ushort SpawnId,
    byte Level,
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
