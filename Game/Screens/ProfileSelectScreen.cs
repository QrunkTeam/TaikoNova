using OpenTK.Windowing.GraphicsLibraryFramework;
using TaikoNova.Engine;
using TaikoNova.Engine.GL;
using TaikoNova.Engine.Text;
using TaikoNova.Game.Beatmap;
using TaikoNova.Game.Profile;
using TaikoNova.Game.Skin;

namespace TaikoNova.Game.Screens;

public sealed class ProfileSelectScreen : Screen
{
    private enum ProfileMode
    {
        Browse,
        AddOptions,
        OnlineProviders,
        LocalName
    }

    private ProfileMode _mode = ProfileMode.Browse;
    private int _selected;
    private float _scrollOffset;
    private float _targetScroll;
    private double _time;
    private string _localName = "";
    private int _previewSeed;
    private int _backgroundPaletteIndex;
    private string _status = "";
    private float _statusFlash;
    private readonly List<BeatmapInfo> _ambientBeatmaps = new();
    private bool _ambientScanned;
    private int _ambientCurrentIndex = -1;
    private bool _profileMusicStarted;
    private float _profileMusicVolume;
    private float _musicEnergy;
    private float _musicEnergySmooth;
    private float _lastShapedMusicEnergy;
    private float _audioFlow;
    private float _beatPulse;
    private float _submenuReveal;
    private float _creatorReveal;
    private float _submenuFlash;
    private float _optionSelectFlash;
    private Task<OnlineAuthResult>? _onlineAuthTask;
    private bool _onlineAuthBusy;
    private double _nextTrackCheck;

    private const float RowH = 76f;
    private const float RowGap = 12f;
    private const float BackgroundDrawSeconds = 5.2f;

    private static readonly (float R, float G, float B)[] BackgroundPalettes =
    {
        (1.00f, 0.18f, 0.28f),
        (0.96f, 0.28f, 0.78f),
        (0.42f, 0.58f, 1.00f),
        (1.00f, 0.48f, 0.18f),
        (0.34f, 0.86f, 0.72f),
        (0.72f, 0.34f, 1.00f),
    };

    public ProfileSelectScreen(GameEngine engine, TaikoGame game) : base(engine, game)
    {
        _previewSeed = Random.Shared.Next();
    }

    public override void OnEnter()
    {
        _mode = ProfileMode.Browse;
        _selected = 0;
        _scrollOffset = 0f;
        _targetScroll = 0f;
        _time = 0;
        _backgroundPaletteIndex = Random.Shared.Next(BackgroundPalettes.Length);
        _status = "";
        _statusFlash = 0f;
        _profileMusicVolume = 0f;
        _musicEnergy = 0f;
        _musicEnergySmooth = 0f;
        _lastShapedMusicEnergy = 0f;
        _audioFlow = 0f;
        _beatPulse = 0f;
        _submenuReveal = 0f;
        _creatorReveal = 0f;
        _submenuFlash = 0f;
        _optionSelectFlash = 0f;
        _onlineAuthTask = null;
        _onlineAuthBusy = false;
        _nextTrackCheck = 0;

        StartProfileAudio();
    }

    public override void OnExit()
    {
        Engine.Audio.StopAmbientPad();
        if (!Game.TopBarOwnsMusic)
            Engine.Audio.StopMusic();
        _profileMusicStarted = false;
    }

    public override void Update(double deltaTime)
    {
        _time += deltaTime;
        float fdt = (float)deltaTime;
        _statusFlash = MathF.Max(0f, _statusFlash - fdt * 2.4f);
        _submenuReveal = Lerp(_submenuReveal, _mode == ProfileMode.Browse ? 0f : 1f, fdt * 9.5f);
        _creatorReveal = Lerp(_creatorReveal, _mode == ProfileMode.LocalName ? 1f : 0f, fdt * 10f);
        _submenuFlash = MathF.Max(0f, _submenuFlash - fdt * 2.8f);
        _optionSelectFlash = MathF.Max(0f, _optionSelectFlash - fdt * 4.2f);
        UpdateProfileAudio(deltaTime);
        if (CompleteOnlineAuthIfReady())
            return;

        switch (_mode)
        {
            case ProfileMode.Browse:
                UpdateBrowse(deltaTime);
                break;
            case ProfileMode.AddOptions:
                UpdateAddOptions();
                break;
            case ProfileMode.OnlineProviders:
                UpdateOnlineProviders();
                break;
            case ProfileMode.LocalName:
                UpdateLocalName();
                break;
        }
    }

    private void StartProfileAudio()
    {
        float ambientVolume = 0.095f * Game.Settings.MasterVolume * Game.Settings.MusicVolume;
        Engine.Audio.StartAmbientPad(ambientVolume);

        if (Game.TopBarOwnsMusic)
            return;

        EnsureAmbientBeatmapsScanned();
        StartRandomProfileMusic();
    }

    private void UpdateProfileAudio(double deltaTime)
    {
        float fdt = (float)deltaTime;
        float rawLevel = Engine.Audio.UpdateMusicLevel(deltaTime);
        float shapedLevel = MathF.Pow(Clamp01(rawLevel * 1.35f), 0.72f);
        float rise = MathF.Max(0f, shapedLevel - _lastShapedMusicEnergy);
        if (rise > 0.030f)
            _beatPulse = MathF.Min(1f, _beatPulse + rise * 3.0f);

        _musicEnergy = Lerp(_musicEnergy, shapedLevel, fdt * 11f);
        _musicEnergySmooth = Lerp(_musicEnergySmooth, _musicEnergy, fdt * 4.5f);
        _beatPulse = MathF.Max(0f, _beatPulse - fdt * 2.45f);
        _audioFlow += fdt * (0.17f + _musicEnergySmooth * 0.78f + _beatPulse * 0.30f);
        if (_audioFlow > 1024f)
            _audioFlow -= 1024f;
        _lastShapedMusicEnergy = shapedLevel;

        float ambientVolume = 0.095f * Game.Settings.MasterVolume * Game.Settings.MusicVolume;
        Engine.Audio.SetAmbientPadVolume(ambientVolume);

        if (Game.TopBarOwnsMusic || !_profileMusicStarted)
            return;

        _profileMusicVolume = Lerp(_profileMusicVolume, 1f, fdt * 0.75f);
        Engine.Audio.SetMusicVolume(_profileMusicVolume * 0.22f
            * Game.Settings.MasterVolume * Game.Settings.MusicVolume);

        if (Engine.Audio.IsMusicLoaded && !Engine.Audio.IsPlaying && _time >= _nextTrackCheck)
        {
            _nextTrackCheck = _time + 0.45;
            StartRandomProfileMusic();
        }
    }

    private void EnsureAmbientBeatmapsScanned()
    {
        if (_ambientScanned) return;

        _ambientBeatmaps.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string[] localPaths =
        {
            Path.Combine(Environment.CurrentDirectory, "Songs"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Songs"),
        };

        foreach (string rawPath in localPaths)
        {
            string path;
            try { path = Path.GetFullPath(rawPath); }
            catch { continue; }

            if (!Directory.Exists(path)) continue;
            AddAmbientCandidates(BeatmapDecoder.ScanSongsDirectory(path), seen);
        }

        foreach (string stableSongs in OsuInstallDetector.FindStableSongsPaths())
        {
            string full;
            try { full = Path.GetFullPath(stableSongs); }
            catch { continue; }

            if (Directory.Exists(full))
                AddAmbientCandidates(BeatmapDecoder.ScanSongsDirectory(full), seen);
        }

        foreach (string osuFile in OsuInstallDetector.FindLazerOsuFiles())
        {
            if (!seen.Add(osuFile)) continue;
            try
            {
                var map = BeatmapDecoder.Decode(osuFile);
                var info = new BeatmapInfo
                {
                    FilePath = osuFile,
                    FolderPath = map.FolderPath,
                    BackgroundFilename = map.BackgroundFilename,
                    AudioFilename = map.AudioFilename,
                    PreviewTime = map.PreviewTime,
                    Title = map.Title,
                    Artist = map.Artist,
                    Version = map.Version,
                    Creator = map.Creator,
                    OD = map.OverallDifficulty
                };

                if (!string.IsNullOrWhiteSpace(info.AudioFilename))
                    _ambientBeatmaps.Add(info);
            }
            catch { }
        }

        _ambientScanned = true;
    }

    private void AddAmbientCandidates(IEnumerable<BeatmapInfo> beatmaps, HashSet<string> seen)
    {
        foreach (var bm in beatmaps)
        {
            if (string.IsNullOrWhiteSpace(bm.AudioFilename)) continue;
            if (!seen.Add(bm.FilePath)) continue;
            _ambientBeatmaps.Add(bm);
        }
    }

    private bool StartRandomProfileMusic()
    {
        if (_ambientBeatmaps.Count == 0) return false;

        int start = Random.Shared.Next(_ambientBeatmaps.Count);
        for (int offset = 0; offset < _ambientBeatmaps.Count; offset++)
        {
            int index = (start + offset) % _ambientBeatmaps.Count;
            if (_ambientBeatmaps.Count > 1 && index == _ambientCurrentIndex)
                continue;

            var bm = _ambientBeatmaps[index];
            string audioPath = ResolveAmbientAudioPath(bm);
            if (string.IsNullOrWhiteSpace(audioPath))
                continue;

            if (!Engine.Audio.LoadMusic(audioPath))
                continue;

            _ambientCurrentIndex = index;
            _profileMusicStarted = true;
            _profileMusicVolume = 0f;

            Engine.Audio.PlayMusic();
            Engine.Audio.SetMusicVolume(0f);

            double seekMs = bm.PreviewTime > 0 ? bm.PreviewTime : 0;
            if (seekMs > 0)
                Engine.Audio.SeekMusic(seekMs);
            else if (Engine.Audio.MusicDuration > 0)
                Engine.Audio.SeekMusic(Engine.Audio.MusicDuration * 0.32);

            return true;
        }

        return false;
    }

    private static string ResolveAmbientAudioPath(BeatmapInfo bm)
    {
        if (string.IsNullOrEmpty(bm.AudioFilename)) return "";
        string folder = string.IsNullOrEmpty(bm.FolderPath)
            ? (Path.GetDirectoryName(bm.FilePath) ?? "")
            : bm.FolderPath;
        string direct = Path.Combine(folder, bm.AudioFilename);
        if (File.Exists(direct)) return direct;

        var resolved = LazerAudioResolver.ResolveAudio(bm.FilePath, bm.AudioFilename);
        return resolved ?? "";
    }

    private void UpdateBrowse(double dt)
    {
        var input = Engine.Input;
        int count = Game.Profiles.Profiles.Count + 1;

        if (input.IsPressed(Keys.Up))
            _selected = Math.Max(0, _selected - 1);
        if (input.IsPressed(Keys.Down))
            _selected = Math.Min(count - 1, _selected + 1);

        if (MathF.Abs(input.ScrollDelta) > 0.01f)
            _selected = Math.Clamp(_selected - (int)MathF.Round(input.ScrollDelta * 2f), 0, count - 1);

        if (input.MousePressed && TrySelectBrowseAt(input.MouseX, input.MouseY))
            ActivateBrowseSelection();

        if (input.IsPressed(Keys.Enter) || input.IsPressed(Keys.Space))
            ActivateBrowseSelection();

        var layout = GetListLayout(Engine.ScreenWidth, Engine.ScreenHeight);
        _targetScroll = _selected * (RowH + RowGap) - layout.Height * 0.35f;
        float maxScroll = MathF.Max(0f, count * (RowH + RowGap) - layout.Height);
        _targetScroll = Math.Clamp(_targetScroll, 0f, maxScroll);
        _scrollOffset = Lerp(_scrollOffset, _targetScroll, (float)dt * 12f);
    }

    private void ActivateBrowseSelection()
    {
        if (_selected == 0)
        {
            _mode = ProfileMode.AddOptions;
            _selected = 1;
            _targetScroll = 0f;
            _scrollOffset = 0f;
            _status = "";
            _submenuFlash = 1f;
            _optionSelectFlash = 1f;
            return;
        }

        int profileIndex = _selected - 1;
        if (profileIndex < 0 || profileIndex >= Game.Profiles.Profiles.Count) return;
        Game.LoginProfile(Game.Profiles.Profiles[profileIndex]);
    }

    private void UpdateAddOptions()
    {
        var input = Engine.Input;

        if (input.IsPressed(Keys.Escape))
        {
            ReturnToBrowse();
            return;
        }

        int previous = _selected;

        if (input.IsPressed(Keys.Up))
            _selected = (_selected + 2) % 3;
        if (input.IsPressed(Keys.Down))
            _selected = (_selected + 1) % 3;

        if (_selected != previous)
            _optionSelectFlash = 1f;

        if (input.MousePressed && TrySelectOptionAt(input.MouseX, input.MouseY, 3))
        {
            _optionSelectFlash = 1f;
            ActivateAddOption();
        }

        if (input.IsPressed(Keys.Enter) || input.IsPressed(Keys.Space))
            ActivateAddOption();
    }

    private void ActivateAddOption()
    {
        switch (_selected)
        {
            case 0:
                _mode = ProfileMode.OnlineProviders;
                _selected = 0;
                _status = "";
                _submenuFlash = 1f;
                _optionSelectFlash = 1f;
                break;
            case 1:
                _mode = ProfileMode.LocalName;
                _localName = "";
                _previewSeed = Random.Shared.Next();
                _status = "";
                _creatorReveal = 0f;
                _submenuFlash = 1f;
                break;
            case 2:
                ReturnToBrowse();
                break;
        }
    }

    private void UpdateOnlineProviders()
    {
        var input = Engine.Input;

        if (input.IsPressed(Keys.Escape))
        {
            _mode = ProfileMode.AddOptions;
            _selected = 0;
            _submenuFlash = 0.75f;
            _optionSelectFlash = 1f;
            return;
        }

        if (_onlineAuthBusy)
            return;

        int previous = _selected;

        if (input.IsPressed(Keys.Up))
            _selected = (_selected + 2) % 3;
        if (input.IsPressed(Keys.Down))
            _selected = (_selected + 1) % 3;

        if (_selected != previous)
            _optionSelectFlash = 1f;

        if (input.MousePressed && TrySelectOptionAt(input.MouseX, input.MouseY, 3))
        {
            _optionSelectFlash = 1f;
            ActivateOnlineProvider();
        }

        if (input.IsPressed(Keys.Enter) || input.IsPressed(Keys.Space))
            ActivateOnlineProvider();
    }

    private void ActivateOnlineProvider()
    {
        switch (_selected)
        {
            case 0:
                StartOnlineLogin("discord");
                break;
            case 1:
                StartOnlineLogin("steam");
                break;
            case 2:
                _mode = ProfileMode.AddOptions;
                _selected = 0;
                _status = "";
                _submenuFlash = 0.75f;
                _optionSelectFlash = 1f;
                break;
        }
    }

    private void StartOnlineLogin(string provider)
    {
        if (_onlineAuthBusy)
            return;

        _onlineAuthBusy = true;
        _status = provider == "discord"
            ? "Waiting for Discord login..."
            : "Checking Steam services...";
        _statusFlash = 1f;

        _onlineAuthTask = provider == "discord"
            ? OnlineAuthClient.LoginWithDiscordAsync()
            : OnlineAuthClient.LoginWithSteamAsync();
    }

    private bool CompleteOnlineAuthIfReady()
    {
        if (_onlineAuthTask == null || !_onlineAuthTask.IsCompleted)
            return false;

        try
        {
            var login = _onlineAuthTask.GetAwaiter().GetResult();
            var profile = Game.Profiles.CreateOrUpdateOnlineProfile(login);
            Game.LoginProfile(profile);
        }
        catch (SteamUnavailableException)
        {
            _status = "Steam services are unavailable";
            _statusFlash = 1f;
            Game.Notifications.Show("Steam", _status, r: 0.85f, g: 0.32f, b: 0.26f);
        }
        catch (Exception ex)
        {
            _status = ex.Message;
            _statusFlash = 1f;
            Game.Notifications.Show("Online", _status, r: 0.85f, g: 0.32f, b: 0.26f);
        }
        finally
        {
            _onlineAuthTask = null;
            _onlineAuthBusy = false;
        }

        return true;
    }

    private void UpdateLocalName()
    {
        var input = Engine.Input;

        if (input.IsPressed(Keys.Escape))
        {
            _mode = ProfileMode.AddOptions;
            _selected = 1;
            _submenuFlash = 0.75f;
            _optionSelectFlash = 1f;
            return;
        }

        foreach (char c in input.TextInput)
        {
            if (c >= 32 && c < 127 && _localName.Length < 18)
                _localName += c;
        }

        if (input.IsPressed(Keys.Backspace) && _localName.Length > 0)
            _localName = _localName[..^1];

        if (input.IsPressed(Keys.F2))
            _previewSeed = Random.Shared.Next();

        if (input.IsPressed(Keys.Enter)
            || (input.MousePressed && IsInside(input.MouseX, input.MouseY, GetCreateButtonRect())))
        {
            var profile = Game.Profiles.CreateLocalProfile(_localName, _previewSeed);
            Game.LoginProfile(profile);
        }
    }

    private void ReturnToBrowse()
    {
        _mode = ProfileMode.Browse;
        _selected = 0;
        _status = "";
    }

    public override void Render(double deltaTime)
    {
        var batch = Engine.SpriteBatch;
        var font = Engine.Font;
        var px = Engine.PixelTex;
        int sw = Engine.ScreenWidth;
        int sh = Engine.ScreenHeight;

        batch.Begin(Engine.Projection);

        DrawContourBackground(batch, px, sw, sh);
        DrawHeader(batch, font, sw);

        switch (_mode)
        {
            case ProfileMode.Browse:
                DrawProfileList(batch, font, px, sw, sh);
                break;
            case ProfileMode.AddOptions:
                DrawProfileList(batch, font, px, sw, sh, 0);
                DrawAddOptions(batch, font, px, sw, sh);
                break;
            case ProfileMode.OnlineProviders:
                DrawProfileList(batch, font, px, sw, sh, 0);
                DrawOnlineProviders(batch, font, px, sw, sh);
                break;
            case ProfileMode.LocalName:
                DrawProfileList(batch, font, px, sw, sh, 0);
                DrawLocalCreator(batch, font, px, sw, sh);
                break;
        }

        batch.End();
    }

    private void DrawHeader(SpriteBatch batch, BitmapFont font, int sw)
    {
        float x = Math.Clamp(sw * 0.10f, 64f, 132f);
        float y = TaikoGame.GlobalTopBarHeight + 54f;

        font.DrawText(batch, "Choose a profile", x, y, 1.08f,
            0.94f, 0.95f, 1f, 0.96f);
        font.DrawText(batch, "Local profiles stay on this PC", x + 2f, y + 48f, 0.48f,
            0.58f, 0.60f, 0.68f, 0.78f);
    }

    private void DrawProfileList(SpriteBatch batch, BitmapFont font,
        Texture2D px, int sw, int sh, int selectedOverride = -1)
    {
        var layout = GetListLayout(sw, sh);
        int count = Game.Profiles.Profiles.Count + 1;
        int selectedIndex = selectedOverride >= 0 ? selectedOverride : _selected;

        for (int i = 0; i < count; i++)
        {
            float y = layout.Y + i * (RowH + RowGap) - _scrollOffset;
            if (y + RowH < layout.Y || y > layout.Y + layout.Height) continue;

            bool selected = i == selectedIndex;
            float rowX = layout.X;
            if (i == 0)
                DrawAddProfileRow(batch, font, px, rowX, y, layout.Width, selected);
            else
                DrawProfileRow(batch, font, px, Game.Profiles.Profiles[i - 1],
                    rowX, y, layout.Width, selected);
        }

        DrawScrollMarker(batch, px, layout, count);

        if (Game.Profiles.Profiles.Count == 0)
        {
            string hint = "Create a local profile to start";
            font.DrawText(batch, hint, layout.X + 3f, layout.Y + RowH + 26f, 0.44f,
                0.48f, 0.50f, 0.58f, 0.74f);
        }
    }

    private void DrawAddProfileRow(SpriteBatch batch, BitmapFont font,
        Texture2D px, float x, float y, float w, bool selected)
    {
        DrawProfileRowShell(batch, px, x, y, w, selected, SkinConfig.Accent);
        ProfileAvatarRenderer.Draw(batch, px, Engine.CircleTex, null, x + 16f, y + 12f, 52f, 0.95f);
        font.DrawText(batch, "Add profile", x + 86f, y + 18f, selected ? 0.70f : 0.62f,
            0.92f, 0.94f, 1f, selected ? 0.98f : 0.80f);
        font.DrawText(batch, "Online placeholder or local account", x + 86f, y + 47f, 0.40f,
            0.48f, 0.50f, 0.58f, 0.72f);
    }

    private void DrawProfileRow(SpriteBatch batch, BitmapFont font,
        Texture2D px, PlayerProfile profile, float x, float y, float w, bool selected)
    {
        var accent = ProfileAvatarRenderer.GetAccent(profile.AvatarSeed);
        float[] accentArray = { accent.R, accent.G, accent.B };
        DrawProfileRowShell(batch, px, x, y, w, selected, accentArray);
        ProfileAvatarRenderer.Draw(batch, px, Engine.CircleTex, profile, x + 16f, y + 12f, 52f, 0.95f);

        string name = TruncateToFit(font, profile.Name, selected ? 0.70f : 0.62f, w - 190f);
        font.DrawText(batch, name, x + 86f, y + 18f, selected ? 0.70f : 0.62f,
            0.92f, 0.94f, 1f, selected ? 0.98f : 0.82f);

        string kind = profile.Kind == PlayerProfileKind.Local ? "LOCAL ACCOUNT" : "ONLINE ACCOUNT";
        font.DrawText(batch, kind, x + 86f, y + 48f, 0.38f,
            accent.R, accent.G, accent.B, 0.82f);

        if (Game.Profiles.CurrentProfile?.Id == profile.Id)
        {
            font.DrawTextRight(batch, "ACTIVE", x + w - 18f, y + 29f, 0.36f,
                0.68f, 0.86f, 0.70f, 0.82f);
        }
    }

    private void DrawProfileRowShell(SpriteBatch batch, Texture2D px,
        float x, float y, float w, bool selected, float[] accent)
    {
        if (selected)
        {
            DrawRoundedRect(batch, px, x - 8f, y - 5f, w + 16f, RowH + 10f, 14f,
                accent[0], accent[1], accent[2], 0.14f);
        }

        DrawRoundedRect(batch, px, x, y, w, RowH, 12f,
            selected ? 0.058f : 0.034f,
            selected ? 0.060f : 0.036f,
            selected ? 0.078f : 0.048f,
            selected ? 0.88f : 0.66f);

        DrawRoundedRect(batch, px, x + 8f, y + 12f, 4f, RowH - 24f, 2f,
            accent[0], accent[1], accent[2], selected ? 0.82f : 0.34f);
        DrawRoundedRect(batch, px, x + 22f, y + RowH - 9f, w - 44f, 1f, 0.5f,
            1f, 1f, 1f, selected ? 0.08f : 0.035f);
    }

    private void DrawAddOptions(SpriteBatch batch, BitmapFont font,
        Texture2D px, int sw, int sh)
    {
        DrawAddOptionsFlyout(batch, font, px, sw, sh, 1f, 0f);
    }

    private void DrawAddOptionsFlyout(SpriteBatch batch, BitmapFont font,
        Texture2D px, int sw, int sh, float alphaMultiplier, float extraSlideX)
    {
        float reveal = SmoothStep(_submenuReveal);
        float baseAlpha = reveal * Clamp01(alphaMultiplier);
        if (baseAlpha <= 0.01f) return;

        var layout = GetOptionLayout(sw, sh);
        string[] labels = { "Online account", "Local account", "Back" };
        string[] details =
        {
            "Placeholder for later online login",
            "Name plus generated local avatar",
            "Return to profile list"
        };
        string[] values = { "Soon", "Create", "Back" };

        float slide = (1f - EaseOutCubic(_submenuReveal)) * -58f + extraSlideX;
        float x = layout.X + slide;
        float menuH = labels.Length * (RowH + RowGap) - RowGap;

        DrawSideMenuFrame(batch, font, px, x, layout.Y, layout.Width, menuH,
            "Add profile", "Local accounts are available now", SkinConfig.Accent, baseAlpha);

        for (int i = 0; i < labels.Length; i++)
        {
            float rowReveal = SmoothStep(Clamp01((_submenuReveal - i * 0.075f) / 0.72f));
            float rowAlpha = baseAlpha * rowReveal;
            if (rowAlpha <= 0.01f) continue;

            float y = layout.Y + i * (RowH + RowGap);
            float rowSlide = (1f - rowReveal) * -30f;
            bool selected = i == _selected;
            float[] accent = i == 0
                ? new[] { 0.44f, 0.62f, 1.00f }
                : (i == 1 ? SkinConfig.Accent : new[] { 0.58f, 0.60f, 0.68f });

            int glyph = i == 0 ? 3 : (i == 1 ? 4 : 2);
            DrawXmbOptionRow(batch, font, px, x + rowSlide, y, layout.Width, glyph,
                labels[i], details[i], values[i], accent, selected, rowAlpha);
        }

        if (_status.Length > 0)
        {
            float a = (0.52f + _statusFlash * 0.36f) * baseAlpha;
            font.DrawText(batch, _status, x + 2f,
                layout.Y + labels.Length * (RowH + RowGap) + 18f, 0.44f,
                0.82f, 0.62f, 0.68f, a);
        }
    }

    private void DrawLocalCreator(SpriteBatch batch, BitmapFont font,
        Texture2D px, int sw, int sh)
    {
        float optionsFade = Clamp01(1f - SmoothStep(_creatorReveal));
        DrawAddOptionsFlyout(batch, font, px, sw, sh, optionsFade, -28f * EaseOutCubic(_creatorReveal));

        float reveal = SmoothStep(_creatorReveal) * SmoothStep(_submenuReveal);
        if (reveal <= 0.01f) return;

        var layout = GetOptionLayout(sw, sh);
        float slide = (1f - EaseOutCubic(_creatorReveal)) * 58f;
        float x = layout.X + slide;
        float alpha = reveal;
        float panelH = 198f;

        DrawSideMenuFrame(batch, font, px, x, layout.Y, layout.Width, panelH,
            "Local account", "Nothing is uploaded", SkinConfig.Accent, alpha);

        float pfpSize = 116f;
        ProfileAvatarRenderer.Draw(batch, px, Engine.CircleTex,
            new PlayerProfile { Name = _localName, AvatarSeed = _previewSeed },
            x, layout.Y + 10f, pfpSize, alpha);

        float inputX = x + pfpSize + 34f;
        float inputW = layout.Width - pfpSize - 34f;
        DrawRoundedRect(batch, px, inputX, layout.Y + 18f, inputW, 52f, 8f,
            0.040f, 0.043f, 0.058f, 0.86f * alpha);
        DrawRoundedRect(batch, px, inputX + 14f, layout.Y + 66f, inputW - 28f, 2f, 1f,
            SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2], 0.52f * alpha);

        string display = _localName.Length == 0 ? "Profile name" : _localName;
        display = TruncateToFit(font, display, 0.62f, inputW - 36f);
        font.DrawText(batch, display, inputX + 18f, layout.Y + 34f, 0.62f,
            _localName.Length == 0 ? 0.42f : 0.92f,
            _localName.Length == 0 ? 0.44f : 0.94f,
            _localName.Length == 0 ? 0.52f : 1.00f,
            0.92f * alpha);

        if (((int)(_time * 2.0) & 1) == 0 && alpha > 0.72f)
        {
            float cursorX = inputX + 20f + font.MeasureWidth(display, 0.62f);
            batch.Draw(px, cursorX, layout.Y + 33f, 2f, 22f,
                1f, 1f, 1f, 0.56f * alpha);
        }

        var button = GetCreateButtonRect();
        button = (x + button.X - layout.X, button.Y, button.W, button.H);
        bool hover = IsInside(Engine.Input.MouseX, Engine.Input.MouseY, button);
        DrawRoundedRect(batch, px, button.X, button.Y, button.W, button.H, 8f,
            SkinConfig.Accent[0] * 0.16f, SkinConfig.Accent[1] * 0.16f,
            SkinConfig.Accent[2] * 0.16f, (hover ? 0.92f : 0.74f) * alpha);
        font.DrawCentered(batch, "Create local profile",
            button.X + button.W * 0.5f, button.Y + button.H * 0.5f, 0.48f,
            0.90f, 0.94f, 1f, 0.94f * alpha);

        font.DrawText(batch, "F2 randomizes the avatar", inputX + 2f, layout.Y + 92f, 0.38f,
            0.46f, 0.48f, 0.56f, 0.72f * alpha);
    }

    private void DrawOnlineProviders(SpriteBatch batch, BitmapFont font,
        Texture2D px, int sw, int sh)
    {
        float reveal = SmoothStep(_submenuReveal);
        if (reveal <= 0.01f) return;

        var layout = GetOptionLayout(sw, sh);
        string[] labels = { "Discord", "Steam", "Back" };
        string[] details =
        {
            "Browser login with Discord",
            "Use your running Steam client",
            "Return to account type"
        };
        string[] values = { "OAuth", "Steamworks", "Back" };

        float slide = (1f - EaseOutCubic(_submenuReveal)) * -58f;
        float x = layout.X + slide;
        float menuH = labels.Length * (RowH + RowGap) - RowGap;

        DrawSideMenuFrame(batch, font, px, x, layout.Y, layout.Width, menuH,
            "Online account", _onlineAuthBusy ? "Authentication in progress" : "Choose a sign-in provider",
            SkinConfig.Accent, reveal);

        for (int i = 0; i < labels.Length; i++)
        {
            float rowReveal = SmoothStep(Clamp01((_submenuReveal - i * 0.075f) / 0.72f));
            float rowAlpha = reveal * rowReveal;
            if (rowAlpha <= 0.01f) continue;

            float y = layout.Y + i * (RowH + RowGap);
            float rowSlide = (1f - rowReveal) * -30f;
            bool selected = i == _selected;
            float[] accent = i switch
            {
                0 => new[] { 0.48f, 0.57f, 1.00f },
                1 => new[] { 0.48f, 0.74f, 0.92f },
                _ => new[] { 0.58f, 0.60f, 0.68f }
            };

            int glyph = i == 0 ? 3 : (i == 1 ? 4 : 2);
            DrawXmbOptionRow(batch, font, px, x + rowSlide, y, layout.Width, glyph,
                labels[i], details[i], values[i], accent, selected, rowAlpha);
        }

        if (_status.Length > 0)
        {
            float a = (0.52f + _statusFlash * 0.36f) * reveal;
            font.DrawText(batch, _status, x + 2f,
                layout.Y + labels.Length * (RowH + RowGap) + 18f, 0.44f,
                0.82f, 0.62f, 0.68f, a);
        }
    }

    private void DrawSideMenuFrame(SpriteBatch batch, BitmapFont font, Texture2D px,
        float x, float y, float w, float h, string title, string subtitle, float[] accent, float alpha)
    {
        alpha = Clamp01(alpha);
        if (alpha <= 0f) return;

        DrawRoundedRect(batch, px, x - 14f, y - 48f, 3f, h + 68f, 1.5f,
            accent[0], accent[1], accent[2], 0.24f * alpha);

        font.DrawText(batch, title, x, y - 46f, 0.58f,
            0.92f, 0.94f, 1f, 0.88f * alpha);
        font.DrawText(batch, subtitle, x + 1f, y - 20f, 0.36f,
            0.52f, 0.54f, 0.62f, 0.68f * alpha);
    }

    private void DrawXmbOptionRow(SpriteBatch batch, BitmapFont font, Texture2D px,
        float x, float y, float w, int kind, string label, string detail, string value,
        float[] accent, bool selected, float alpha)
    {
        alpha = Clamp01(alpha);
        if (alpha <= 0f) return;

        float selectPulse = selected ? EaseOutCubic(_optionSelectFlash) : 0f;
        float entryPulse = selected ? EaseOutCubic(_submenuFlash) : 0f;
        float rowY = y - selectPulse * 2f;

        if (selected)
        {
            DrawRoundedRect(batch, px, x - 6f, rowY - 4f, w + 12f, RowH + 8f, 8f,
                accent[0], accent[1], accent[2], (0.10f + selectPulse * 0.07f + entryPulse * 0.04f) * alpha);
            DrawRoundedRect(batch, px, x, rowY, w, RowH, 8f,
                0.052f, 0.058f, 0.078f, 0.70f * alpha);
        }
        else
        {
            DrawRoundedRect(batch, px, x, rowY, w, RowH, 8f,
                0.024f, 0.028f, 0.040f, 0.26f * alpha);
        }

        DrawRoundedRect(batch, px, x + 18f, rowY + RowH - 5f, w - 36f, 1f, 0.5f,
            1f, 1f, 1f, (selected ? 0.13f : 0.050f) * alpha);
        DrawRoundedRect(batch, px, x + 8f, rowY + 12f, 4f, RowH - 24f, 2f,
            accent[0], accent[1], accent[2], (selected ? 0.82f : 0.34f) * alpha);

        DrawOptionGlyph(batch, px, x + 42f, rowY + RowH * 0.5f, kind, accent,
            (selected ? 0.95f : 0.62f) * alpha);

        float labelScale = selected ? 0.66f : 0.57f;
        string fittedLabel = TruncateToFit(font, label, labelScale, MathF.Max(110f, w - 210f));
        font.DrawText(batch, fittedLabel, x + 86f, rowY + 18f, labelScale,
            selected ? 0.96f : 0.74f,
            selected ? 0.98f : 0.76f,
            selected ? 1.00f : 0.84f,
            (selected ? 0.98f : 0.80f) * alpha);

        string fittedDetail = TruncateToFit(font, detail, 0.37f, MathF.Max(120f, w - 176f));
        font.DrawText(batch, fittedDetail, x + 86f, rowY + 48f, 0.37f,
            0.48f, 0.50f, 0.58f, 0.70f * alpha);

        float valueScale = selected ? 0.42f : 0.37f;
        string fittedValue = TruncateToFit(font, value, valueScale, 86f);
        font.DrawTextRight(batch, fittedValue, x + w - 18f, rowY + 30f, valueScale,
            selected ? 0.90f : 0.56f,
            selected ? 0.92f : 0.58f,
            selected ? 1.00f : 0.66f,
            (selected ? 0.92f : 0.58f) * alpha);
    }

    private void DrawOptionGlyph(SpriteBatch batch, Texture2D px,
        float cx, float cy, int kind, float[] accent, float alpha)
    {
        batch.Draw(Engine.CircleTex, cx - 22f, cy - 22f, 44f, 44f,
            accent[0] * 0.18f, accent[1] * 0.18f, accent[2] * 0.18f, 0.90f * alpha);

        if (kind == 0)
        {
            DrawGlobeGlyph(batch, px, cx, cy, accent, alpha);
        }
        else if (kind == 1)
        {
            DrawLocalAccountGlyph(batch, px, cx, cy, accent, alpha);
        }
        else if (kind == 3)
        {
            DrawDiscordGlyph(batch, px, cx, cy, accent, alpha);
        }
        else if (kind == 4)
        {
            DrawSteamGlyph(batch, px, cx, cy, accent, alpha);
        }
        else
        {
            DrawLine(batch, px, cx + 12f, cy, cx - 10f, cy, 3f,
                accent[0], accent[1], accent[2], alpha);
            DrawLine(batch, px, cx - 9f, cy, cx + 1f, cy - 10f, 3f,
                accent[0], accent[1], accent[2], alpha);
            DrawLine(batch, px, cx - 9f, cy, cx + 1f, cy + 10f, 3f,
                accent[0], accent[1], accent[2], alpha);
        }
    }

    private void DrawGlobeGlyph(SpriteBatch batch, Texture2D px,
        float cx, float cy, float[] accent, float alpha)
    {
        batch.Draw(Engine.CircleTex, cx - 15f, cy - 15f, 30f, 30f,
            accent[0], accent[1], accent[2], 0.92f * alpha);
        batch.Draw(Engine.CircleTex, cx - 12f, cy - 12f, 24f, 24f,
            0.018f, 0.020f, 0.028f, 0.92f * alpha);

        DrawRoundedRect(batch, px, cx - 10f, cy - 1f, 20f, 2f, 1f,
            accent[0], accent[1], accent[2], 0.88f * alpha);
        DrawRoundedRect(batch, px, cx - 7f, cy - 8f, 14f, 2f, 1f,
            accent[0], accent[1], accent[2], 0.58f * alpha);
        DrawRoundedRect(batch, px, cx - 7f, cy + 6f, 14f, 2f, 1f,
            accent[0], accent[1], accent[2], 0.58f * alpha);
        DrawRoundedRect(batch, px, cx - 1f, cy - 10f, 2f, 20f, 1f,
            accent[0], accent[1], accent[2], 0.74f * alpha);
        DrawRoundedRect(batch, px, cx - 7f, cy - 9f, 2f, 18f, 1f,
            accent[0], accent[1], accent[2], 0.40f * alpha);
        DrawRoundedRect(batch, px, cx + 5f, cy - 9f, 2f, 18f, 1f,
            accent[0], accent[1], accent[2], 0.40f * alpha);
    }

    private void DrawLocalAccountGlyph(SpriteBatch batch, Texture2D px,
        float cx, float cy, float[] accent, float alpha)
    {
        DrawRoundedRect(batch, px, cx - 16f, cy - 13f, 32f, 22f, 4f,
            accent[0], accent[1], accent[2], 0.92f * alpha);
        DrawRoundedRect(batch, px, cx - 13f, cy - 10f, 26f, 16f, 2f,
            0.018f, 0.020f, 0.028f, 0.92f * alpha);

        batch.Draw(Engine.CircleTex, cx - 4f, cy - 7f, 8f, 8f,
            0.92f, 0.94f, 1f, 0.86f * alpha);
        DrawRoundedRect(batch, px, cx - 8f, cy + 2f, 16f, 6f, 3f,
            0.92f, 0.94f, 1f, 0.70f * alpha);

        DrawRoundedRect(batch, px, cx - 3f, cy + 9f, 6f, 6f, 2f,
            accent[0], accent[1], accent[2], 0.82f * alpha);
        DrawRoundedRect(batch, px, cx - 11f, cy + 15f, 22f, 3f, 1.5f,
            accent[0], accent[1], accent[2], 0.82f * alpha);
    }

    private void DrawDiscordGlyph(SpriteBatch batch, Texture2D px,
        float cx, float cy, float[] accent, float alpha)
    {
        DrawRoundedRect(batch, px, cx - 15f, cy - 10f, 30f, 20f, 7f,
            accent[0], accent[1], accent[2], 0.92f * alpha);
        DrawRoundedRect(batch, px, cx - 10f, cy + 7f, 7f, 8f, 3f,
            accent[0], accent[1], accent[2], 0.80f * alpha);
        DrawRoundedRect(batch, px, cx + 3f, cy + 7f, 7f, 8f, 3f,
            accent[0], accent[1], accent[2], 0.80f * alpha);
        batch.Draw(Engine.CircleTex, cx - 8f, cy - 2f, 5f, 5f,
            0.018f, 0.020f, 0.028f, 0.95f * alpha);
        batch.Draw(Engine.CircleTex, cx + 3f, cy - 2f, 5f, 5f,
            0.018f, 0.020f, 0.028f, 0.95f * alpha);
    }

    private void DrawSteamGlyph(SpriteBatch batch, Texture2D px,
        float cx, float cy, float[] accent, float alpha)
    {
        batch.Draw(Engine.CircleTex, cx - 15f, cy - 15f, 30f, 30f,
            accent[0], accent[1], accent[2], 0.92f * alpha);
        batch.Draw(Engine.CircleTex, cx + 3f, cy - 11f, 13f, 13f,
            0.018f, 0.020f, 0.028f, 0.95f * alpha);
        batch.Draw(Engine.CircleTex, cx + 6f, cy - 8f, 7f, 7f,
            accent[0], accent[1], accent[2], 0.82f * alpha);
        DrawLine(batch, px, cx - 10f, cy + 8f, cx + 2f, cy + 1f, 4f,
            0.018f, 0.020f, 0.028f, 0.88f * alpha);
        batch.Draw(Engine.CircleTex, cx - 15f, cy + 3f, 12f, 12f,
            0.018f, 0.020f, 0.028f, 0.95f * alpha);
        batch.Draw(Engine.CircleTex, cx - 12f, cy + 6f, 6f, 6f,
            accent[0], accent[1], accent[2], 0.82f * alpha);
    }

    private void DrawScrollMarker(SpriteBatch batch, Texture2D px,
        (float X, float Y, float Width, float Height) layout, int count)
    {
        float contentH = count * (RowH + RowGap);
        if (contentH <= layout.Height) return;

        float trackX = layout.X + layout.Width + 16f;
        float thumbH = MathF.Max(34f, layout.Height * (layout.Height / contentH));
        float maxScroll = MathF.Max(1f, contentH - layout.Height);
        float thumbY = layout.Y + (layout.Height - thumbH) * Math.Clamp(_scrollOffset / maxScroll, 0f, 1f);
        DrawRoundedRect(batch, px, trackX, layout.Y, 4f, layout.Height, 2f,
            1f, 1f, 1f, 0.055f);
        DrawRoundedRect(batch, px, trackX - 1f, thumbY, 6f, thumbH, 3f,
            SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2], 0.52f);
    }

    private void DrawContourBackground(SpriteBatch batch, Texture2D px, int sw, int sh)
    {
        float t = (float)_time;
        var color = GetAnimatedBackgroundColor(t);
        float drawProgress = GetBackgroundDrawProgress(t);
        float energy = Clamp01(_musicEnergySmooth * 1.45f);
        float pulse = Clamp01(_beatPulse);
        float flow = _audioFlow;

        batch.Draw(px, 0, 0, sw, sh,
            0.012f + color.R * (0.006f + energy * 0.002f),
            0.013f + color.G * (0.003f + energy * 0.0015f),
            0.018f + color.B * (0.007f + energy * 0.002f),
            1f);

        DrawContentMatte(batch, px, sw, sh);

        DrawRightSideAudioWaves(batch, px, sw, sh, color, drawProgress, energy, pulse, flow);

        batch.Draw(px, 0, 0, sw, TaikoGame.GlobalTopBarHeight + 110f,
            0f, 0f, 0f, 0.20f);
        batch.Draw(px, 0, sh - 150f, sw, 150f, 0f, 0f, 0f, 0.24f);
        batch.Draw(px, 0, 0, sw, sh, 0f, 0f, 0f, 0.16f);
    }

    private void DrawContentMatte(SpriteBatch batch, Texture2D px, int sw, int sh)
    {
        var layout = GetListLayout(sw, sh);
        float laneW = MathF.Min(sw * (_mode == ProfileMode.Browse ? 0.50f : 0.48f),
            layout.X + layout.Width + (_mode == ProfileMode.Browse ? 150f : 100f));
        batch.Draw(px, 0, 0, laneW, sh, 0f, 0f, 0f,
            _mode == ProfileMode.Browse ? 0.38f : 0.34f);

        for (int i = 0; i < 14; i++)
        {
            float stripeW = 18f;
            float x = laneW + i * stripeW;
            float alpha = 0.030f * (1f - i / 14f);
            batch.Draw(px, x, 0, stripeW + 1f, sh, 0f, 0f, 0f, alpha);
        }
    }

    private void DrawRightSideAudioWaves(SpriteBatch batch, Texture2D px, int sw, int sh,
        (float R, float G, float B) baseColor, float drawProgress,
        float musicEnergy, float pulse, float flow)
    {
        const int Lines = 10;
        const int Segments = 112;

        float reveal = SmoothStep(drawProgress);
        if (reveal <= 0f) return;

        float startX = GetRightWaveStartX(sw, sh);
        float top = TaikoGame.GlobalTopBarHeight + 46f;
        float bottom = sh + 42f;
        float height = bottom - top;
        float spacing = MathF.Max(26f, (sw - startX + 90f) / Lines);

        batch.Draw(px, startX - 46f, 0, sw - startX + 46f, sh,
            baseColor.R * 0.025f, baseColor.G * 0.014f, baseColor.B * 0.030f,
            0.075f + musicEnergy * 0.035f);

        for (int line = 0; line < Lines; line++)
        {
            var tint = ShiftBackgroundColor(baseColor, 0.28f + line * 0.08f);
            float baseX = startX + line * spacing;
            float lane = line / (float)(Lines - 1);
            float baseAlpha = Lerp(0.155f, 0.070f, lane);
            float width = (line % 3 == 0 ? 2.4f : 1.15f)
                * (1f + musicEnergy * 0.85f + pulse * 0.38f);

            for (int s = 0; s < Segments; s++)
            {
                float p0 = s / (float)Segments;
                float p1 = (s + 1) / (float)Segments;
                float revealA = GetRevealAlpha(p0, drawProgress, line * 0.018f);
                if (revealA <= 0f) continue;

                float packet = TravelingPacket(p0, flow * 0.20f + line * 0.055f);
                float alpha = baseAlpha
                    * (0.72f + musicEnergy * 0.65f + packet * pulse * 1.10f)
                    * reveal * revealA;

                var a0 = RightWavePoint(baseX, top, height, p0, line, musicEnergy, pulse, flow);
                var a1 = RightWavePoint(baseX, top, height, p1, line, musicEnergy, pulse, flow);
                DrawGlowLine(batch, px, a0.X, a0.Y, a1.X, a1.Y,
                    width * (1f + packet * pulse * 1.15f),
                    tint.R, tint.G, tint.B, alpha);
            }
        }
    }

    private static (float X, float Y) RightWavePoint(float baseX, float top, float height,
        float p, int line, float musicEnergy, float pulse, float flow)
    {
        float y = top + p * height;
        float phase = flow * MathF.Tau;
        float amplitude = 22f + musicEnergy * 52f + pulse * 18f;
        float slow = MathF.Sin(p * MathF.Tau * (1.08f + line * 0.035f) - phase * 0.72f + line * 0.62f);
        float detail = MathF.Sin(p * MathF.Tau * (3.85f + line * 0.11f) - phase * 1.55f + line * 1.18f);
        float bounce = MathF.Sin(p * MathF.Tau * 7.0f + line * 0.45f) * pulse * 9f;
        float x = baseX + slow * amplitude + detail * (8f + musicEnergy * 12f) + bounce;
        return (x, y);
    }

    private float GetRightWaveStartX(int sw, int sh)
    {
        float contentEnd = sw * 0.54f;
        if (_mode == ProfileMode.Browse)
        {
            var layout = GetListLayout(sw, sh);
            contentEnd = layout.X + layout.Width + 120f;
        }
        else
        {
            var options = GetOptionLayout(sw, sh);
            contentEnd = options.X + options.Width + 90f;
        }

        float min = sw * 0.54f;
        float max = MathF.Max(min, sw - 260f);
        return Math.Clamp(contentEnd, min, max);
    }

    private static float TravelingPacket(float position, float phase)
    {
        float center = Wrap01(phase);
        float d = MathF.Abs(Wrap01(position - center + 0.5f) - 0.5f);
        return SmoothStep(Clamp01((0.155f - d) / 0.155f));
    }

    private static float Wrap01(float value)
        => value - MathF.Floor(value);

    private void DrawGlowLine(SpriteBatch batch, Texture2D px,
        float x1, float y1, float x2, float y2, float thickness,
        float r, float g, float b, float a)
    {
        DrawLine(batch, px, x1, y1, x2, y2, thickness * 2.9f,
            r, g * 0.45f, b * 0.50f, a * 0.12f);
        DrawLine(batch, px, x1, y1, x2, y2, thickness,
            r, g, b, a);
    }

    private (float R, float G, float B) GetAnimatedBackgroundColor(float t)
    {
        var color = BackgroundPalettes[Math.Clamp(_backgroundPaletteIndex, 0, BackgroundPalettes.Length - 1)];
        float fadeIn = EaseOutCubic(Clamp01(t / 0.65f));
        return LerpColor((0.16f, 0.03f, 0.05f), color, fadeIn);
    }

    private static float GetBackgroundDrawProgress(float t)
    {
        return EaseInOutCubic(Clamp01(t / BackgroundDrawSeconds));
    }

    private static float GetRevealAlpha(float position, float drawProgress, float delay)
    {
        float head = drawProgress * (1.08f + delay) - delay;
        if (position > head) return 0f;
        return SmoothStep(Clamp01((head - position) / 0.055f));
    }

    private static (float R, float G, float B) ShiftBackgroundColor(
        (float R, float G, float B) color, float offset)
    {
        return (
            Clamp01(color.R + MathF.Sin(offset * 5.1f) * 0.070f),
            Clamp01(color.G + MathF.Sin(offset * 3.7f + 1.2f) * 0.055f),
            Clamp01(color.B + MathF.Sin(offset * 4.5f + 2.1f) * 0.065f)
        );
    }

    private static (float R, float G, float B) LerpColor(
        (float R, float G, float B) a,
        (float R, float G, float B) b,
        float t)
    {
        return (
            a.R + (b.R - a.R) * t,
            a.G + (b.G - a.G) * t,
            a.B + (b.B - a.B) * t
        );
    }

    private static float SmoothStep(float t)
    {
        t = Clamp01(t);
        return t * t * (3f - 2f * t);
    }

    private static float Clamp01(float v)
        => MathF.Max(0f, MathF.Min(1f, v));

    private static float EaseOutCubic(float t)
    {
        t = Clamp01(t);
        float inv = 1f - t;
        return 1f - inv * inv * inv;
    }

    private static float EaseInOutCubic(float t)
    {
        t = Clamp01(t);
        return t < 0.5f
            ? 4f * t * t * t
            : 1f - MathF.Pow(-2f * t + 2f, 3f) * 0.5f;
    }

    private void DrawLine(SpriteBatch batch, Texture2D px,
        float x1, float y1, float x2, float y2, float thickness,
        float r, float g, float b, float a)
    {
        float dx = x2 - x1;
        float dy = y2 - y1;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.5f) return;

        float angle = MathF.Atan2(dy, dx);
        batch.Draw(px, (x1 + x2) * 0.5f - len * 0.5f, (y1 + y2) * 0.5f - thickness * 0.5f,
            len, thickness, r, g, b, a, angle);
    }

    private bool TrySelectBrowseAt(float mx, float my)
    {
        var layout = GetListLayout(Engine.ScreenWidth, Engine.ScreenHeight);
        int count = Game.Profiles.Profiles.Count + 1;

        for (int i = 0; i < count; i++)
        {
            float y = layout.Y + i * (RowH + RowGap) - _scrollOffset;
            if (!IsInside(mx, my, (layout.X, y, layout.Width, RowH))) continue;
            _selected = i;
            return true;
        }

        return false;
    }

    private bool TrySelectOptionAt(float mx, float my, int count)
    {
        var layout = GetOptionLayout(Engine.ScreenWidth, Engine.ScreenHeight);
        for (int i = 0; i < count; i++)
        {
            float y = layout.Y + i * (RowH + RowGap);
            if (!IsInside(mx, my, (layout.X, y, layout.Width, RowH))) continue;
            _selected = i;
            return true;
        }

        return false;
    }

    private (float X, float Y, float Width, float Height) GetListLayout(int sw, int sh)
    {
        float x = Math.Clamp(sw * 0.10f, 64f, 132f);
        float y = TaikoGame.GlobalTopBarHeight + 154f;
        float width = Math.Clamp(sw * 0.36f, 360f, 560f);
        float height = MathF.Max(220f, sh - y - 84f);
        return (x, y, width, height);
    }

    private (float X, float Y, float Width) GetOptionLayout(int sw, int sh)
    {
        var list = GetListLayout(sw, sh);
        float gap = Math.Clamp(sw * 0.035f, 36f, 70f);
        float rightPad = Math.Clamp(sw * 0.04f, 42f, 84f);
        float x = list.X + list.Width + gap;
        float width = Math.Clamp(sw * 0.34f, 360f, 540f);
        float available = sw - x - rightPad;
        if (available < width)
            width = MathF.Max(320f, available);
        if (width < 360f)
            x = MathF.Max(list.X + list.Width * 0.72f, sw - width - 34f);

        float y = list.Y - _scrollOffset;
        float menuH = 3f * (RowH + RowGap) - RowGap;
        float minY = TaikoGame.GlobalTopBarHeight + 118f;
        float maxY = sh - menuH - 68f;
        y = Math.Clamp(y, minY, MathF.Max(minY, maxY));
        return (x, y, width);
    }

    private (float X, float Y, float W, float H) GetCreateButtonRect()
    {
        var layout = GetOptionLayout(Engine.ScreenWidth, Engine.ScreenHeight);
        float w = MathF.Min(230f, MathF.Max(180f, layout.Width - 150f));
        return (layout.X + layout.Width - w, layout.Y + 144f, w, 42f);
    }

    private void DrawRoundedRect(SpriteBatch batch, Texture2D px,
        float x, float y, float w, float h, float radius,
        float r, float g, float b, float a)
    {
        if (a <= 0f || w <= 0f || h <= 0f) return;

        radius = MathF.Min(radius, MathF.Min(w, h) * 0.5f);
        if (radius <= 0.5f)
        {
            batch.Draw(px, x, y, w, h, r, g, b, a);
            return;
        }

        float d = radius * 2f;
        float midW = MathF.Max(0f, w - d);
        float midH = MathF.Max(0f, h - d);

        if (midW > 0f)
            batch.Draw(px, x + radius, y, midW, h, r, g, b, a);
        if (midH > 0f)
        {
            batch.Draw(px, x, y + radius, radius, midH, r, g, b, a);
            batch.Draw(px, x + w - radius, y + radius, radius, midH, r, g, b, a);
        }

        batch.Draw(Engine.CircleTex, x, y, d, d, r, g, b, a);
        batch.Draw(Engine.CircleTex, x + w - d, y, d, d, r, g, b, a);
        batch.Draw(Engine.CircleTex, x, y + h - d, d, d, r, g, b, a);
        batch.Draw(Engine.CircleTex, x + w - d, y + h - d, d, d, r, g, b, a);
    }

    private static bool IsInside(float mx, float my, (float X, float Y, float W, float H) rect)
        => mx >= rect.X && mx <= rect.X + rect.W && my >= rect.Y && my <= rect.Y + rect.H;

    private static string TruncateToFit(BitmapFont font, string text, float scale, float maxWidth)
    {
        if (font.MeasureWidth(text, scale) <= maxWidth) return text;
        for (int len = text.Length - 1; len > 0; len--)
        {
            string truncated = text[..len] + "..";
            if (font.MeasureWidth(truncated, scale) <= maxWidth)
                return truncated;
        }
        return "..";
    }

    private static float Lerp(float a, float b, float t)
        => a + (b - a) * MathF.Min(1f, t);
}
