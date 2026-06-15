using System.Diagnostics;
using OpenTK.Windowing.GraphicsLibraryFramework;
using TaikoNova.Engine;
using TaikoNova.Engine.GL;
using TaikoNova.Game.Beatmap;
using TaikoNova.Game.Skin;
using TaikoNova.Game.Taiko;

namespace TaikoNova.Game.Screens;

/// <summary>
/// Main gameplay screen. Handles note scrolling, input, judgment,
/// scoring, and all gameplay rendering.
/// </summary>
public class GameplayScreen : Screen
{
    // ── Beatmap ──
    private BeatmapData _beatmap = null!;
    private HitWindows _hitWindows = null!;
    private ScoreProcessor _score = null!;
    private Playfield _playfield = null!;
    private NoteRenderer _noteRenderer = null!;

    // ── Timing ──
    private readonly Stopwatch _stopwatch = new();
    private double _currentTime;
    private double _startOffset;  // Lead-in time
    private bool _started;
    private bool _finished;
    private bool _useAudio;
    private bool _autoPlay;

    // ── Gameplay state ──
    private int _nextHitIndex; // Index of next unhit note to check
    private float _scrollSpeed;
    private const double AutoLongTickInterval = 75.0;
    private double _autoNextLongTickTime;
    private bool _autoDendenDon = true;
    private double _lastAutoDonFlashTime = -9999;
    private double _lastAutoKatFlashTime = -9999;

    // ── Drum input bindings ──
    private static readonly Keys[] DonKeysLeft = { Keys.D, Keys.F };
    private static readonly Keys[] KatKeysLeft = { Keys.J, Keys.K };
    private static readonly Keys[] DonKeysRight = { Keys.D, Keys.F };
    private static readonly Keys[] KatKeysRight = { Keys.J, Keys.K };
    private static readonly Keys[] AllDonKeys = { Keys.D, Keys.F };
    private static readonly Keys[] AllKatKeys = { Keys.J, Keys.K };

    // ── Pause ──
    private bool _paused;
    private float _pauseAnim;
    private bool _musicWasPlayingOnPause;
    private int _pauseSelection;

    // ── Background ──
    private BackgroundManager _background = null!;

    // ── Hit explosions ──
    private struct Explosion
    {
        public double Time;
        public bool IsDon;
        public bool Active;
    }
    private Explosion[] _explosions = new Explosion[SkinConfig.MaxExplosions];
    private int _nextExplosion;

    // ── Combo milestones ──
    private int _lastMilestone;
    private double _milestoneTime;
    private string _milestoneText = "";

    // ── HP danger pulse ──
    private float _hpPulse;

    // ── Judgment bounce ──
    private float _judgmentBounce;

    // ── Transition guard ──
    private bool _quitting;

    public GameplayScreen(GameEngine engine, TaikoGame game) : base(engine, game)
    {
        _background = new BackgroundManager(engine);
    }

    /// <summary>Load a beatmap for play.</summary>
    public void LoadBeatmap(BeatmapData beatmap, bool withAudio)
    {
        _beatmap = beatmap;
        _useAudio = withAudio;
        _autoPlay = Game.AutoPlay;
        _hitWindows = new HitWindows(beatmap.OverallDifficulty);
        _score = new ScoreProcessor(beatmap.HPDrainRate, beatmap.OverallDifficulty);
        _playfield = new Playfield(Engine);
        _noteRenderer = new NoteRenderer(Engine);
        _scrollSpeed = SkinConfig.BaseScrollSpeed * beatmap.SliderMultiplier * Game.Settings.ScrollSpeed;
        _playfield.BuildBarlines(beatmap);
        _nextHitIndex = 0;
        _started = false;
        _finished = false;
        _paused = false;
        _pauseAnim = 0f;
        _musicWasPlayingOnPause = false;
        _pauseSelection = 0;
        _lastMilestone = 0;
        _milestoneTime = -9999;
        _milestoneText = "";
        _hpPulse = 0f;
        _judgmentBounce = 0f;
        _quitting = false;
        _autoNextLongTickTime = 0;
        _autoDendenDon = true;
        _lastAutoDonFlashTime = -9999;
        _lastAutoKatFlashTime = -9999;
        _nextExplosion = 0;
        for (int i = 0; i < _explosions.Length; i++)
            _explosions[i].Active = false;

        // Reset hit objects
        foreach (var ho in _beatmap.HitObjects)
        {
            ho.IsHit = false;
            ho.Result = HitResult.None;
            ho.TicksHit = 0;
        }

        // Lead-in
        _startOffset = Math.Max(2000, _beatmap.AudioLeadIn);

        // Load background image / video (dim from settings)
        _background.DimLevel = Game.Settings.BackgroundDim;
        _background.Load(beatmap);
    }

    public override void OnEnter()
    {
        if (_beatmap == null) return;

        // Try loading audio
        if (_useAudio && !string.IsNullOrEmpty(_beatmap.AudioFilename))
        {
            string audioPath = Path.Combine(_beatmap.FolderPath, _beatmap.AudioFilename);
            if (File.Exists(audioPath))
            {
                Engine.Audio.LoadMusic(audioPath);
            }
            else if (Beatmap.LazerAudioResolver.IsLazerPath(_beatmap.FilePath))
            {
                // Beatmap is from osu! lazer's hash store — try to resolve audio
                Console.WriteLine($"[Gameplay] Audio not found at expected path (lazer hash store)");
                Console.WriteLine($"[Gameplay] Attempting lazer audio resolution...");

                string? resolved = Beatmap.LazerAudioResolver.ResolveAudio(
                    _beatmap.FilePath, _beatmap.AudioFilename);

                if (resolved != null)
                {
                    Console.WriteLine($"[Gameplay] Resolved lazer audio: {resolved}");
                    Engine.Audio.LoadMusic(resolved);
                }
                else
                {
                    Console.WriteLine($"[Gameplay] Could not resolve lazer audio.");
                    Console.WriteLine($"[Gameplay] Tip: Export the map from osu! lazer (right-click → Export)");
                    Console.WriteLine($"[Gameplay]       and place the .osz file in the Songs folder.");
                    _useAudio = false;
                }
            }
            else
            {
                Console.WriteLine($"[Gameplay] Audio file not found: {audioPath}");
                _useAudio = false;
            }
        }

        // Start the clock
        _stopwatch.Restart();
        _currentTime = -_startOffset;

        if (_useAudio && Engine.Audio.IsMusicLoaded)
        {
            // Will start audio after lead-in
        }

        _started = true;
    }

    public override void OnExit()
    {
        Engine.Audio.StopMusic();
        _stopwatch.Stop();
        _background.Unload();
    }

    public override void OnEscape()
    {
        if (_finished)
        {
            QuitToSongSelect();
            return;
        }

        if (_paused)
        {
            ResumeGameplay();
            return;
        }

        PauseGameplay();
    }

    public override void Update(double deltaTime)
    {
        if (!_started) return;
        if (_paused)
        {
            UpdatePause(deltaTime);
            return;
        }

        _pauseAnim = 0f;

        // ── Update time ──
        if (_useAudio && Engine.Audio.IsPlaying)
        {
            _currentTime = Engine.Audio.MusicPosition + Game.Settings.GlobalOffset;
        }
        else
        {
            _currentTime = _stopwatch.Elapsed.TotalMilliseconds - _startOffset + Game.Settings.GlobalOffset;
        }

        // Update video background
        _background.Update(_currentTime);

        // Start audio when lead-in finishes
        if (_useAudio && Engine.Audio.IsMusicLoaded && !Engine.Audio.IsPlaying && _currentTime >= 0)
        {
            Engine.Audio.SetMusicVolume(Game.Settings.MasterVolume * Game.Settings.MusicVolume);
            Engine.Audio.PlayMusic();
        }

        // ── HP drain ──
        _score.ApplyDrain(deltaTime * 1000);

        // ── HP danger pulse ──
        if (_score.HP <= 0.3)
            _hpPulse = MathF.Min(1f, _hpPulse + (float)deltaTime * 4f);
        else
            _hpPulse = MathF.Max(0f, _hpPulse - (float)deltaTime * 3f);

        // ── Combo milestones ──
        int milestone = (_score.Combo / 50) * 50;
        if (milestone > 0 && milestone > _lastMilestone)
        {
            _lastMilestone = milestone;
            _milestoneTime = _currentTime;
            _milestoneText = $"{milestone} COMBO!";
        }
        if (_score.Combo == 0) _lastMilestone = 0;

        // ── Judgment bounce decay ──
        _judgmentBounce = MathF.Max(0f, _judgmentBounce - (float)deltaTime * 6f);

        if (_autoPlay)
            ProcessAutoPlay();

        // ── Check for missed notes ──
        for (int i = _nextHitIndex; i < _beatmap.HitObjects.Count; i++)
        {
            var ho = _beatmap.HitObjects[i];
            if (ho.IsHit) continue;

            if (ho.IsNote)
            {
                if (_hitWindows.IsMissed(ho.Time, _currentTime))
                {
                    ho.IsHit = true;
                    ho.Result = HitResult.Miss;
                    _score.ApplyHit(HitResult.Miss, _currentTime);
                    if (i == _nextHitIndex) _nextHitIndex++;
                }
            }
            else if (ho.IsLong)
            {
                // Long notes auto-complete when their end time passes
                if (_currentTime > ho.EndTime + 200)
                {
                    ho.IsHit = true;
                    if (ho.TicksHit > 0)
                        ho.Result = HitResult.Good;
                    else
                        ho.Result = HitResult.Miss;
                    if (i == _nextHitIndex) _nextHitIndex++;
                }
            }
            else
            {
                break; // Notes are sorted by time, stop early
            }
        }

        if (!_autoPlay)
        {
            // ── Process drum input ──
            bool donHit = Engine.Input.AnyPressed(AllDonKeys);
            bool katHit = Engine.Input.AnyPressed(AllKatKeys);

            if (donHit) ProcessHit(true);
            if (katHit) ProcessHit(false);

            // ── Drumroll/Denden continuous input ──
            if (donHit || katHit)
                ProcessLongNoteHit();
        }

        // ── Advance next hit index past already-hit notes ──
        while (_nextHitIndex < _beatmap.HitObjects.Count &&
               _beatmap.HitObjects[_nextHitIndex].IsHit)
        {
            _nextHitIndex++;
        }

        // ── Check if map is finished ──
        if (!_finished && !_quitting && _beatmap.HitObjects.Count > 0)
        {
            var lastObj = _beatmap.HitObjects[^1];
            double lastTime = lastObj.IsLong ? lastObj.EndTime : lastObj.Time;
            if (_currentTime > lastTime + 3000)
            {
                _finished = true;
                Game.ShowResults(_beatmap, _score);
            }
        }
    }

    private void ProcessHit(bool isDon)
    {
        // Find the closest unhit note within the hit window
        HitObject? bestNote = null;
        double bestOffset = double.MaxValue;

        for (int i = _nextHitIndex; i < _beatmap.HitObjects.Count; i++)
        {
            var ho = _beatmap.HitObjects[i];
            if (ho.IsHit || !ho.IsNote) continue;

            double offset = Math.Abs(_currentTime - ho.Time);
            if (offset > _hitWindows.Miss) break; // Notes are sorted, no point continuing

            // Check if the hit type matches
            bool noteIsDon = ho.IsDon;
            if (noteIsDon != isDon) continue;

            if (offset < bestOffset)
            {
                bestOffset = offset;
                bestNote = ho;
            }
        }

        if (bestNote != null)
        {
            bestNote.IsHit = true;
            bestNote.HitTime = _currentTime;

            HitResult result = _hitWindows.Evaluate(bestOffset);
            if (result == HitResult.None)
                result = HitResult.Miss;

            bestNote.Result = result;
            _score.ApplyHit(result, _currentTime, bestNote.IsBig);

            // Judgment bounce
            _judgmentBounce = 1f;

            // Visual feedback
            if (isDon)
                _playfield.FlashDon(_currentTime);
            else
                _playfield.FlashKat(_currentTime);

            // Spawn hit explosion
            if (result != HitResult.Miss)
            {
                ref var exp = ref _explosions[_nextExplosion % SkinConfig.MaxExplosions];
                exp.Time = _currentTime;
                exp.IsDon = isDon;
                exp.Active = true;
                _nextExplosion++;
            }
        }
        else
        {
            // Hit but no matching note — just show flash (no penalty for empty hits)
            if (isDon)
                _playfield.FlashDon(_currentTime);
            else
                _playfield.FlashKat(_currentTime);
        }
    }

    private void ProcessAutoPlay()
    {
        while (_nextHitIndex < _beatmap.HitObjects.Count)
        {
            var ho = _beatmap.HitObjects[_nextHitIndex];
            if (ho.IsHit)
            {
                _nextHitIndex++;
                continue;
            }

            if (ho.IsNote)
            {
                if (_currentTime < ho.Time)
                    break;

                AutoHitNote(ho);
                _nextHitIndex++;
                continue;
            }

            if (ho.IsLong)
            {
                if (_currentTime < ho.Time)
                    break;

                AutoHitLongNote(ho);
                if (ho.IsHit)
                {
                    _nextHitIndex++;
                    continue;
                }
                break;
            }

            break;
        }
    }

    private void AutoHitNote(HitObject ho)
    {
        ho.IsHit = true;
        ho.HitTime = _currentTime;
        ho.Result = HitResult.Great;

        _score.ApplyHit(HitResult.Great, _currentTime, ho.IsBig);
        _judgmentBounce = 1f;
        TriggerDrumVisual(ho.IsDon);
    }

    private void AutoHitLongNote(HitObject ho)
    {
        if (_autoNextLongTickTime < ho.Time || _autoNextLongTickTime > ho.EndTime)
            _autoNextLongTickTime = ho.Time;

        while (!ho.IsHit &&
               _autoNextLongTickTime <= _currentTime &&
               _autoNextLongTickTime <= ho.EndTime)
        {
            bool isDon = !ho.IsDenden || _autoDendenDon;
            TriggerDrumVisual(isDon);
            _autoDendenDon = !_autoDendenDon;

            ho.TicksHit++;
            _score.ApplyTick(_currentTime);

            if (ho.TicksHit >= ho.TicksRequired)
            {
                ho.IsHit = true;
                ho.Result = HitResult.Great;
                break;
            }

            _autoNextLongTickTime += AutoLongTickInterval;
        }

        if (!ho.IsHit && _currentTime > ho.EndTime + 200)
        {
            ho.IsHit = true;
            ho.Result = ho.TicksHit > 0 ? HitResult.Good : HitResult.Miss;
        }

        if (ho.IsHit)
            _autoNextLongTickTime = 0;
    }

    private void TriggerDrumVisual(bool isDon)
    {
        if (isDon)
        {
            _playfield.FlashDon(_currentTime);
            _lastAutoDonFlashTime = _currentTime;
        }
        else
        {
            _playfield.FlashKat(_currentTime);
            _lastAutoKatFlashTime = _currentTime;
        }

        ref var exp = ref _explosions[_nextExplosion % SkinConfig.MaxExplosions];
        exp.Time = _currentTime;
        exp.IsDon = isDon;
        exp.Active = true;
        _nextExplosion++;
    }

    private void ProcessLongNoteHit()
    {
        // Check for active drumrolls/dendens
        for (int i = 0; i < _beatmap.HitObjects.Count; i++)
        {
            var ho = _beatmap.HitObjects[i];
            if (ho.IsHit || !ho.IsLong) continue;
            if (_currentTime < ho.Time || _currentTime > ho.EndTime) continue;

            ho.TicksHit++;
            _score.ApplyTick(_currentTime);

            if (ho.TicksHit >= ho.TicksRequired)
            {
                ho.IsHit = true;
                ho.Result = HitResult.Great;
            }
            break; // Only process one long note at a time
        }
    }

    public override void Render(double deltaTime)
    {
        var batch = Engine.SpriteBatch;
        var font = Engine.Font;
        var pixel = Engine.PixelTex;
        var proj = Engine.Projection;
        int sw = Engine.ScreenWidth;
        int sh = Engine.ScreenHeight;

        batch.Begin(proj);

        // ── Background (beatmap image/video or fallback solid color) ──
        if (_background.HasBackground)
        {
            _background.Render();
        }
        else
        {
            batch.Draw(pixel, 0, 0, sw, sh, SkinConfig.BgColor);
        }

        // ── Kiai background effect ──
        if (_beatmap.IsKiaiAt(_currentTime))
        {
            batch.Draw(pixel, 0, 0, sw, sh, SkinConfig.KiaiGlow);
        }

        // ── Playfield ──
        _playfield.Render(_currentTime, _scrollSpeed);

        // ── Notes ──
        _noteRenderer.RenderNotes(_beatmap.HitObjects, _currentTime, _scrollSpeed);

        // ── Hit explosions ──
        RenderExplosions();

        // ── Judgment display ──
        RenderJudgment();

        // ── Combo milestone flash ──
        RenderMilestone();

        // ── HUD ──
        RenderHUD();

        if (_paused)
            RenderPauseOverlay(deltaTime);

        batch.End();
    }

    private void UpdatePause(double deltaTime)
    {
        _pauseAnim = MathF.Min(1f, _pauseAnim + (float)deltaTime * 7.5f);

        if (Engine.Input.IsPressed(Keys.Up) || Engine.Input.IsPressed(Keys.Left))
            _pauseSelection = (_pauseSelection + 2) % 3;
        if (Engine.Input.IsPressed(Keys.Down) || Engine.Input.IsPressed(Keys.Right))
            _pauseSelection = (_pauseSelection + 1) % 3;

        if (Engine.Input.IsPressed(Keys.R))
        {
            RestartGameplay();
            return;
        }

        if (Engine.Input.IsPressed(Keys.Enter) || Engine.Input.IsPressed(Keys.Space))
            ExecutePauseSelection();

        if (Engine.Input.IsPressed(Keys.Backspace) || Engine.Input.IsPressed(Keys.Q))
            QuitToSongSelect();
    }

    private void PauseGameplay()
    {
        _paused = true;
        _pauseSelection = 0;
        _musicWasPlayingOnPause = _useAudio && Engine.Audio.IsPlaying;
        _stopwatch.Stop();
        Engine.Audio.PauseMusic();
    }

    private void ResumeGameplay()
    {
        _paused = false;
        _pauseAnim = 0f;
        _stopwatch.Start();
        if (_musicWasPlayingOnPause && _useAudio && Engine.Audio.IsMusicLoaded)
            Engine.Audio.PlayMusic();
        _musicWasPlayingOnPause = false;
    }

    private void ExecutePauseSelection()
    {
        switch (_pauseSelection)
        {
            case 0:
                ResumeGameplay();
                break;
            case 1:
                RestartGameplay();
                break;
            default:
                QuitToSongSelect();
                break;
        }
    }

    private void RestartGameplay()
    {
        var beatmap = _beatmap;
        bool withAudio = _useAudio;
        Engine.Audio.StopMusic();
        _background.Unload();
        LoadBeatmap(beatmap, withAudio);
        OnEnter();
    }

    private void QuitToSongSelect()
    {
        if (_quitting) return;
        _quitting = true;
        Game.GoToSongSelect();
    }

    private void RenderPauseOverlay(double deltaTime)
    {
        var batch = Engine.SpriteBatch;
        var font = Engine.Font;
        var pixel = Engine.PixelTex;
        int sw = Engine.ScreenWidth;
        int sh = Engine.ScreenHeight;

        float t = EaseOutCubic(Clamp01(_pauseAnim));
        float panelW = MathF.Min(860f, sw - 180f);
        float panelH = 430f;
        float panelX = (sw - panelW) * 0.5f;
        float panelY = (sh - panelH) * 0.5f + (1f - t) * 22f;

        batch.Draw(pixel, 0, 0, sw, sh, 0f, 0f, 0f, 0.48f * t);
        batch.Draw(pixel, 0, 0, sw, sh, 0.02f, 0.022f, 0.030f, 0.18f * t);

        DrawRoundedRect(batch, panelX + 10f, panelY + 12f, panelW, panelH, 26f,
            0f, 0f, 0f, 0.28f * t);
        DrawRoundedRect(batch, panelX, panelY, panelW, panelH, 26f,
            0.030f, 0.032f, 0.042f, 0.88f * t);
        DrawRoundedRect(batch, panelX + 26f, panelY + 24f, panelW - 52f, 1f, 0.5f,
            1f, 1f, 1f, 0.08f * t);

        float leftX = panelX + 44f;
        float topY = panelY + 42f;
        string status = _autoPlay ? "AUTO MODE" : "GAME PAUSED";
        font.DrawText(batch, status, leftX, topY, 0.48f,
            SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2], 0.82f * t);

        font.DrawTextShadow(batch, "Pause", leftX, topY + 42f, 1.48f,
            0.95f, 0.96f, 1f, t, 2f);

        string song = _beatmap == null
            ? ""
            : $"{_beatmap.DisplayArtist} - {_beatmap.DisplayTitle}";
        song = TruncateToFit(font, song, 0.56f, panelW * 0.48f);
        font.DrawText(batch, song, leftX, topY + 104f, 0.56f,
            0.58f, 0.60f, 0.66f, 0.82f * t);

        DrawPauseProgress(batch, font, leftX, topY + 158f, panelW * 0.48f, t);

        float statsY = topY + 226f;
        DrawPauseStat(batch, font, leftX, statsY, "Score", $"{_score.Score:D8}", t);
        DrawPauseStat(batch, font, leftX + 176f, statsY, "Accuracy", _score.AccuracyDisplay, t);
        DrawPauseStat(batch, font, leftX + 352f, statsY, "Combo", _score.Combo.ToString(), t);

        float menuX = panelX + panelW - 322f;
        float menuY = panelY + 88f;
        font.DrawText(batch, "OPTIONS", menuX + 8f, menuY - 34f, 0.42f,
            0.42f, 0.44f, 0.50f, 0.70f * t);
        DrawPauseOption(batch, font, menuX, menuY, 0, "Resume", "Return to play", "ESC", t);
        DrawPauseOption(batch, font, menuX, menuY + 66f, 1, "Restart", "Run it back", "R", t);
        DrawPauseOption(batch, font, menuX, menuY + 132f, 2, "Song Select", "Leave this run", "BACK", t);

        string hint = "Up/Down choose    Enter confirm    Q song select";
        hint = TruncateToFit(font, hint, 0.40f, panelW - 88f);
        font.DrawText(batch, hint, leftX, panelY + panelH - 42f, 0.40f,
            0.42f, 0.44f, 0.50f, 0.72f * t);
    }

    private void DrawPauseProgress(SpriteBatch batch, Engine.Text.BitmapFont font,
        float x, float y, float w, float alpha)
    {
        double endTime = GetMapEndTime();
        float progress = endTime > 0
            ? Clamp01((float)(_currentTime / endTime))
            : 0f;

        DrawRoundedRect(batch, x, y, w, 3f, 1.5f,
            1f, 1f, 1f, 0.09f * alpha);
        DrawRoundedRect(batch, x, y, w * progress, 3f, 1.5f,
            SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2], 0.78f * alpha);

        string time = $"{FormatTime(_currentTime)} / {FormatTime(endTime)}";
        font.DrawText(batch, time, x, y + 18f, 0.42f,
            0.48f, 0.50f, 0.56f, 0.76f * alpha);
    }

    private void DrawPauseStat(SpriteBatch batch, Engine.Text.BitmapFont font,
        float x, float y, string label, string value, float alpha)
    {
        font.DrawText(batch, label, x, y, 0.42f,
            0.42f, 0.44f, 0.50f, 0.72f * alpha);
        string fitted = TruncateToFit(font, value, 0.68f, 144f);
        font.DrawText(batch, fitted, x, y + 26f, 0.68f,
            0.90f, 0.91f, 0.96f, 0.92f * alpha);
    }

    private void DrawPauseOption(SpriteBatch batch, Engine.Text.BitmapFont font,
        float x, float y, int index, string label, string subtitle, string key, float alpha)
    {
        bool selected = index == _pauseSelection;
        float w = 258f;
        float h = 54f;

        if (selected)
        {
            DrawRoundedRect(batch, x, y, w, h, 12f,
                SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2], 0.075f * alpha);
            DrawRoundedRect(batch, x, y + 10f, 3f, h - 20f, 1.5f,
                SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2], 0.88f * alpha);
            DrawRoundedRect(batch, x + 16f, y + h - 1f, w - 32f, 1f, 0.5f,
                SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2], 0.34f * alpha);
        }
        else
        {
            DrawRoundedRect(batch, x + 16f, y + h - 1f, w - 32f, 1f, 0.5f,
                1f, 1f, 1f, 0.045f * alpha);
        }

        string title = TruncateToFit(font, label, selected ? 0.68f : 0.58f, 166f);
        font.DrawText(batch, title, x + 22f, y + 8f, selected ? 0.68f : 0.58f,
            selected ? 0.96f : 0.74f,
            selected ? 0.97f : 0.76f,
            selected ? 1.00f : 0.82f,
            (selected ? 1f : 0.80f) * alpha);

        string sub = TruncateToFit(font, subtitle, 0.34f, 166f);
        font.DrawText(batch, sub, x + 22f, y + 34f, 0.34f,
            0.46f, 0.48f, 0.54f, (selected ? 0.78f : 0.56f) * alpha);

        float keyW = font.MeasureWidth(key, 0.38f);
        font.DrawText(batch, key, x + w - keyW - 18f, y + 19f, 0.38f,
            selected ? 0.92f : 0.66f,
            selected ? 0.94f : 0.68f,
            selected ? 1.00f : 0.74f,
            (selected ? 0.90f : 0.46f) * alpha);
    }

    private double GetMapEndTime()
    {
        if (_beatmap == null || _beatmap.HitObjects.Count == 0)
            return 0;

        var last = _beatmap.HitObjects[^1];
        return Math.Max(0, last.IsLong ? last.EndTime : last.Time);
    }

    private static string FormatTime(double milliseconds)
    {
        int totalSeconds = Math.Max(0, (int)(milliseconds / 1000.0));
        return $"{totalSeconds / 60}:{totalSeconds % 60:00}";
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

    private void RenderExplosions()
    {
        var batch = Engine.SpriteBatch;
        var glow = Engine.GlowTex;
        float cx = SkinConfig.HitPositionX;
        float cy = SkinConfig.PlayfieldY;

        for (int i = 0; i < _explosions.Length; i++)
        {
            ref var exp = ref _explosions[i];
            if (!exp.Active) continue;

            double age = _currentTime - exp.Time;
            if (age < 0 || age > SkinConfig.ExplosionDuration)
            {
                exp.Active = false;
                continue;
            }

            float t = (float)(age / SkinConfig.ExplosionDuration);
            float ease = 1f - (1f - t) * (1f - t); // ease-out quad
            float radius = SkinConfig.ExplosionMaxRadius * ease;
            float alpha = (1f - t) * 0.45f;

            float[] col = exp.IsDon ? SkinConfig.DonFlash : SkinConfig.KatFlash;

            // Expanding ring
            float ringSize = radius * 2f;
            batch.Draw(Engine.RingTex, cx - radius, cy - radius, ringSize, ringSize,
                col[0], col[1], col[2], alpha * 0.6f);

            // Inner glow
            float glowR = radius * 0.6f;
            batch.Draw(glow, cx - glowR, cy - glowR, glowR * 2, glowR * 2,
                col[0], col[1], col[2], alpha * 0.3f);
        }
    }

    private void RenderMilestone()
    {
        double age = _currentTime - _milestoneTime;
        if (age < 0 || age > SkinConfig.MilestoneFlashDuration || string.IsNullOrEmpty(_milestoneText))
            return;

        float t = (float)(age / SkinConfig.MilestoneFlashDuration);
        // Bounce-in then fade-out
        float scaleT = t < 0.3f ? EaseOutBack(t / 0.3f) : 1f;
        float alpha = t < 0.6f ? 1f : 1f - (t - 0.6f) / 0.4f;
        float scale = 1.5f * scaleT;
        float y = SkinConfig.PlayfieldY - SkinConfig.TrackHeight / 2f - 90f;

        Engine.Font.DrawCenteredShadow(Engine.SpriteBatch, _milestoneText,
            SkinConfig.HitPositionX, y, scale,
            SkinConfig.MilestoneColor[0], SkinConfig.MilestoneColor[1],
            SkinConfig.MilestoneColor[2], alpha, 3f);
    }

    private void RenderJudgment()
    {
        if (_score.LastJudgment == HitResult.None) return;

        double age = _currentTime - _score.LastJudgmentTime;
        if (age > SkinConfig.JudgmentFade || age < 0) return;

        float t = (float)(age / SkinConfig.JudgmentFade);
        float alpha = t < 0.7f ? 1f : 1f - (t - 0.7f) / 0.3f; // hold then fade
        float y = SkinConfig.PlayfieldY - SkinConfig.TrackHeight / 2f - 50f - t * 8f;
        float x = SkinConfig.HitPositionX;

        // Bounce easing for scale
        float bounceT = MathF.Min(1f, t * 4f);
        float bounce = bounceT < 1f ? EaseOutBack(bounceT) : 1f;
        float scale = 1.0f + bounce * 0.35f + _judgmentBounce * 0.15f;

        string text;
        float[] color;

        switch (_score.LastJudgment)
        {
            case HitResult.Great:
                text = "GREAT";
                color = SkinConfig.GreatColor;
                break;
            case HitResult.Good:
                text = "GOOD";
                color = SkinConfig.GoodColor;
                break;
            default:
                text = "MISS";
                color = SkinConfig.MissColor;
                // Shake effect for miss
                x += MathF.Sin(t * 30f) * (1f - t) * 6f;
                break;
        }

        Engine.Font.DrawCenteredShadow(Engine.SpriteBatch, text, x, y, scale,
            color[0], color[1], color[2], alpha);
    }

    private static float EaseOutBack(float t)
    {
        float c1 = 1.70158f;
        float c3 = c1 + 1f;
        float t1 = t - 1f;
        return 1f + c3 * t1 * t1 * t1 + c1 * t1 * t1;
    }

    private static float EaseOutCubic(float t)
    {
        t = Clamp01(t);
        float t1 = 1f - t;
        return 1f - t1 * t1 * t1;
    }

    private static float Clamp01(float value)
        => MathF.Max(0f, MathF.Min(1f, value));

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

    private void RenderHUD()
    {
        var batch = Engine.SpriteBatch;
        var font = Engine.Font;
        var pixel = Engine.PixelTex;
        int sw = Engine.ScreenWidth;
        int sh = Engine.ScreenHeight;
        float m = SkinConfig.HudMargin;
        float right = sw - m;

        // ── HP bar (chunky pixel bar, top) ──
        float hpW = SkinConfig.HpBarWidth;
        float hpX = right - hpW;
        float hpBarH = SkinConfig.HpBarH;
        // Border with danger pulse
        float dangerGlow = _hpPulse * (0.5f + 0.5f * MathF.Sin((float)_currentTime * 0.008f));
        if (dangerGlow > 0.01f)
        {
            batch.Draw(pixel, hpX - 4, m - 4, hpW + 8, hpBarH + 8,
                SkinConfig.HpDanger[0], SkinConfig.HpDanger[1], SkinConfig.HpDanger[2],
                dangerGlow * 0.3f);
        }
        batch.Draw(pixel, hpX - 2, m - 2, hpW + 4, hpBarH + 4, SkinConfig.NoteBorder);
        batch.Draw(pixel, hpX, m, hpW, hpBarH, SkinConfig.HpBg);
        float fill = hpW * (float)_score.HP;
        float[] hpCol = _score.HP > 0.3 ? SkinConfig.HpFill : SkinConfig.HpDanger;
        batch.Draw(pixel, hpX, m, fill, hpBarH, hpCol);
        // HP bar shine line
        batch.Draw(pixel, hpX, m, fill, 2f, 1f, 1f, 1f, 0.12f);

        // ── Score (top-right) ──
        font.DrawTextRightShadow(batch, $"{_score.Score:D8}", right, m + 16, 1.6f,
            1f, 1f, 1f, 1f);

        // ── Accuracy (below score) ──
        font.DrawTextRight(batch, _score.AccuracyDisplay, right, m + 52, 1.0f,
            0.75f, 0.70f, 0.55f, 1f);

        if (_autoPlay)
        {
            float badgeW = 82f;
            batch.Draw(pixel, m, m, badgeW, 28f,
                SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2], 0.16f);
            batch.Draw(pixel, m, m + 27f, badgeW, 1f,
                SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2], 0.62f);
            font.DrawCentered(batch, "AUTO", m + badgeW * 0.5f, m + 14f, 0.62f,
                0.86f, 0.90f, 1f, 0.95f);
        }

        // ── Combo (below drum) ──
        if (_score.Combo > 0)
        {
            float age = (float)(_currentTime - _score.ComboPopTime);
            float s = 2.2f;
            if (age >= 0 && age < SkinConfig.ComboPopMs)
            {
                float p = 1f - age / SkinConfig.ComboPopMs;
                s *= 1f + p * (SkinConfig.ComboPopScale - 1f);
            }

            // Color shifts at milestones
            float cr = 1f, cg = 0.85f, cb = 0.20f;
            if (_score.Combo >= 100) { cr = 1f; cg = 0.5f; cb = 0.2f; }     // fiery
            if (_score.Combo >= 200) { cr = 0.9f; cg = 0.2f; cb = 0.9f; }   // purple
            if (_score.Combo >= 300) { cr = 0.3f; cg = 1f; cb = 0.95f; }    // cyan

            float comboY = SkinConfig.PlayfieldY + SkinConfig.TrackHeight / 2f + 44;
            font.DrawCenteredShadow(batch, _score.Combo.ToString(),
                SkinConfig.HitPositionX, comboY, s, cr, cg, cb, 1f);

            // Small "combo" label below the number
            font.DrawCentered(batch, "COMBO", SkinConfig.HitPositionX, comboY + 26, 0.55f,
                cr, cg, cb, 0.5f);
        }

        // ── Keys (pixel boxes, bottom-left) ──
        float ky = sh - 42f;
        float kw = 28f, kh = 28f, kg = 4f, kx = 12f;
        bool autoDonOn = _autoPlay && _currentTime - _lastAutoDonFlashTime < 90;
        bool autoKatOn = _autoPlay && _currentTime - _lastAutoKatFlashTime < 90;
        Key(kx,                  ky, kw, kh, "D", Engine.Input.IsDown(Keys.D) || autoDonOn, true);
        Key(kx + kw + kg,        ky, kw, kh, "F", Engine.Input.IsDown(Keys.F) || autoDonOn, true);
        Key(kx + (kw+kg)*2 + 8,  ky, kw, kh, "J", Engine.Input.IsDown(Keys.J) || autoKatOn, false);
        Key(kx + (kw+kg)*3 + 8,  ky, kw, kh, "K", Engine.Input.IsDown(Keys.K) || autoKatOn, false);

        // ── Song (bottom, faint) ──
        font.DrawText(batch, $"{_beatmap.DisplayArtist} - {_beatmap.DisplayTitle}",
            12, sh - 16f, 0.6f, 0.40f, 0.32f, 0.22f, 1f);
    }

    private void Key(float x, float y, float w, float h, string l, bool on, bool don)
    {
        var batch = Engine.SpriteBatch;
        // Dark border
        batch.Draw(Engine.PixelTex, x - 2, y - 2, w + 4, h + 4, SkinConfig.NoteBorder);
        if (on)
        {
            float[] c = don ? SkinConfig.DonColor : SkinConfig.KatColor;
            batch.Draw(Engine.PixelTex, x, y, w, h, c);
        }
        else
        {
            batch.Draw(Engine.PixelTex, x, y, w, h, 0.16f, 0.12f, 0.08f, 0.7f);
        }
        Engine.Font.DrawCentered(batch, l, x + w/2, y + h/2, 0.7f,
            1f, 1f, 1f, on ? 1f : 0.4f);
    }

    public override void Dispose()
    {
        Engine.Audio.StopMusic();
        _background?.Dispose();
    }
}
