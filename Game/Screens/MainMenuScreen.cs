using OpenTK.Windowing.GraphicsLibraryFramework;
using TaikoNova.Engine;
using TaikoNova.Engine.GL;
using TaikoNova.Game.Beatmap;
using TaikoNova.Game.Skin;

namespace TaikoNova.Game.Screens;

/// <summary>
/// Main menu with arrow-key navigable options, background music + image
/// from the beatmap library, per-item accent colours, and a "now playing"
/// indicator in the top-right corner.
/// </summary>
public class MainMenuScreen : Screen
{
    // ── Menu items ──
    private readonly struct MenuItem
    {
        public string Label { get; init; }
        public string Description { get; init; }
        public Action OnSelect { get; init; }
        public float[] Accent { get; init; } // RGB
    }

    private MenuItem[] _items = Array.Empty<MenuItem>();
    private int _selected;

    // ── Animation ──
    private double _time;
    private float _enterAnim;      // 0→1 screen fade-in
    private float _selectBounce;   // brief bounce on select change
    private float _confirmAnim;    // 0→1 exit animation
    private bool _confirming;

    // ── Background music / image ──
    private List<BeatmapInfo> _beatmaps = new();
    private bool _scanned;
    private BackgroundManager _background;
    private BeatmapInfo? _nowPlaying;
    private float _musicVolume;       // current volume lerp 0→1
    private float _musicTarget;      // target volume (1 = fade in, 0 = fade out)
    private bool _musicStarted;
    private bool _changingTrack;     // true while fading out before switching
    private float _bgFade;           // background crossfade 0→1
    private float _npPulse;           // subtle pulse for now-playing icon
    private double _trackEndDelay;   // brief silence between tracks

    // ── Per-item accent colours ──
    private static readonly float[] AccentPlay     = { 0.30f, 0.55f, 0.95f }; // blue
    private static readonly float[] AccentPractice = { 0.90f, 0.72f, 0.20f }; // gold
    private static readonly float[] AccentSettings = { 0.45f, 0.80f, 0.50f }; // green
    private static readonly float[] AccentExit     = { 0.86f, 0.24f, 0.18f }; // red

    public IReadOnlyList<BeatmapInfo> Beatmaps => _beatmaps;

    public MainMenuScreen(GameEngine engine, TaikoGame game) : base(engine, game)
    {
        _background = new BackgroundManager(engine);
        _background.DimLevel = 0.65f;

        _items = new MenuItem[]
        {
            new() { Label = "Play",     Description = "Select a song and start playing",      Accent = AccentPlay,     OnSelect = () => game.GoToSongSelectFromMenu() },
            new() { Label = "Practice", Description = "Warm up with auto-generated patterns", Accent = AccentPractice, OnSelect = () => game.StartPracticeFromMenu() },
            new() { Label = "Settings", Description = "Adjust audio, display, and gameplay",  Accent = AccentSettings, OnSelect = () => game.OpenSettings() },
            new() { Label = "Exit",     Description = "Close the game",                       Accent = AccentExit,     OnSelect = () => engine.Close() },
        };
    }

    public override void OnEnter()
    {
        _time = 0;
        _enterAnim = 0;
        _selectBounce = 0;
        _confirmAnim = 0;
        _confirming = false;
        _musicVolume = 0f;
        _musicTarget = 1f;
        _musicStarted = false;
        _changingTrack = false;
        _bgFade = 0f;
        _trackEndDelay = 0;

        if (!_scanned)
        {
            ScanBeatmaps();
            _scanned = true;
        }

        if (!Game.TopBarOwnsMusic || !Engine.Audio.IsMusicLoaded)
            PickRandomBackground();
    }

    public override void OnExit()
    {
        // Fade out / stop music when leaving menu
        Engine.Audio.StopMusic();
        Game.ReleaseTopBarMusicControl();
        _background.Unload();
        _musicStarted = false;
    }

    public void SetManualAmbient(BeatmapInfo beatmap)
    {
        _nowPlaying = beatmap;
        _musicStarted = false;
        _changingTrack = false;
        _trackEndDelay = 0;
        _musicVolume = 1f;
        _musicTarget = 1f;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Beatmap scanning (lightweight copy of SongSelect logic)
    // ═══════════════════════════════════════════════════════════════════

    private void ScanBeatmaps()
    {
        _beatmaps.Clear();
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
            if (!seen.Add(path)) continue;
            if (Directory.Exists(path))
                AddFromDirectory(path, seen);
        }

        foreach (string stableSongs in OsuInstallDetector.FindStableSongsPaths())
        {
            string full;
            try { full = Path.GetFullPath(stableSongs); }
            catch { continue; }
            if (!seen.Add(full)) continue;
            AddFromDirectory(full, seen);
        }

        var lazerFiles = OsuInstallDetector.FindLazerOsuFiles();
        foreach (string osuFile in lazerFiles)
        {
            if (seen.Contains(osuFile)) continue;
            try
            {
                var map = BeatmapDecoder.Decode(osuFile);
                _beatmaps.Add(new BeatmapInfo
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

        Console.WriteLine($"[MainMenu] Scanned {_beatmaps.Count} beatmaps for ambient playback");
    }

    private void AddFromDirectory(string path, HashSet<string> seen)
    {
        var found = BeatmapDecoder.ScanSongsDirectory(path);
        foreach (var b in found)
            if (seen.Add(b.FilePath))
                _beatmaps.Add(b);
    }

    private void PickRandomBackground()
    {
        _background.Unload();
        _nowPlaying = null;
        _musicStarted = false;

        if (_beatmaps.Count == 0) return;

        // Pre-validate: only keep beatmaps whose audio file actually exists on disk
        var validated = new List<(BeatmapInfo bm, string audioPath, string? bgPath)>();
        foreach (var b in _beatmaps)
        {
            string audio = ResolveAudioPath(b);
            if (string.IsNullOrEmpty(audio)) continue; // no valid audio file

            string? bg = ResolveBgPath(b);
            validated.Add((b, audio, bg));
        }

        if (validated.Count == 0) return;

        // Prefer candidates that have BOTH audio and background image
        var withBg = validated.Where(v => v.bgPath != null).ToList();
        var pool = withBg.Count > 0 ? withBg : validated;

        var rng = new Random();
        var (pick, pickAudio, pickBg) = pool[rng.Next(pool.Count)];

        // Load audio (already validated to exist)
        if (Engine.Audio.LoadMusic(pickAudio))
        {
            double seekMs = pick.PreviewTime > 0 ? pick.PreviewTime : 0;
            Engine.Audio.PlayMusic();
            if (seekMs > 0)
                Engine.Audio.SeekMusic(seekMs);
            else
            {
                double dur = Engine.Audio.MusicDuration;
                if (dur > 0) Engine.Audio.SeekMusic(dur * 0.3);
            }
            Engine.Audio.SetMusicVolume(0f); // will fade in
            _musicVolume = 0f;
            _musicTarget = 1f;
            _musicStarted = true;
            _changingTrack = false;
            _nowPlaying = pick;
        }

        // Load background image (already validated to exist)
        if (pickBg != null)
        {
            string folder = string.IsNullOrEmpty(pick.FolderPath)
                ? (Path.GetDirectoryName(pick.FilePath) ?? "")
                : pick.FolderPath;
            var stub = new BeatmapData
            {
                FilePath = pick.FilePath,
                FolderPath = folder,
                BackgroundFilename = pick.BackgroundFilename
            };
            _background.Load(stub);
        }
    }

    /// <summary>
    /// Check if a beatmap's background image file actually exists on disk.
    /// Returns the resolved path or null.
    /// </summary>
    private string? ResolveBgPath(BeatmapInfo bm)
    {
        if (string.IsNullOrEmpty(bm.BackgroundFilename)) return null;
        string folder = string.IsNullOrEmpty(bm.FolderPath)
            ? (Path.GetDirectoryName(bm.FilePath) ?? "")
            : bm.FolderPath;
        string direct = Path.Combine(folder, bm.BackgroundFilename);
        if (File.Exists(direct)) return direct;

        // Try lazer hash store
        if (LazerAudioResolver.IsLazerPath(bm.FilePath))
        {
            var resolved = LazerFileResolver.ResolveFile(bm.FilePath, bm.BackgroundFilename);
            if (resolved != null) return resolved;
        }
        return null;
    }

    private string ResolveAudioPath(BeatmapInfo bm)
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

    // ═══════════════════════════════════════════════════════════════════
    //  Update
    // ═══════════════════════════════════════════════════════════════════

    public override void Update(double dt)
    {
        _time += dt;
        float fdt = (float)dt;

        _enterAnim = MathF.Min(1f, _enterAnim + fdt * 3.5f);
        _selectBounce = MathF.Max(0f, _selectBounce - fdt * 6f);
        _npPulse += fdt * 2.5f;

        // Background fade-in
        _bgFade = MathF.Min(1f, _bgFade + fdt * 1.5f);

        // Music volume management with smooth transitions
        if (Game.TopBarOwnsMusic)
        {
            if (Engine.Audio.IsMusicLoaded)
                Engine.Audio.SetMusicVolume(0.30f * Game.Settings.MasterVolume * Game.Settings.MusicVolume);
        }
        else if (_musicStarted)
        {
            // Smoothly lerp toward target
            float fadeSpeed = _musicTarget > _musicVolume ? 0.5f : 1.8f; // slow in, faster out
            _musicVolume += (_musicTarget - _musicVolume) * fdt * fadeSpeed;
            _musicVolume = MathF.Max(0f, MathF.Min(1f, _musicVolume));

            if (Engine.Audio.IsMusicLoaded)
                Engine.Audio.SetMusicVolume(_musicVolume * 0.30f * Game.Settings.MasterVolume * Game.Settings.MusicVolume); // scaled by settings

            // When fading out for a track change, wait until quiet then switch
            if (_changingTrack && _musicVolume < 0.02f)
            {
                _changingTrack = false;
                _musicStarted = false;
                Engine.Audio.StopMusic();
                _trackEndDelay = 1.2; // brief silence before next track
            }

            // Track ended naturally — start fade-out then switch
            if (!_changingTrack && Engine.Audio.IsMusicLoaded && !Engine.Audio.IsPlaying && _musicVolume > 0.05f)
            {
                _changingTrack = true;
                _musicTarget = 0f;
            }
        }
        else if (_trackEndDelay > 0)
        {
            // Brief silence between tracks
            _trackEndDelay -= dt;
            if (_trackEndDelay <= 0)
                PickRandomBackground();
        }

        if (_confirming)
        {
            _confirmAnim = MathF.Min(1f, _confirmAnim + fdt * 4f);

            // Fade music out during confirm via target system
            if (_musicStarted)
                _musicTarget = 0f;

            if (_confirmAnim >= 1f)
                _items[_selected].OnSelect();
            return;
        }

        var input = Engine.Input;

        if (input.IsPressed(Keys.Up) || input.IsPressed(Keys.Left))
        {
            _selected = (_selected - 1 + _items.Length) % _items.Length;
            _selectBounce = 1f;
        }
        if (input.IsPressed(Keys.Down) || input.IsPressed(Keys.Right))
        {
            _selected = (_selected + 1) % _items.Length;
            _selectBounce = 1f;
        }

        if (MathF.Abs(input.ScrollDelta) > 0.01f)
        {
            int delta = -(int)MathF.Round(input.ScrollDelta);
            if (delta != 0)
            {
                _selected = (_selected + delta + _items.Length) % _items.Length;
                _selectBounce = 1f;
            }
        }

        if (input.MousePressed && TrySelectItemAt(input.MouseX, input.MouseY))
        {
            if (_items[_selected].Label == "Settings")
                _items[_selected].OnSelect();
            else
            {
                _confirming = true;
                _confirmAnim = 0;
            }
        }

        if (input.IsPressed(Keys.Enter) || input.IsPressed(Keys.Space))
        {
            // Settings opens overlay immediately (no confirm animation needed)
            if (_items[_selected].Label == "Settings")
            {
                _items[_selected].OnSelect();
            }
            else
            {
                _confirming = true;
                _confirmAnim = 0;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Render
    // ═══════════════════════════════════════════════════════════════════

    public override void Render(double dt)
    {
        var batch = Engine.SpriteBatch;
        var font  = Engine.Font;
        var px    = Engine.PixelTex;
        var proj  = Engine.Projection;
        int sw    = Engine.ScreenWidth;
        int sh    = Engine.ScreenHeight;

        float fadeA = EaseOutCubic(_enterAnim);
        float confirmT = EaseOutCubic(_confirmAnim);
        float contentA = fadeA * (1f - confirmT * 0.35f);
        float[] selectedAccent = _items[_selected].Accent;

        batch.Begin(proj);

        batch.Draw(px, 0, 0, sw, sh, 0.018f, 0.019f, 0.026f, 1f);
        if (_background.HasBackground)
        {
            float savedDim = _background.DimLevel;
            _background.DimLevel = 1f - (1f - savedDim) * EaseOutCubic(_bgFade);
            _background.Render();
            _background.DimLevel = savedDim;
        }

        batch.Draw(px, 0, 0, sw, sh, 0f, 0f, 0f, 0.34f * fadeA);
        batch.Draw(px, 0, 0, MathF.Min(680f, sw * 0.52f), sh, 0f, 0f, 0f, 0.38f * fadeA);
        batch.Draw(px, 0, 0, sw, 140f, 0f, 0f, 0f, 0.24f * fadeA);
        batch.Draw(px, 0, sh - 170f, sw, 170f, 0f, 0f, 0f, 0.32f * fadeA);

        var layout = GetMenuLayout(sw, sh);
        float brandX = layout.X;
        float brandY = 118f;

        font.DrawText(batch, "TaikoNova", brandX, brandY, 1.58f,
            0.94f, 0.95f, 1f, contentA);
        float titleW = font.MeasureWidth("TaikoNova", 1.58f);
        DrawRoundedRect(batch, brandX, brandY + 62f, MathF.Min(330f, titleW) * contentA, 2f, 1f,
            selectedAccent[0], selectedAccent[1], selectedAccent[2], 0.58f * fadeA);

        string mode = Game.AutoPlay ? "AUTO ENABLED" : "MAIN MENU";
        font.DrawText(batch, mode, brandX, brandY - 34f, 0.48f,
            selectedAccent[0], selectedAccent[1], selectedAccent[2], 0.74f * contentA);

        string selectedDescription = _items[_selected].Description;
        selectedDescription = TruncateToFit(font, selectedDescription, 0.54f, layout.W);
        font.DrawText(batch, selectedDescription, brandX, brandY + 92f, 0.54f,
            0.54f, 0.56f, 0.62f, 0.72f * contentA);

        for (int i = 0; i < _items.Length; i++)
        {
            float rowDelay = i * 0.10f + 0.15f;
            float rowT = MathF.Max(0f, ((float)_time - rowDelay) / 0.35f);
            DrawMenuItem(batch, font, i, rowT, layout.X, layout.Y + i * (layout.RowH + layout.Gap),
                layout.W, layout.RowH, contentA);
        }

        DrawAmbientStatus(batch, font, px, sw, sh, contentA, selectedAccent);
        DrawControlHint(batch, font, sw, sh, contentA);

        if (_confirmAnim > 0.01f)
            batch.Draw(px, 0, 0, sw, sh, 0f, 0f, 0f, confirmT * 0.68f);

        batch.End();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════

    private (float X, float Y, float W, float RowH, float Gap) GetMenuLayout(int sw, int sh)
    {
        float w = MathF.Min(520f, sw * 0.42f);
        float rowH = 56f;
        float gap = 16f;
        float x = MathF.Max(76f, sw * 0.085f);
        float totalH = _items.Length * rowH + (_items.Length - 1) * gap;
        float y = MathF.Min(sh - totalH - 150f, 332f);
        return (x, y, w, rowH, gap);
    }

    private void DrawMenuItem(SpriteBatch batch, Engine.Text.BitmapFont font,
        int index, float rowT, float x, float y, float w, float h, float fadeA)
    {
        var item = _items[index];
        bool selected = index == _selected;
        float[] accent = item.Accent;
        float reveal = EaseOutCubic(MathF.Min(1f, rowT));
        float a = fadeA * reveal;
        float slide = (1f - reveal) * 30f;

        if (_confirming)
        {
            if (selected)
                a *= 0.82f + MathF.Sin((float)_time * 12f) * 0.12f;
            else
                a *= 1f - EaseOutCubic(_confirmAnim);
        }

        if (selected)
        {
            DrawRoundedRect(batch, x - 18f + slide, y - 5f, w + 36f, h + 10f, 14f,
                accent[0], accent[1], accent[2], 0.075f * a);
            DrawRoundedRect(batch, x - 18f + slide, y + 10f, 3f, h - 20f, 1.5f,
                accent[0], accent[1], accent[2], 0.88f * a);
        }

        DrawRoundedRect(batch, x + slide, y + h - 1f, w, 1f, 0.5f,
            1f, 1f, 1f, (selected ? 0.10f : 0.045f) * a);

        string idx = (index + 1).ToString("00");
        font.DrawText(batch, idx, x + slide, y + 18f, 0.46f,
            selected ? accent[0] : 0.34f,
            selected ? accent[1] : 0.36f,
            selected ? accent[2] : 0.42f,
            (selected ? 0.78f : 0.54f) * a);

        float labelScale = selected ? 0.92f + _selectBounce * 0.035f : 0.76f;
        string label = TruncateToFit(font, item.Label, labelScale, w - 144f);
        font.DrawText(batch, label, x + 58f + slide, y + (selected ? 9f : 13f), labelScale,
            selected ? 0.95f : 0.64f,
            selected ? 0.96f : 0.66f,
            selected ? 1.00f : 0.72f,
            a);

        if (selected)
        {
            string action = "ENTER";
            float actionW = font.MeasureWidth(action, 0.40f);
            font.DrawText(batch, action, x + w - actionW + slide, y + 20f, 0.40f,
                0.62f, 0.64f, 0.70f, 0.74f * a);
        }
    }

    private void DrawAmbientStatus(SpriteBatch batch, Engine.Text.BitmapFont font,
        Texture2D px, int sw, int sh, float fadeA, float[] accent)
    {
        float x = sw - 520f;
        float y = sh - 126f;
        float w = 450f;

        DrawRoundedRect(batch, x, y + 28f, w, 1f, 0.5f,
            1f, 1f, 1f, 0.075f * fadeA);

        string label = _nowPlaying == null ? "READY" : "AMBIENT";
        font.DrawText(batch, label, x, y, 0.42f,
            accent[0], accent[1], accent[2], 0.72f * fadeA);

        if (_nowPlaying == null)
        {
            string fallback = $"{_beatmaps.Count} maps scanned";
            font.DrawText(batch, fallback, x, y + 42f, 0.56f,
                0.56f, 0.58f, 0.64f, 0.72f * fadeA);
            return;
        }

        float pulse = 0.55f + MathF.Sin(_npPulse) * 0.16f;
        batch.Draw(Engine.CircleTex, x, y + 44f, 8f, 8f,
            accent[0], accent[1], accent[2], pulse * fadeA);

        string title = $"{_nowPlaying.Artist} - {_nowPlaying.Title}";
        title = TruncateToFit(font, title, 0.54f, w - 24f);
        font.DrawText(batch, title, x + 20f, y + 40f, 0.54f,
            0.62f, 0.64f, 0.70f, 0.78f * fadeA);
    }

    private void DrawControlHint(SpriteBatch batch, Engine.Text.BitmapFont font,
        int sw, int sh, float fadeA)
    {
        string controls = "Up/Down move     Enter select     Esc exit";
        controls = TruncateToFit(font, controls, 0.42f, sw - 160f);
        float x = MathF.Max(76f, sw * 0.085f);
        font.DrawText(batch, controls, x, sh - 52f, 0.42f,
            0.42f, 0.44f, 0.50f, 0.68f * fadeA);
    }

    private bool TrySelectItemAt(float mx, float my)
    {
        var layout = GetMenuLayout(Engine.ScreenWidth, Engine.ScreenHeight);
        for (int i = 0; i < _items.Length; i++)
        {
            float y = layout.Y + i * (layout.RowH + layout.Gap);
            if (mx < layout.X - 24f || mx > layout.X + layout.W + 24f) continue;
            if (my < y - 8f || my > y + layout.RowH + 8f) continue;

            _selected = i;
            _selectBounce = 1f;
            return true;
        }

        return false;
    }

    private void DrawRoundedRect(SpriteBatch batch, float x, float y, float w, float h,
        float radius, float r, float g, float b, float a)
    {
        if (a <= 0f || w <= 0f || h <= 0f) return;

        radius = MathF.Min(radius, MathF.Min(w, h) * 0.5f);
        if (radius <= 0.5f)
        {
            batch.Draw(Engine.PixelTex, x, y, w, h, r, g, b, a);
            return;
        }

        float d = radius * 2f;
        float midW = MathF.Max(0f, w - d);
        float midH = MathF.Max(0f, h - d);

        if (midW > 0f)
            batch.Draw(Engine.PixelTex, x + radius, y, midW, h, r, g, b, a);
        if (midH > 0f)
        {
            batch.Draw(Engine.PixelTex, x, y + radius, radius, midH, r, g, b, a);
            batch.Draw(Engine.PixelTex, x + w - radius, y + radius, radius, midH, r, g, b, a);
        }

        batch.Draw(Engine.CircleTex, x, y, d, d, r, g, b, a);
        batch.Draw(Engine.CircleTex, x + w - d, y, d, d, r, g, b, a);
        batch.Draw(Engine.CircleTex, x, y + h - d, d, d, r, g, b, a);
        batch.Draw(Engine.CircleTex, x + w - d, y + h - d, d, d, r, g, b, a);
    }

    private static string TruncateToFit(Engine.Text.BitmapFont font, string text,
        float scale, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || font.MeasureWidth(text, scale) <= maxWidth)
            return text;

        for (int len = text.Length - 1; len > 0; len--)
        {
            string truncated = text[..len] + "..";
            if (font.MeasureWidth(truncated, scale) <= maxWidth)
                return truncated;
        }

        return "..";
    }

    private static float EaseOutCubic(float t)
    {
        float t1 = 1f - MathF.Max(0f, MathF.Min(1f, t));
        return 1f - t1 * t1 * t1;
    }

    public override void OnEscape()
    {
        int exitIdx = _items.Length - 1;
        if (_selected == exitIdx)
        {
            if (!_confirming)
            {
                _confirming = true;
                _confirmAnim = 0;
            }
        }
        else
        {
            _selected = exitIdx;
            _selectBounce = 1f;
        }
    }

    public override void Dispose()
    {
        _background.Dispose();
    }
}
