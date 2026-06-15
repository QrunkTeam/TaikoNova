using OpenTK.Windowing.Common;
using TaikoNova.Engine;
using TaikoNova.Game.Beatmap;
using TaikoNova.Game.Screens;
using TaikoNova.Game.Settings;
using TaikoNova.Game.Taiko;

namespace TaikoNova.Game;

/// <summary>
/// Top-level game coordinator. Manages screen transitions
/// and shared game state.
/// </summary>
public sealed class TaikoGame : IDisposable
{
    private readonly GameEngine _engine;
    private readonly GameLaunchOptions _launchOptions;

    // ── Settings ──
    public SettingsManager Settings { get; }
    public SettingsOverlay SettingsOverlay { get; }
    public NotificationOverlay Notifications { get; }
    public bool AutoPlay => _launchOptions.AutoPlay;

    // ── Screens ──
    private readonly MainMenuScreen _mainMenu;
    private readonly SongSelectScreen _songSelect;
    private readonly GameplayScreen _gameplay;
    private readonly ResultScreen _results;
    private readonly LoadingScreen _loading;
    private Screen _currentScreen;

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
        SettingsOverlay = new SettingsOverlay(engine, Settings, () => ApplySettings());
        Notifications = new NotificationOverlay(engine);

        _mainMenu = new MainMenuScreen(engine, this);
        _songSelect = new SongSelectScreen(engine, this);
        _gameplay = new GameplayScreen(engine, this);
        _results = new ResultScreen(engine, this);
        _loading = new LoadingScreen(engine, this);

        // Apply loaded settings to engine
        ApplySettings();

        // Start on main menu
        _currentScreen = _mainMenu;
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
        // ── Settings overlay (takes priority) ──
        if (SettingsOverlay.IsOpen)
        {
            SettingsOverlay.Update(deltaTime);
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

        _currentScreen.Update(deltaTime);
        Notifications.Update(deltaTime);
    }

    public void Render(double deltaTime)
    {
        _currentScreen.Render(deltaTime);

        // ── Settings overlay on top of everything ──
        if (SettingsOverlay.IsOpen)
            SettingsOverlay.Render(deltaTime);

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
