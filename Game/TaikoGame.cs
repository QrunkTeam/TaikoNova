using OpenTK.Windowing.Common;
using TaikoNova.Engine;
using TaikoNova.Engine.GL;
using TaikoNova.Engine.Text;
using TaikoNova.Game.Beatmap;
using TaikoNova.Game.Profile;
using TaikoNova.Game.Screens;
using TaikoNova.Game.Settings;
using TaikoNova.Game.Skin;
using TaikoNova.Game.Taiko;

namespace TaikoNova.Game;

/// <summary>
/// Top-level game coordinator. Manages screen transitions
/// and shared game state.
/// </summary>
public sealed class TaikoGame : IDisposable
{
    public const float GlobalTopBarHeight = 36f;

    private readonly GameEngine _engine;
    private readonly GameLaunchOptions _launchOptions;

    // ── Settings ──
    public SettingsManager Settings { get; }
    public ProfileStore Profiles { get; }
    public SettingsOverlay SettingsOverlay { get; }
    public NotificationOverlay Notifications { get; }
    public bool AutoPlay => _launchOptions.AutoPlay;
    public bool LevelSelectGridLayout { get; private set; }
    public bool TopBarOwnsMusic { get; private set; }

    // ── Screens ──
    private readonly ProfileSelectScreen _profileSelect;
    private readonly MainMenuScreen _mainMenu;
    private readonly SongSelectScreen _songSelect;
    private readonly GameplayScreen _gameplay;
    private readonly ResultScreen _results;
    private readonly LoadingScreen _loading;
    private Screen _currentScreen;

    // Global topbar controls
    private bool _musicMenuOpen;
    private bool _musicBeatmapsScanned;
    private readonly List<BeatmapInfo> _musicBeatmaps = new();
    private int _musicTopIndex;
    private int _musicCurrentIndex = -1;
    private string _musicStatus = "Pick a song";
    private double _topBarTime;

    // ── Pending load (set during loading screen) ──
    private BeatmapData? _pendingBeatmap;
    private bool _pendingWithAudio;

    // ── Screen transition overlay ──
    private enum TransitionState { None, FadingOut, FadingIn }
    private TransitionState _transState = TransitionState.None;
    private float _transAlpha;          // 0→1 overlay opacity
    private Action? _transAction;       // runs at the midpoint (screen switch)
    private const float TransFadeSpeed = 5.0f;

    public TaikoGame(GameEngine engine, GameLaunchOptions? launchOptions = null)
    {
        _engine = engine;
        _launchOptions = launchOptions ?? GameLaunchOptions.Default;

        // Load settings first so screens can use them
        Settings = SettingsManager.Load();
        Profiles = ProfileStore.Load();
        SettingsOverlay = new SettingsOverlay(engine, Settings, () => ApplySettings());
        Notifications = new NotificationOverlay(engine);

        _profileSelect = new ProfileSelectScreen(engine, this);
        _mainMenu = new MainMenuScreen(engine, this);
        _songSelect = new SongSelectScreen(engine, this);
        _gameplay = new GameplayScreen(engine, this);
        _results = new ResultScreen(engine, this);
        _loading = new LoadingScreen(engine, this);

        // Apply loaded settings to engine
        ApplySettings();

        // Start on profile selection
        _currentScreen = _profileSelect;
        _currentScreen.OnEnter();

        Console.WriteLine("[Game] TaikoGame initialized — starting on Main Menu");
        Console.WriteLine("[Game] Controls: D/F = Don (center), J/K = Kat (rim)");
        Console.WriteLine("[Game] Auto-detects osu! stable & lazer installations.");
        Console.WriteLine("[Game] You can also drop .osz/.osu files into a 'Songs' folder.");
        if (AutoPlay)
            Console.WriteLine("[Game] AutoPlay enabled via launch option.");
    }

    public void Update(double deltaTime)
    {
        bool topBarConsumed = UpdateGlobalTopBar(deltaTime);

        // ── Settings overlay (takes priority) ──
        if (SettingsOverlay.IsOpen)
        {
            if (!topBarConsumed)
                SettingsOverlay.Update(deltaTime);
            Notifications.Update(deltaTime);
            return;
        }

        // ── Transition overlay ──
        float fdt = (float)deltaTime;
        switch (_transState)
        {
            case TransitionState.FadingOut:
                _transAlpha = MathF.Min(1f, _transAlpha + fdt * TransFadeSpeed);
                if (_transAlpha >= 1f)
                {
                    _transAction?.Invoke();
                    _transAction = null;
                    _transState = TransitionState.FadingIn;
                }
                break;
            case TransitionState.FadingIn:
                _transAlpha = MathF.Max(0f, _transAlpha - fdt * TransFadeSpeed);
                if (_transAlpha <= 0f)
                    _transState = TransitionState.None;
                break;
        }

        if (!topBarConsumed)
            _currentScreen.Update(deltaTime);
        Notifications.Update(deltaTime);
    }

    public void Render(double deltaTime)
    {
        _currentScreen.Render(deltaTime);

        // ── Settings overlay on top of everything ──
        if (SettingsOverlay.IsOpen)
            SettingsOverlay.Render(deltaTime);

        RenderGlobalTopBar();

        // ── Notifications (always on top) ──
        Notifications.Render(deltaTime);

        // ── Draw transition overlay on top ──
        if (_transAlpha > 0.005f)
        {
            var batch = _engine.SpriteBatch;
            batch.Begin(_engine.Projection);
            batch.Draw(_engine.PixelTex, 0, 0,
                _engine.ScreenWidth, _engine.ScreenHeight,
                0f, 0f, 0f, _transAlpha);
            batch.End();
        }
    }

    private bool UpdateGlobalTopBar(double deltaTime)
    {
        _topBarTime += deltaTime;

        var input = _engine.Input;
        bool consumed = false;

        if (_musicMenuOpen && MathF.Abs(input.ScrollDelta) > 0.01f
            && IsInside(input.MouseX, input.MouseY, GetMusicPopupRect()))
        {
            EnsureMusicBeatmapsScanned();
            int maxTop = Math.Max(0, _musicBeatmaps.Count - GetVisibleMusicRows());
            _musicTopIndex = Math.Clamp(_musicTopIndex - (int)MathF.Round(input.ScrollDelta * 2f), 0, maxTop);
            consumed = true;
        }

        if (!input.MousePressed)
            return consumed;

        if (IsInside(input.MouseX, input.MouseY, GetProfileChipRect(_engine.Font)))
        {
            _musicMenuOpen = false;
            if (SettingsOverlay.IsOpen)
                SettingsOverlay.Close();
            if (!ReferenceEquals(_currentScreen, _profileSelect))
                GoToProfileSelect();
            return true;
        }

        if (TryGetTopBarButton(input.MouseX, input.MouseY, out int button))
        {
            switch (button)
            {
                case 0:
                    _musicMenuOpen = false;
                    if (SettingsOverlay.IsOpen)
                        SettingsOverlay.Close();
                    else
                        OpenSettings();
                    break;
                case 1:
                    _musicMenuOpen = !_musicMenuOpen;
                    if (_musicMenuOpen)
                        EnsureMusicBeatmapsScanned();
                    break;
                case 2:
                    _musicMenuOpen = false;
                    ToggleLevelSelectLayout();
                    break;
            }

            return true;
        }

        if (_musicMenuOpen)
        {
            if (TryHandleMusicPopupClick(input.MouseX, input.MouseY))
                return true;

            if (input.MouseY > GlobalTopBarHeight)
            {
                _musicMenuOpen = false;
                return true;
            }
        }

        return input.MouseY <= GlobalTopBarHeight;
    }

    private void RenderGlobalTopBar()
    {
        var batch = _engine.SpriteBatch;
        var font = _engine.Font;
        var px = _engine.PixelTex;
        int sw = _engine.ScreenWidth;
        int sh = _engine.ScreenHeight;
        int hoverButton = GetTopBarHoverButton(_engine.Input.MouseX, _engine.Input.MouseY);

        batch.Begin(_engine.Projection);

        batch.Draw(px, 0, 0, sw, GlobalTopBarHeight,
            0.018f, 0.020f, 0.028f, 0.86f);
        batch.Draw(px, 0, GlobalTopBarHeight - 1f, sw, 1f,
            1f, 1f, 1f, 0.075f);

        DrawTopBarButton(batch, 12f, 0, SettingsOverlay.IsOpen, hoverButton == 0);
        DrawTopBarButton(batch, 44f, 1, _musicMenuOpen, hoverButton == 1);
        DrawTopBarButton(batch, 76f, 2, LevelSelectGridLayout, hoverButton == 2);

        string brand = "TaikoNova";
        font.DrawText(batch, brand, 120f, 11f, 0.54f,
            0.86f, 0.88f, 0.94f, 0.92f);

        string section = GetCurrentSectionName();
        float sectionW = font.MeasureWidth(section, 0.46f);
        font.DrawText(batch, section, (sw - sectionW) * 0.5f, 12f, 0.46f,
            0.50f, 0.52f, 0.58f, 0.82f);

        var profileChip = GetProfileChipRect(font);
        string status = SettingsOverlay.IsOpen ? "SETTINGS" : (AutoPlay ? "AUTO" : "READY");
        float statusScale = 0.42f;
        float statusW = font.MeasureWidth(status, statusScale);
        float statusX = profileChip.X - statusW - 38f;
        DrawRoundedRect(batch, statusX, 8f, statusW + 28f, 20f, 10f,
            AutoPlay ? SkinConfig.Accent[0] : 0.055f,
            AutoPlay ? SkinConfig.Accent[1] : 0.058f,
            AutoPlay ? SkinConfig.Accent[2] : 0.072f,
            AutoPlay ? 0.18f : 0.58f);
        font.DrawText(batch, status, statusX + 14f, 14f, statusScale,
            AutoPlay ? 0.95f : 0.62f,
            AutoPlay ? 0.78f : 0.64f,
            AutoPlay ? 0.72f : 0.70f,
            0.88f);
        DrawProfileChip(batch, font, profileChip);

        if (_musicMenuOpen)
            RenderMusicPopup(batch, font, sw, sh);
        else if (hoverButton >= 0)
            RenderTopBarTooltip(batch, font, hoverButton);

        batch.End();
    }

    public void ToggleLevelSelectLayout()
    {
        LevelSelectGridLayout = !LevelSelectGridLayout;
        Notifications.Show("Layout",
            LevelSelectGridLayout ? "Grid view enabled" : "Focus view enabled",
            r: SkinConfig.Accent[0], g: SkinConfig.Accent[1], b: SkinConfig.Accent[2]);
    }

    public void ReleaseTopBarMusicControl()
    {
        TopBarOwnsMusic = false;
    }

    private void DrawTopBarButton(SpriteBatch batch, float x, int icon,
        bool active, bool hover)
    {
        float a = active ? 0.84f : (hover ? 0.64f : 0.36f);
        DrawRoundedRect(batch, x, 6f, 24f, 24f, 8f,
            active ? SkinConfig.Accent[0] * 0.18f : 0.055f,
            active ? SkinConfig.Accent[1] * 0.18f : 0.058f,
            active ? SkinConfig.Accent[2] * 0.18f : 0.074f,
            a);

        if (active)
        {
            DrawRoundedRect(batch, x + 7f, 27f, 10f, 2f, 1f,
                SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2], 0.78f);
        }

        float r = active ? 0.94f : (hover ? 0.82f : 0.58f);
        float g = active ? 0.96f : (hover ? 0.84f : 0.60f);
        float b = active ? 1.00f : (hover ? 0.90f : 0.68f);
        DrawTopBarIcon(batch, icon, x + 12f, 18f, r, g, b, 0.92f);
    }

    private void DrawTopBarIcon(SpriteBatch batch, int icon, float cx, float cy,
        float r, float g, float b, float a)
    {
        switch (icon)
        {
            case 0:
                DrawRoundedRect(batch, cx - 1f, cy - 9f, 2f, 4f, 1f, r, g, b, a);
                DrawRoundedRect(batch, cx - 1f, cy + 5f, 2f, 4f, 1f, r, g, b, a);
                DrawRoundedRect(batch, cx - 9f, cy - 1f, 4f, 2f, 1f, r, g, b, a);
                DrawRoundedRect(batch, cx + 5f, cy - 1f, 4f, 2f, 1f, r, g, b, a);
                batch.Draw(_engine.CircleTex, cx - 6f, cy - 6f, 12f, 12f, r, g, b, a);
                batch.Draw(_engine.CircleTex, cx - 2.5f, cy - 2.5f, 5f, 5f,
                    0.055f, 0.058f, 0.074f, 0.95f);
                break;
            case 1:
                DrawRoundedRect(batch, cx + 1f, cy - 8f, 2f, 14f, 1f, r, g, b, a);
                DrawRoundedRect(batch, cx + 3f, cy - 8f, 7f, 2f, 1f, r, g, b, a);
                batch.Draw(_engine.CircleTex, cx - 7f, cy + 2f, 9f, 9f, r, g, b, a);
                break;
            case 2:
                for (int row = 0; row < 2; row++)
                {
                    for (int col = 0; col < 2; col++)
                    {
                        DrawRoundedRect(batch, cx - 7f + col * 8f, cy - 7f + row * 8f,
                            6f, 6f, 2f, r, g, b, a);
                    }
                }
                break;
        }
    }

    private void RenderTopBarTooltip(SpriteBatch batch, BitmapFont font, int button)
    {
        string text = button switch
        {
            0 => "Settings",
            1 => "Music",
            2 => LevelSelectGridLayout ? "Focus layout" : "Grid layout",
            _ => ""
        };
        if (text.Length == 0) return;

        float x = 12f + button * 32f;
        float y = GlobalTopBarHeight + 8f;
        float w = font.MeasureWidth(text, 0.40f) + 22f;
        DrawRoundedRect(batch, x, y, w, 24f, 8f,
            0.025f, 0.027f, 0.036f, 0.92f);
        font.DrawText(batch, text, x + 11f, y + 8f, 0.40f,
            0.70f, 0.72f, 0.80f, 0.92f);
    }

    private (float X, float Y, float W, float H) GetProfileChipRect(BitmapFont font)
    {
        string name = Profiles.CurrentProfile?.Name ?? "Choose profile";
        float nameW = MathF.Min(150f, font.MeasureWidth(name, 0.42f));
        float w = Math.Clamp(nameW + 52f, 136f, 220f);
        return (_engine.ScreenWidth - w - 14f, 5f, w, 26f);
    }

    private void DrawProfileChip(SpriteBatch batch, BitmapFont font,
        (float X, float Y, float W, float H) rect)
    {
        var profile = Profiles.CurrentProfile;
        bool hasProfile = profile != null;
        float hover = IsInside(_engine.Input.MouseX, _engine.Input.MouseY, rect) ? 1f : 0f;

        DrawRoundedRect(batch, rect.X, rect.Y, rect.W, rect.H, 13f,
            hasProfile ? 0.048f : 0.060f,
            hasProfile ? 0.052f : 0.052f,
            hasProfile ? 0.070f : 0.058f,
            0.70f + hover * 0.12f);

        ProfileAvatarRenderer.Draw(batch, _engine.PixelTex, _engine.CircleTex,
            profile, rect.X + 4f, rect.Y + 2f, 22f, 0.95f);

        string label = profile?.Name ?? "Choose profile";
        label = TruncateToFit(font, label, 0.42f, rect.W - 40f);
        font.DrawText(batch, label, rect.X + 32f, rect.Y + 9f, 0.42f,
            hasProfile ? 0.82f : 0.62f,
            hasProfile ? 0.84f : 0.64f,
            hasProfile ? 0.92f : 0.72f,
            0.92f);
    }

    private void RenderMusicPopup(SpriteBatch batch, BitmapFont font, int sw, int sh)
    {
        var rect = GetMusicPopupRect();
        float x = rect.X;
        float y = rect.Y;
        float w = rect.W;
        float h = MathF.Min(rect.H, sh - rect.Y - 12f);

        DrawRoundedRect(batch, x, y, w, h, 14f,
            0.018f, 0.020f, 0.028f, 0.96f);
        DrawRoundedRect(batch, x + 1f, y + 1f, w - 2f, 1f, 0.5f,
            1f, 1f, 1f, 0.08f);

        font.DrawText(batch, "Music", x + 18f, y + 16f, 0.58f,
            0.88f, 0.90f, 0.96f, 0.96f);

        string count = $"{_musicBeatmaps.Count} maps";
        font.DrawTextRight(batch, count, x + w - 18f, y + 17f, 0.42f,
            0.48f, 0.50f, 0.58f, 0.76f);

        float controlY = y + 48f;
        bool locked = !CanUseTopBarMusic();
        bool hasMusic = _engine.Audio.IsMusicLoaded;
        string control = locked
            ? "Locked during gameplay"
            : (hasMusic ? (_engine.Audio.IsPlaying ? "Pause current" : "Resume current") : "No track loaded");
        DrawRoundedRect(batch, x + 14f, controlY, w - 28f, 34f, 10f,
            locked ? 0.080f : 0.055f,
            locked ? 0.038f : 0.058f,
            locked ? 0.038f : 0.074f,
            0.74f);
        font.DrawText(batch, control, x + 30f, controlY + 11f, 0.44f,
            locked ? 0.90f : 0.76f,
            locked ? 0.46f : 0.78f,
            locked ? 0.46f : 0.86f,
            0.90f);

        string status = TruncateToFit(font, _musicStatus, 0.38f, w - 36f);
        font.DrawText(batch, status, x + 18f, y + h - 26f, 0.38f,
            0.42f, 0.44f, 0.52f, 0.78f);

        float rowY = y + 94f;
        float rowH = 34f;
        float gap = 6f;
        int visible = GetVisibleMusicRows();

        if (_musicBeatmaps.Count == 0)
        {
            string empty = "No beatmap audio found";
            float emptyW = font.MeasureWidth(empty, 0.48f);
            font.DrawText(batch, empty, x + (w - emptyW) * 0.5f, rowY + 34f, 0.48f,
                0.52f, 0.54f, 0.62f, 0.74f);
            return;
        }

        _musicTopIndex = Math.Clamp(_musicTopIndex, 0, Math.Max(0, _musicBeatmaps.Count - visible));

        for (int row = 0; row < visible; row++)
        {
            int index = _musicTopIndex + row;
            if (index >= _musicBeatmaps.Count) break;

            var bm = _musicBeatmaps[index];
            bool current = index == _musicCurrentIndex;
            float yy = rowY + row * (rowH + gap);
            float pulse = current ? (0.55f + MathF.Sin((float)_topBarTime * 5.0f) * 0.08f) : 0.0f;
            DrawRoundedRect(batch, x + 14f, yy, w - 28f, rowH, 10f,
                current ? SkinConfig.Accent[0] * 0.16f : 0.035f,
                current ? SkinConfig.Accent[1] * 0.16f : 0.037f,
                current ? SkinConfig.Accent[2] * 0.16f : 0.050f,
                current ? 0.88f : 0.62f);
            if (current)
            {
                DrawRoundedRect(batch, x + 22f, yy + 9f, 4f, rowH - 18f, 2f,
                    SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2], pulse);
            }

            string title = TruncateToFit(font, $"{bm.Artist} - {bm.Title}", 0.42f, w - 118f);
            font.DrawText(batch, title, x + 34f, yy + 7f, 0.42f,
                current ? 0.92f : 0.68f,
                current ? 0.94f : 0.70f,
                current ? 1.00f : 0.78f,
                current ? 0.96f : 0.82f);

            string version = string.IsNullOrWhiteSpace(bm.Version) ? "Audio" : bm.Version;
            version = TruncateToFit(font, version, 0.34f, w - 128f);
            font.DrawText(batch, version, x + 34f, yy + 21f, 0.34f,
                0.44f, 0.46f, 0.54f, 0.72f);
        }

        if (_musicBeatmaps.Count > visible)
        {
            float trackH = rowH * visible + gap * (visible - 1);
            float thumbH = MathF.Max(24f, trackH * (visible / (float)_musicBeatmaps.Count));
            float pct = _musicTopIndex / (float)Math.Max(1, _musicBeatmaps.Count - visible);
            float thumbY = rowY + (trackH - thumbH) * pct;
            DrawRoundedRect(batch, x + w - 10f, rowY, 3f, trackH, 1.5f,
                1f, 1f, 1f, 0.07f);
            DrawRoundedRect(batch, x + w - 11f, thumbY, 5f, thumbH, 2.5f,
                SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2], 0.54f);
        }
    }

    private bool TryHandleMusicPopupClick(float mx, float my)
    {
        var rect = GetMusicPopupRect();
        if (!IsInside(mx, my, rect))
            return false;

        EnsureMusicBeatmapsScanned();

        float controlY = rect.Y + 48f;
        if (IsInside(mx, my, (rect.X + 14f, controlY, rect.W - 28f, 34f)))
        {
            if (!CanUseTopBarMusic())
            {
                _musicStatus = "Gameplay audio stays locked";
                return true;
            }

            TopBarOwnsMusic = true;
            if (_engine.Audio.IsMusicLoaded)
            {
                if (_engine.Audio.IsPlaying)
                {
                    _engine.Audio.PauseMusic();
                    _musicStatus = "Paused";
                }
                else
                {
                    _engine.Audio.PlayMusic();
                    _engine.Audio.SetMusicVolume(0.30f * Settings.MasterVolume * Settings.MusicVolume);
                    _musicStatus = "Playing";
                }
            }
            else if (_musicBeatmaps.Count > 0)
            {
                PlayMusicBeatmap(Math.Clamp(_musicCurrentIndex, 0, _musicBeatmaps.Count - 1));
            }
            else
            {
                _musicStatus = "No beatmap audio found";
            }
            return true;
        }

        float rowY = rect.Y + 94f;
        float rowH = 34f;
        float gap = 6f;
        int visible = GetVisibleMusicRows();
        for (int row = 0; row < visible; row++)
        {
            int index = _musicTopIndex + row;
            if (index >= _musicBeatmaps.Count) break;

            if (IsInside(mx, my, (rect.X + 14f, rowY + row * (rowH + gap), rect.W - 28f, rowH)))
            {
                PlayMusicBeatmap(index);
                return true;
            }
        }

        return true;
    }

    private void PlayMusicBeatmap(int index)
    {
        if (!CanUseTopBarMusic())
        {
            _musicStatus = "Gameplay audio stays locked";
            return;
        }

        if (index < 0 || index >= _musicBeatmaps.Count)
        {
            _musicStatus = "No beatmap selected";
            return;
        }

        var bm = _musicBeatmaps[index];
        string audioPath = ResolveMusicAudioPath(bm);
        if (string.IsNullOrEmpty(audioPath))
        {
            _musicStatus = "Audio file missing";
            return;
        }

        if (_engine.Audio.LoadMusic(audioPath))
        {
            TopBarOwnsMusic = true;
            _musicCurrentIndex = index;
            _engine.Audio.PlayMusic();
            _engine.Audio.SetMusicVolume(0.30f * Settings.MasterVolume * Settings.MusicVolume);

            double seekMs = bm.PreviewTime > 0 ? bm.PreviewTime : 0;
            if (seekMs > 0)
                _engine.Audio.SeekMusic(seekMs);
            else if (_engine.Audio.MusicDuration > 0)
                _engine.Audio.SeekMusic(_engine.Audio.MusicDuration * 0.32);

            _musicStatus = $"Playing {bm.Title}";
            if (ReferenceEquals(_currentScreen, _mainMenu))
                _mainMenu.SetManualAmbient(bm);
        }
        else
        {
            _musicStatus = "Could not play audio";
        }
    }

    private bool CanUseTopBarMusic()
        => !ReferenceEquals(_currentScreen, _gameplay)
           && !ReferenceEquals(_currentScreen, _loading);

    private void EnsureMusicBeatmapsScanned()
    {
        if (_musicBeatmapsScanned) return;

        _musicBeatmaps.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddMusicCandidates(_songSelect.Beatmaps, seen);
        AddMusicCandidates(_mainMenu.Beatmaps, seen);

        if (_musicBeatmaps.Count == 0)
            ScanMusicFallback(seen);

        _musicBeatmaps.Sort((a, b) =>
        {
            int c = string.Compare(a.Artist, b.Artist, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            c = string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.Compare(a.Version, b.Version, StringComparison.OrdinalIgnoreCase);
        });

        _musicBeatmapsScanned = true;
        _musicStatus = _musicBeatmaps.Count == 0 ? "No beatmap audio found" : "Pick a song";
    }

    private void AddMusicCandidates(IEnumerable<BeatmapInfo> beatmaps, HashSet<string> seen)
    {
        foreach (var bm in beatmaps)
        {
            if (string.IsNullOrWhiteSpace(bm.AudioFilename)) continue;
            if (!seen.Add(bm.FilePath)) continue;
            _musicBeatmaps.Add(bm);
        }
    }

    private void ScanMusicFallback(HashSet<string> seen)
    {
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
            AddMusicCandidates(BeatmapDecoder.ScanSongsDirectory(path), seen);
        }

        foreach (string stableSongs in OsuInstallDetector.FindStableSongsPaths())
        {
            string full;
            try { full = Path.GetFullPath(stableSongs); }
            catch { continue; }
            if (Directory.Exists(full))
                AddMusicCandidates(BeatmapDecoder.ScanSongsDirectory(full), seen);
        }

        foreach (string osuFile in OsuInstallDetector.FindLazerOsuFiles())
        {
            if (!seen.Add(osuFile)) continue;
            try
            {
                var map = BeatmapDecoder.Decode(osuFile);
                _musicBeatmaps.Add(new BeatmapInfo
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
                });
            }
            catch { }
        }
    }

    private string ResolveMusicAudioPath(BeatmapInfo bm)
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

    private int GetVisibleMusicRows() => 6;

    private (float X, float Y, float W, float H) GetMusicPopupRect()
    {
        float w = MathF.Min(520f, _engine.ScreenWidth - 24f);
        float h = MathF.Min(360f, _engine.ScreenHeight - GlobalTopBarHeight - 20f);
        return (12f, GlobalTopBarHeight + 8f, MathF.Max(280f, w), MathF.Max(220f, h));
    }

    private static bool IsInside(float mx, float my, (float X, float Y, float W, float H) rect)
        => mx >= rect.X && mx <= rect.X + rect.W && my >= rect.Y && my <= rect.Y + rect.H;

    private static bool TryGetTopBarButton(float mx, float my, out int index)
    {
        index = -1;
        if (my < 6f || my > 30f) return false;

        for (int i = 0; i < 3; i++)
        {
            float x = 12f + i * 32f;
            if (mx >= x && mx <= x + 24f)
            {
                index = i;
                return true;
            }
        }

        return false;
    }

    private static int GetTopBarHoverButton(float mx, float my)
        => TryGetTopBarButton(mx, my, out int index) ? index : -1;

    private static string TruncateToFit(BitmapFont font, string text,
        float scale, float maxWidth)
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

    private string GetCurrentSectionName()
    {
        if (SettingsOverlay.IsOpen) return "Settings";
        if (ReferenceEquals(_currentScreen, _profileSelect)) return "Profiles";
        if (ReferenceEquals(_currentScreen, _mainMenu)) return "Main Menu";
        if (ReferenceEquals(_currentScreen, _songSelect)) return "Level Select";
        if (ReferenceEquals(_currentScreen, _loading)) return "Loading";
        if (ReferenceEquals(_currentScreen, _gameplay)) return "Gameplay";
        if (ReferenceEquals(_currentScreen, _results)) return "Results";
        return "TaikoNova";
    }

    private void DrawRoundedRect(SpriteBatch batch, float x, float y, float w, float h,
        float radius, float r, float g, float b, float a)
    {
        if (a <= 0f || w <= 0f || h <= 0f) return;

        radius = MathF.Min(radius, MathF.Min(w, h) * 0.5f);
        if (radius <= 0.5f)
        {
            batch.Draw(_engine.PixelTex, x, y, w, h, r, g, b, a);
            return;
        }

        float d = radius * 2f;
        float midW = MathF.Max(0f, w - d);
        float midH = MathF.Max(0f, h - d);

        if (midW > 0f)
            batch.Draw(_engine.PixelTex, x + radius, y, midW, h, r, g, b, a);
        if (midH > 0f)
        {
            batch.Draw(_engine.PixelTex, x, y + radius, radius, midH, r, g, b, a);
            batch.Draw(_engine.PixelTex, x + w - radius, y + radius, radius, midH, r, g, b, a);
        }

        batch.Draw(_engine.CircleTex, x, y, d, d, r, g, b, a);
        batch.Draw(_engine.CircleTex, x + w - d, y, d, d, r, g, b, a);
        batch.Draw(_engine.CircleTex, x, y + h - d, d, d, r, g, b, a);
        batch.Draw(_engine.CircleTex, x + w - d, y + h - d, d, d, r, g, b, a);
    }

    /// <summary>Start a fade-to-black transition, executing an action at the midpoint.</summary>
    private void TransitionTo(Action midpointAction)
    {
        if (_transState != TransitionState.None) return;
        _transState = TransitionState.FadingOut;
        _transAlpha = 0f;
        _transAction = midpointAction;
    }

    public void OnEscape()
    {
        if (SettingsOverlay.IsOpen)
        {
            SettingsOverlay.Close();
            return;
        }
        _currentScreen.OnEscape();
    }

    public void OpenSettings()
    {
        SettingsOverlay.Open();
    }

    // ── Apply settings to engine systems ──
    private void ApplySettings()
    {
        // Fullscreen
        _engine.WindowState = Settings.Fullscreen
            ? WindowState.Fullscreen
            : WindowState.Normal;

        // VSync
        _engine.VSync = Settings.VSync
            ? VSyncMode.Adaptive
            : VSyncMode.Off;
    }

    // ── Screen transitions ──

    public void LoginProfile(PlayerProfile profile)
    {
        Profiles.SetActiveProfile(profile);
        Notifications.Show("Profile", profile.Name,
            r: SkinConfig.Accent[0], g: SkinConfig.Accent[1], b: SkinConfig.Accent[2]);
        GoToMainMenu();
    }

    public void GoToProfileSelect()
    {
        TransitionTo(() =>
        {
            _currentScreen.OnExit();
            _currentScreen = _profileSelect;
            _currentScreen.OnEnter();
        });
    }

    public void GoToMainMenu()
    {
        TransitionTo(() =>
        {
            _currentScreen.OnExit();
            _currentScreen = _mainMenu;
            _currentScreen.OnEnter();
        });
    }

    public void GoToSongSelect()
    {
        TransitionTo(() =>
        {
            _currentScreen.OnExit();
            _currentScreen = _songSelect;
            _currentScreen.OnEnter();
        });
    }

    /// <summary>Transition from main menu to song select (uses fade).</summary>
    public void GoToSongSelectFromMenu()
    {
        TransitionTo(() =>
        {
            _currentScreen.OnExit();
            _currentScreen = _songSelect;
            _currentScreen.OnEnter();
        });
    }

    /// <summary>Start practice from main menu (uses fade → loading screen).</summary>
    public void StartPracticeFromMenu()
    {
        TransitionTo(() =>
        {
            _currentScreen.OnExit();
            StartPracticeInternal();
        });
    }

    public void StartBeatmap(string osuFilePath)
    {
        try
        {
            ReleaseTopBarMusicControl();
            Console.WriteLine($"[Game] Loading beatmap: {osuFilePath}");
            var beatmap = BeatmapDecoder.Decode(osuFilePath);
            Console.WriteLine($"[Game] Loaded: {beatmap.DisplayArtist} - {beatmap.DisplayTitle} [{beatmap.Version}]");
            Console.WriteLine($"[Game] {beatmap.HitObjects.Count} hit objects, OD={beatmap.OverallDifficulty}");

            // Store pending load and show loading screen
            _pendingBeatmap = beatmap;
            _pendingWithAudio = true;

            _currentScreen.OnExit();
            _loading.SetBeatmap(beatmap, withAudio: true);
            _currentScreen = _loading;
            _currentScreen.OnEnter();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Game] Error loading beatmap: {ex.Message}");
        }
    }

    public void StartPractice()
    {
        StartPracticeInternal();
    }

    private void StartPracticeInternal()
    {
        ReleaseTopBarMusicControl();
        _engine.Audio.StopMusic();
        Console.WriteLine("[Game] Starting practice mode...");
        var beatmap = TestBeatmapGenerator.Generate(bpm: 160, durationSeconds: 60);

        _pendingBeatmap = beatmap;
        _pendingWithAudio = false;

        _currentScreen.OnExit();
        _loading.SetPractice(beatmap);
        _currentScreen = _loading;
        _currentScreen.OnEnter();
    }

    /// <summary>Called by LoadingScreen when it's done — actually transitions to gameplay.</summary>
    public void FinishLoading()
    {
        if (_pendingBeatmap == null) return;

        _currentScreen.OnExit();
        _gameplay.LoadBeatmap(_pendingBeatmap, withAudio: _pendingWithAudio);
        _currentScreen = _gameplay;
        _currentScreen.OnEnter();

        _pendingBeatmap = null;
    }

    public void ShowResults(BeatmapData beatmap, ScoreProcessor score)
    {
        Console.WriteLine($"[Game] Results — Score: {score.Score:N0}, Acc: {score.AccuracyDisplay}, Grade: {score.Grade}");
        TransitionTo(() =>
        {
            _currentScreen.OnExit();
            _results.SetResults(beatmap, score);
            _currentScreen = _results;
            _currentScreen.OnEnter();
        });
    }

    public void Dispose()
    {
        _profileSelect.Dispose();
        _mainMenu.Dispose();
        _songSelect.Dispose();
        _gameplay.Dispose();
        _results.Dispose();
        _loading.Dispose();
    }

    // ══════════════════════════════════════════════════
    // File drop import (.osz)
    // ══════════════════════════════════════════════════

    public void OnFileDrop(string[] files)
    {
        string songsDir = Path.Combine(Environment.CurrentDirectory, "Songs");
        Directory.CreateDirectory(songsDir);

        var oszFiles = files
            .Where(f => f.EndsWith(".osz", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (oszFiles.Length == 0) return;

        foreach (var oszFile in oszFiles)
        {
            string name = Path.GetFileNameWithoutExtension(oszFile);
            int notifId = Notifications.Show(
                "Importing",
                name.Length > 28 ? name[..26] + ".." : name,
                progress: 0f,
                r: 0.3f, g: 0.7f, b: 1f);

            // Run extraction on a background thread to avoid blocking
            Task.Run(() =>
            {
                try
                {
                    // Simulate incremental progress (zip extraction is atomic)
                    Notifications.UpdateProgress(notifId, 0.15f, "Extracting...");
                    Thread.Sleep(80);

                    string? result = BeatmapDecoder.ExtractOsz(oszFile, songsDir);

                    if (result != null)
                    {
                        Notifications.UpdateProgress(notifId, 0.7f, "Scanning...");
                        Thread.Sleep(60);
                        Notifications.Complete(notifId, "Imported!");
                        Console.WriteLine($"[Import] Successfully imported: {name}");

                        // Flag song select for rescan
                        _songSelect.NeedsRescan = true;
                        _musicBeatmapsScanned = false;
                    }
                    else
                    {
                        Notifications.Fail(notifId, "Already exists");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Import] Failed: {ex.Message}");
                    Notifications.Fail(notifId, "Import failed");
                }
            });
        }
    }
}
