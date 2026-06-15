using TaikoNova.Engine;
using TaikoNova.Engine.GL;
using TaikoNova.Game.Beatmap;
using TaikoNova.Game.Skin;

namespace TaikoNova.Game.Screens;

/// <summary>
/// Minimal pre-level screen with full-bleed beatmap art, restrained
/// typography, and a quiet progress transition.
/// </summary>
public class LoadingScreen : Screen
{
    private BeatmapData? _beatmap;
    private bool _isPractice;
    private bool _withAudio;

    private BackgroundManager _background;

    private double _time;
    private float _fadeIn;
    private float _fadeOut;
    private bool _transitioning;
    private bool _loaded;
    private float _progressBar;
    private float _titleScale;

    private const float MinShowSeconds = 2.2f;
    private const float FadeInSpeed    = 3.5f;
    private const float FadeOutSpeed   = 4.0f;

    public LoadingScreen(GameEngine engine, TaikoGame game) : base(engine, game)
    {
        _background = new BackgroundManager(engine);
        _background.DimLevel = 0.78f;
    }

    public void SetBeatmap(BeatmapData beatmap, bool withAudio)
    {
        _beatmap = beatmap;
        _isPractice = false;
        _withAudio = withAudio;
    }

    public void SetPractice(BeatmapData beatmap)
    {
        _beatmap = beatmap;
        _isPractice = true;
        _withAudio = false;
    }

    public override void OnEnter()
    {
        _time = 0;
        _fadeIn = 0;
        _fadeOut = 0;
        _transitioning = false;
        _loaded = false;
        _progressBar = 0;
        _titleScale = 0;

        _background.Unload();
        if (_beatmap != null && !_isPractice)
            _background.Load(_beatmap);
    }

    public override void OnExit() => _background.Unload();

    public override void Update(double dt)
    {
        _time += dt;
        float fdt = (float)dt;

        _fadeIn = MathF.Min(1f, _fadeIn + fdt * FadeInSpeed);
        _titleScale = MathF.Min(1f, _titleScale + fdt * 4.5f);

        float target = _transitioning ? 1f : MathF.Min(0.9f, (float)(_time / MinShowSeconds));
        _progressBar = Lerp(_progressBar, target, fdt * 3f);

        if (_time > 0.1 && !_loaded) _loaded = true;

        if (!_transitioning && _loaded && _time >= MinShowSeconds)
            _transitioning = true;

        if (_time > 0.5f && !_transitioning)
        {
            var input = Engine.Input;
            if (input.IsPressed(OpenTK.Windowing.GraphicsLibraryFramework.Keys.Enter) ||
                input.IsPressed(OpenTK.Windowing.GraphicsLibraryFramework.Keys.Space))
                _transitioning = true;
        }

        if (_transitioning)
        {
            _progressBar = Lerp(_progressBar, 1f, fdt * 8f);
            _fadeOut = MathF.Min(1f, _fadeOut + fdt * FadeOutSpeed);
            if (_fadeOut >= 1f) Game.FinishLoading();
        }
    }

    public override void Render(double dt)
    {
        if (_beatmap == null) return;

        var batch = Engine.SpriteBatch;
        var font  = Engine.Font;
        var px    = Engine.PixelTex;
        var proj  = Engine.Projection;
        int sw    = Engine.ScreenWidth;
        int sh    = Engine.ScreenHeight;

        float fadeA = EaseOutCubic(_fadeIn) * (1f - EaseOutCubic(_fadeOut));
        float titleT = EaseOutCubic(MathF.Min(1f, (float)_time * 2.4f));
        float subT = EaseOutCubic(Math.Clamp(((float)_time - 0.12f) * 2.2f, 0f, 1f));
        float detailT = EaseOutCubic(Math.Clamp(((float)_time - 0.26f) * 2.0f, 0f, 1f));
        float progressT = EaseOutCubic(Math.Clamp(((float)_time - 0.40f) * 1.8f, 0f, 1f));

        batch.Begin(proj);

        if (_background.HasBackground)
        {
            _background.Render();
        }
        else
        {
            batch.Draw(px, 0, 0, sw, sh, 0.018f, 0.019f, 0.026f, 1f);
        }

        batch.Draw(px, 0, 0, sw, sh, 0f, 0f, 0f, 0.28f * fadeA);
        batch.Draw(px, 0, 0, sw, 132f, 0f, 0f, 0f, 0.28f * fadeA);
        batch.Draw(px, 0, sh - 190f, sw, 190f, 0f, 0f, 0f, 0.38f * fadeA);

        font.DrawText(batch, "TaikoNova", 42f, 34f, 0.76f,
            0.90f, 0.91f, 0.96f, 0.84f * fadeA);

        string state = _transitioning ? "ready" : "loading";
        font.DrawTextRight(batch, state, sw - 42f, 34f, 0.58f,
            0.56f, 0.58f, 0.64f, 0.76f * fadeA);

        float centerX = sw * 0.5f;
        float baseY = 266f + (1f - titleT) * 18f;

        string title = _isPractice ? "Practice Mode" : _beatmap.DisplayTitle;
        float titleScale = 1.54f;
        float maxTitleW = MathF.Max(220f, sw - 300f);
        FitText(font, title, ref titleScale, maxTitleW, 0.94f);
        if (font.MeasureWidth(title, titleScale) > maxTitleW)
            title = TruncateToFit(font, title, titleScale, maxTitleW);
        float titleW = font.MeasureWidth(title, titleScale);
        font.DrawTextShadow(batch, title, centerX - titleW * 0.5f, baseY, titleScale,
            0.96f, 0.97f, 1f, fadeA * titleT, 3f);

        float underlineW = MathF.Min(titleW, 340f) * titleT;
        if (underlineW > 2f)
        {
            batch.Draw(px, centerX - underlineW * 0.5f,
                baseY + font.MeasureHeight(titleScale) + 10f, underlineW, 2f,
                SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2],
                0.46f * fadeA);
        }

        float y = baseY + font.MeasureHeight(titleScale) + 34f;
        string subtitle = _isPractice ? "Generated patterns / 160 BPM" : _beatmap.DisplayArtist;
        subtitle = TruncateToFit(font, subtitle, 0.72f, MathF.Max(180f, sw - 380f));
        float subtitleW = font.MeasureWidth(subtitle, 0.72f);
        font.DrawText(batch, subtitle, centerX - subtitleW * 0.5f,
            y + (1f - subT) * 8f, 0.72f,
            0.62f, 0.64f, 0.70f, 0.88f * fadeA * subT);

        y += 58f;
        string detail = _isPractice
            ? "Warmup    No audio"
            : BuildDetailLine();
        detail = TruncateToFit(font, detail, 0.54f, MathF.Max(160f, sw - 460f));
        float detailW = font.MeasureWidth(detail, 0.54f);
        font.DrawText(batch, detail, centerX - detailW * 0.5f,
            y + (1f - detailT) * 6f, 0.54f,
            0.46f, 0.48f, 0.54f, 0.82f * fadeA * detailT);

        float barW = MathF.Min(620f, MathF.Max(180f, sw - 420f));
        float barX = (sw - barW) * 0.5f;
        float barY = sh - 112f;
        batch.Draw(px, barX, barY, barW, 2f,
            0.22f, 0.23f, 0.28f, 0.48f * fadeA * progressT);

        float fillW = barW * _progressBar * progressT;
        if (fillW > 1f)
            batch.Draw(px, barX, barY, fillW, 2f,
                SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2],
                0.76f * fadeA);

        if (!_transitioning && _time > 1.0f)
        {
            float hintT = EaseOutCubic(Math.Clamp(((float)_time - 1f) * 2.4f, 0f, 1f));
            string hint = "enter to skip";
            float hintW = font.MeasureWidth(hint, 0.44f);
            font.DrawText(batch, hint, centerX - hintW * 0.5f, barY + 22f, 0.44f,
                0.40f, 0.42f, 0.48f, hintT * 0.58f * fadeA);
        }

        if (_fadeOut > 0.2f)
        {
            float t = (_fadeOut - 0.2f) / 0.8f;
            batch.Draw(px, 0, 0, sw, sh, 0f, 0f, 0f, EaseOutCubic(t));
        }

        batch.End();
    }

    // ── Beatmap info ──

    private void RenderBeatmap(SpriteBatch batch, Engine.Text.BitmapFont font,
        Texture2D px, float cx, float cw, float cy, float bounce, float fadeA)
    {
        // Title
        float a0 = RowAlpha(0) * fadeA;
        string title = _beatmap!.DisplayTitle;
        float ts = 1.2f * bounce;
        float tw = font.MeasureWidth(title, ts);
        if (tw > cw) { ts *= cw / tw; tw = font.MeasureWidth(title, ts); }
        float slide0 = (1f - EaseOutCubic(MathF.Min(1f, a0))) * 12f;
        font.DrawTextShadow(batch, title, cx + (cw - tw) * 0.5f + slide0, cy, ts,
            1f, 1f, 1f, a0, 2f);
        cy += font.MeasureHeight(ts) + 4;

        // Artist
        float a1 = RowAlpha(1) * fadeA;
        string artist = _beatmap.DisplayArtist;
        float as_ = 0.8f;
        float aw = font.MeasureWidth(artist, as_);
        if (aw > cw) { as_ *= cw / aw; aw = font.MeasureWidth(artist, as_); }
        float slide1 = (1f - EaseOutCubic(MathF.Min(1f, a1))) * 10f;
        font.DrawText(batch, artist, cx + (cw - aw) * 0.5f + slide1, cy, as_,
            0.6f, 0.6f, 0.7f, a1);
        cy += font.MeasureHeight(as_) + 12;

        // Divider
        float a2 = RowAlpha(2) * fadeA;
        float divReveal = EaseOutCubic(MathF.Min(1f, (float)(_time * 2.0f)));
        float divW = cw * 0.5f * divReveal;
        batch.Draw(px, cx + (cw - divW) * 0.5f, cy, divW, 1f,
            SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2], 0.55f * a2);
        cy += 14;

        // Detail rows
        int row = 3;
        if (!string.IsNullOrEmpty(_beatmap.Version))
            cy = DetailRow(batch, font, cx, cy, cw, "Difficulty", _beatmap.Version,
                RowAlpha(row++) * fadeA, SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2]);

        if (!string.IsNullOrEmpty(_beatmap.Creator))
            cy = DetailRow(batch, font, cx, cy, cw, "Mapper", _beatmap.Creator,
                RowAlpha(row++) * fadeA);

        {
            float[] c = GetOdColor(_beatmap.OverallDifficulty);
            cy = DetailRow(batch, font, cx, cy, cw, "OD", $"{_beatmap.OverallDifficulty:F1}",
                RowAlpha(row++) * fadeA, c[0], c[1], c[2]);
        }

        cy = DetailRow(batch, font, cx, cy, cw, "Objects", $"{_beatmap.HitObjects.Count}",
            RowAlpha(row++) * fadeA);

        if (_beatmap.TimingPoints.Count > 0)
        {
            double bpm = 60000.0 / _beatmap.TimingPoints[0].BeatLength;
            cy = DetailRow(batch, font, cx, cy, cw, "BPM", $"{bpm:F0}",
                RowAlpha(row++) * fadeA);
        }
    }

    // ── Practice info ──

    private void RenderPractice(SpriteBatch batch, Engine.Text.BitmapFont font,
        Texture2D px, float cx, float cw, float cy, float bounce, float fadeA)
    {
        float a0 = RowAlpha(0) * fadeA;
        string title = "Practice Mode";
        float ts = 1.2f * bounce;
        float tw = font.MeasureWidth(title, ts);
        float slide0 = (1f - EaseOutCubic(MathF.Min(1f, a0))) * 12f;
        font.DrawTextShadow(batch, title, cx + (cw - tw) * 0.5f + slide0, cy, ts,
            SkinConfig.DrumrollColor[0], SkinConfig.DrumrollColor[1],
            SkinConfig.DrumrollColor[2], a0, 2f);
        cy += font.MeasureHeight(ts) + 4;

        float a1 = RowAlpha(1) * fadeA;
        string sub = "Auto-generated patterns";
        float sw2 = font.MeasureWidth(sub, 0.7f);
        font.DrawText(batch, sub, cx + (cw - sw2) * 0.5f, cy, 0.7f,
            0.5f, 0.5f, 0.6f, a1);
        cy += font.MeasureHeight(0.7f) + 12;

        float a2 = RowAlpha(2) * fadeA;
        float divW = cw * 0.4f * EaseOutCubic(MathF.Min(1f, (float)(_time * 2f)));
        batch.Draw(px, cx + (cw - divW) * 0.5f, cy, divW, 1f,
            SkinConfig.DrumrollColor[0], SkinConfig.DrumrollColor[1],
            SkinConfig.DrumrollColor[2], 0.5f * a2);
        cy += 14;

        cy = DetailRow(batch, font, cx, cy, cw, "BPM", "160", RowAlpha(3) * fadeA);
        cy = DetailRow(batch, font, cx, cy, cw, "Duration", "60s", RowAlpha(4) * fadeA);
    }

    // ── Helpers ──

    private float DetailRow(SpriteBatch batch, Engine.Text.BitmapFont font,
        float x, float y, float w, string label, string value, float fadeA,
        float vr = 0.85f, float vg = 0.85f, float vb = 0.9f)
    {
        if (fadeA < 0.01f) return y + 24f;

        float slide = (1f - EaseOutCubic(MathF.Min(1f, fadeA))) * 10f;
        float labelScale = 0.6f;
        float valScale   = 0.7f;

        string lbl = $"{label}:";
        float lw = font.MeasureWidth(lbl, labelScale);
        float vw = font.MeasureWidth(value, valScale);
        float gap = 10f;
        float totalW = lw + gap + vw;
        float sx = x + (w - totalW) * 0.5f + slide;

        font.DrawText(batch, lbl, sx, y, labelScale,
            0.4f, 0.4f, 0.48f, fadeA * 0.8f);
        font.DrawText(batch, value, sx + lw + gap, y, valScale,
            vr, vg, vb, fadeA);

        return y + 24f;
    }

    private float RowAlpha(int row)
    {
        float delay = row * 0.08f + 0.15f;
        float t = MathF.Max(0f, ((float)_time - delay) / 0.28f);
        return MathF.Min(1f, t);
    }

    private string BuildDetailLine()
    {
        if (_beatmap == null) return "";

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(_beatmap.Version))
            parts.Add(_beatmap.Version);
        parts.Add($"OD {_beatmap.OverallDifficulty:F1}");

        if (_beatmap.TimingPoints.Count > 0)
        {
            double bpm = 60000.0 / _beatmap.TimingPoints[0].BeatLength;
            parts.Add($"{bpm:F0} BPM");
        }

        if (_beatmap.HitObjects.Count > 0)
            parts.Add($"{_beatmap.HitObjects.Count} objects");

        return string.Join("    ", parts);
    }

    private static void FitText(Engine.Text.BitmapFont font, string text,
        ref float scale, float maxWidth, float minScale)
    {
        while (scale > minScale && font.MeasureWidth(text, scale) > maxWidth)
            scale -= 0.05f;
    }

    private static string TruncateToFit(Engine.Text.BitmapFont font, string text,
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

    private static float[] GetOdColor(float od)
    {
        if (od <= 4) return new[] { 0.3f, 0.85f, 0.4f, 1f };
        if (od <= 6) return new[] { 0.9f, 0.85f, 0.2f, 1f };
        if (od <= 8) return new[] { 0.95f, 0.5f, 0.15f, 1f };
        return new[] { 0.9f, 0.25f, 0.2f, 1f };
    }

    private static float Lerp(float a, float b, float t)
        => a + (b - a) * MathF.Min(1f, t);

    private static float EaseOutCubic(float t)
    {
        float t1 = 1f - MathF.Max(0f, MathF.Min(1f, t));
        return 1f - t1 * t1 * t1;
    }

    private static float EaseOutBack(float t)
    {
        t = MathF.Max(0f, MathF.Min(1f, t));
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;
        float t1 = t - 1f;
        return 1f + c3 * t1 * t1 * t1 + c1 * t1 * t1;
    }

    public override void OnEscape() => Game.GoToSongSelect();

    public override void Dispose() => _background?.Dispose();
}
