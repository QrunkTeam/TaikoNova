namespace TaikoNova.Game.Profile;

public enum PlayerProfileKind
{
    Local,
    Online
}

public sealed class PlayerProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "Player";
    public PlayerProfileKind Kind { get; set; } = PlayerProfileKind.Local;
    public int AvatarSeed { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class LocalLeaderboardData
{
    public string ProfileId { get; set; } = "";
    public List<LocalLeaderboardEntry> Entries { get; set; } = new();
}

public sealed class LocalLeaderboardEntry
{
    public string BeatmapKey { get; set; } = "";
    public string BeatmapTitle { get; set; } = "";
    public int Score { get; set; }
    public float Accuracy { get; set; }
    public string PlayedUtc { get; set; } = "";
}
