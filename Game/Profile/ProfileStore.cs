using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaikoNova.Game.Profile;

public sealed class ProfileStore
{
    private sealed class ProfileStoreData
    {
        public List<PlayerProfile> Profiles { get; set; } = new();
        public string? LastProfileId { get; set; }
    }

    private static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaikoNova");
    private static readonly string ProfilesPath = Path.Combine(ConfigDir, "profiles.json");
    private static readonly string LeaderboardsDir = Path.Combine(ConfigDir, "leaderboards");
    private static readonly string AvatarsDir = Path.Combine(ConfigDir, "avatars");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private ProfileStoreData _data = new();

    public IReadOnlyList<PlayerProfile> Profiles => _data.Profiles;
    public PlayerProfile? CurrentProfile { get; private set; }
    public string ProfilesFilePath => ProfilesPath;
    public string LeaderboardsDirectory => LeaderboardsDir;
    public string AvatarsDirectory => AvatarsDir;

    public static ProfileStore Load()
    {
        var store = new ProfileStore();
        store.LoadInternal();
        return store;
    }

    public PlayerProfile CreateLocalProfile(string requestedName, int avatarSeed)
    {
        string name = SanitizeName(requestedName);
        var profile = new PlayerProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Kind = PlayerProfileKind.Local,
            AvatarSeed = avatarSeed,
            CreatedUtc = DateTime.UtcNow,
            LastUsedUtc = DateTime.UtcNow
        };

        _data.Profiles.Add(profile);
        SetActiveProfile(profile);
        EnsureLocalLeaderboardFile(profile);
        Save();
        return profile;
    }

    public PlayerProfile CreateOrUpdateOnlineProfile(OnlineAuthResult login)
    {
        string provider = SanitizeKey(login.Provider).ToLowerInvariant();
        string accountId = SanitizeKey(login.AccountId);
        string name = SanitizeName(login.Username);
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(accountId))
            throw new InvalidOperationException("Online account response did not include a provider/account id.");

        var profile = _data.Profiles.FirstOrDefault(p =>
            p.Kind == PlayerProfileKind.Online
            && string.Equals(p.OnlineProvider, provider, StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.OnlineAccountId, accountId, StringComparison.Ordinal));

        bool created = profile == null;
        profile ??= new PlayerProfile
        {
            Id = $"online-{provider}-{accountId}",
            Kind = PlayerProfileKind.Online,
            AvatarSeed = StableSeed($"{provider}:{accountId}"),
            CreatedUtc = DateTime.UtcNow
        };

        profile.Name = name;
        profile.OnlineProvider = provider;
        profile.OnlineAccountId = accountId;
        profile.AvatarUrl = login.AvatarUrl;
        profile.LastUsedUtc = DateTime.UtcNow;

        string avatarPath = SaveOnlineAvatar(provider, accountId, login.AvatarImage, login.AvatarContentType);
        if (!string.IsNullOrEmpty(avatarPath))
            profile.AvatarImagePath = avatarPath;

        if (created)
            _data.Profiles.Add(profile);

        SetActiveProfile(profile);
        Save();
        return profile;
    }

    public void SetActiveProfile(PlayerProfile profile)
    {
        CurrentProfile = profile;
        profile.LastUsedUtc = DateTime.UtcNow;
        _data.LastProfileId = profile.Id;

        if (profile.Kind == PlayerProfileKind.Local)
            EnsureLocalLeaderboardFile(profile);

        Save();
    }

    public string GetLocalLeaderboardPath(PlayerProfile profile)
        => Path.Combine(LeaderboardsDir, $"local-{profile.Id}.json");

    public LocalLeaderboardData ReadLocalLeaderboard(PlayerProfile profile)
    {
        EnsureLocalLeaderboardFile(profile);
        try
        {
            string json = File.ReadAllText(GetLocalLeaderboardPath(profile));
            var data = JsonSerializer.Deserialize<LocalLeaderboardData>(json, JsonOptions);
            return data ?? new LocalLeaderboardData { ProfileId = profile.Id };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Profiles] Failed to read local leaderboard: {ex.Message}");
            return new LocalLeaderboardData { ProfileId = profile.Id };
        }
    }

    private void LoadInternal()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            Directory.CreateDirectory(LeaderboardsDir);
            Directory.CreateDirectory(AvatarsDir);

            if (!File.Exists(ProfilesPath))
            {
                Save();
                return;
            }

            string json = File.ReadAllText(ProfilesPath);
            var loaded = JsonSerializer.Deserialize<ProfileStoreData>(json, JsonOptions);
            if (loaded != null)
                _data = loaded;

            foreach (var profile in _data.Profiles)
            {
                if (profile.Kind == PlayerProfileKind.Local)
                    EnsureLocalLeaderboardFile(profile);
            }

            _data.Profiles.Sort((a, b) => b.LastUsedUtc.CompareTo(a.LastUsedUtc));
            Console.WriteLine($"[Profiles] Loaded {_data.Profiles.Count} profile(s)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Profiles] Load failed, using empty profile store: {ex.Message}");
            _data = new ProfileStoreData();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            Directory.CreateDirectory(LeaderboardsDir);
            Directory.CreateDirectory(AvatarsDir);
            string json = JsonSerializer.Serialize(_data, JsonOptions);
            File.WriteAllText(ProfilesPath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Profiles] Save failed: {ex.Message}");
        }
    }

    private void EnsureLocalLeaderboardFile(PlayerProfile profile)
    {
        try
        {
            Directory.CreateDirectory(LeaderboardsDir);
            string path = GetLocalLeaderboardPath(profile);
            if (File.Exists(path)) return;

            var data = new LocalLeaderboardData { ProfileId = profile.Id };
            string json = JsonSerializer.Serialize(data, JsonOptions);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Profiles] Failed to create local leaderboard file: {ex.Message}");
        }
    }

    private static string SanitizeName(string requestedName)
    {
        string name = new(requestedName
            .Where(c => c >= 32 && c < 127)
            .ToArray());
        name = name.Trim();

        if (name.Length == 0)
            name = "Local Player";
        if (name.Length > 18)
            name = name[..18].Trim();
        return name;
    }

    private static string SaveOnlineAvatar(string provider, string accountId, byte[]? bytes, string contentType)
    {
        if (bytes == null || bytes.Length == 0) return "";

        try
        {
            Directory.CreateDirectory(AvatarsDir);
            string ext = contentType.Contains("jpeg", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("jpg", StringComparison.OrdinalIgnoreCase)
                    ? ".jpg"
                    : ".png";
            string fileName = $"{SanitizeKey(provider)}-{SanitizeKey(accountId)}{ext}";
            string path = Path.Combine(AvatarsDir, fileName);
            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Profiles] Failed to save online avatar: {ex.Message}");
            return "";
        }
    }

    private static string SanitizeKey(string value)
    {
        string safe = new(value
            .Where(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_' || c == '.')
            .ToArray());
        return safe.Trim();
    }

    private static int StableSeed(string value)
    {
        unchecked
        {
            uint hash = 2166136261u;
            foreach (char c in value)
            {
                hash ^= c;
                hash *= 16777619u;
            }
            return (int)hash;
        }
    }
}
