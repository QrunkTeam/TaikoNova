using OpenTK.Windowing.GraphicsLibraryFramework;
using TaikoNova.Engine;
using TaikoNova.Engine.GL;
using TaikoNova.Game.Beatmap;
using TaikoNova.Game.Skin;

namespace TaikoNova.Game.Screens;

/// <summary>
/// Level select with a browse rail, selected-song hero panel, smooth
/// scrolling, search filtering, audio previews, random select, and
/// exit animation.
/// </summary>
public class SongSelectScreen : Screen
{
    // ── Data ──
    private sealed class BeatmapGroup
    {
        public string Key { get; init; } = "";
        public List<int> BeatmapIndices { get; } = new();
        public int SelectedDifficultyIndex;
    }

    private List<BeatmapInfo> _beatmaps = new();
    private List<BeatmapGroup> _beatmapGroups = new();
    private List<int> _filteredIndices = new(); // indices into _beatmapGroups that match filter
    private int _selectedFilterIdx;             // index into _filteredIndices
    private bool _scanned;
    private string _songsPath = "";

    /// <summary>Set by file drop import to trigger a rescan on next update.</summary>
    public volatile bool NeedsRescan;

    // ── Search / filter ──
    private string _searchQuery = "";
    private float _searchBarReveal;           // 0→1 reveal animation
    private bool _searchActive;               // whether search bar is visible
    private float _searchCursorBlink;         // cursor blink timer

    // ── Animation state ──
    private float _scrollOffset;
    private float _targetScroll;
    private float _selectGlow;        // pulsing glow on selected card (0-1)
    private float _enterAnim;         // screen enter fade-in (0→1)
    private double _time;             // running clock for animations
    private float[] _cardSlide;       // per-card horizontal slide-in offset
    private float _logoWobble;        // subtle title animation
    private int _prevSelectedIndex;   // for info panel reveal on selection change
    private int _prevSelectedFilterIdx;
    private int _selectionDirection = 1;
    private float _infoReveal;        // info panel content reveal (0→1)
    private float _selectionFlash;    // brief flash on selection change
    private float _difficultyChipY = 380f;

    // ── Exit animation ──
    private bool _exiting;
    private float _exitAnim;          // 0→1, fade out when confirming
    private string _exitFilePath = "";
    private bool _exitPractice;

    // ── Audio preview ──
    private int _previewLoadedIndex = -1;
    private float _previewVolume;     // fade in 0→1
    private bool _previewPlaying;
    private double _previewDelay;     // delay before starting preview (seconds)

    // ── Background ──
    private BackgroundManager _background;
    private int _bgLoadedIndex = -1;
    private readonly Texture2D _uiCircle;

    // ── Key repeat ──
    private double _repeatTimer;
    private int _repeatDir;
    private const double RepeatDelay = 0.35;
    private const double RepeatRate  = 0.06;

    // ── Layout constants ──
    private const float TopBarH     = 76f;
    private const float BottomBarH  = 52f;
    private const float CardH       = 62f;
    private const float CardGap     = 8f;
    private const float CardItemH   = CardH + CardGap;
    private const float ListPadX    = 16f;
    private const float InfoPanelPct= 0.38f;
    private const float CardRadius  = 6f;
    private const float SearchBarH  = 42f;

    public IReadOnlyList<BeatmapInfo> Beatmaps => _beatmaps;

    public SongSelectScreen(GameEngine engine, TaikoGame game) : base(engine, game)
    {
        _cardSlide = Array.Empty<float>();
        _prevSelectedIndex = -1;
        _background = new BackgroundManager(engine);
        _background.DimLevel = 0.82f; // overridden by settings when available
        _uiCircle = Texture2D.CreateCircle(96);
    }

    public override void OnEnter()
    {
        if (!_scanned)
        {
            ScanForBeatmaps();
            _scanned = true;
        }
        _enterAnim = 0f;
        _infoReveal = 0f;
        _selectionFlash = 0f;
        _exiting = false;
        _exitAnim = 0f;
        _prevSelectedIndex = SelectedBeatmapIndex;
        _searchQuery = "";
        _searchActive = false;
        _searchBarReveal = 0f;
        RebuildFilter();
        _prevSelectedFilterIdx = _selectedFilterIdx;
        _selectionDirection = 1;
        int total = _filteredIndices.Count + 1;
        _cardSlide = new float[total];
        for (int i = 0; i < _cardSlide.Length; i++)
            _cardSlide[i] = 120f + i * 18f;
        // Resume or start audio preview
        _previewLoadedIndex = -1;
        _previewPlaying = false;
        _previewVolume = 0f;
        _previewDelay = 0.6; // brief delay before first preview
    }

    public override void OnExit()
    {
        // Stop preview audio when leaving
        StopPreview();
    }

    /// <summary>The actual beatmap index (into _beatmaps) of the current selection, or _beatmaps.Count for practice.</summary>
    private int SelectedBeatmapIndex
    {
        get
        {
            if (_selectedFilterIdx < 0 || _selectedFilterIdx >= _filteredIndices.Count)
                return _beatmaps.Count; // practice mode
            int groupIdx = _filteredIndices[_selectedFilterIdx];
            if (groupIdx < 0 || groupIdx >= _beatmapGroups.Count)
                return _beatmaps.Count;

            var group = _beatmapGroups[groupIdx];
            if (group.BeatmapIndices.Count == 0)
                return _beatmaps.Count;

            group.SelectedDifficultyIndex = Math.Clamp(
                group.SelectedDifficultyIndex, 0, group.BeatmapIndices.Count - 1);
            return group.BeatmapIndices[group.SelectedDifficultyIndex];
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Beatmap scanning (unchanged logic)
    // ═══════════════════════════════════════════════════════════════════

    private void ScanForBeatmaps()
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
            {
                _songsPath = path;
                Console.WriteLine($"[SongSelect] Local Songs: {path}");
                AddFromDirectory(path, seen);
            }
        }

        foreach (string stableSongs in OsuInstallDetector.FindStableSongsPaths())
        {
            string full;
            try { full = Path.GetFullPath(stableSongs); }
            catch { continue; }
            if (!seen.Add(full)) continue;
            Console.WriteLine($"[SongSelect] osu! stable Songs: {full}");
            AddFromDirectory(full, seen);
        }

        var lazerFiles = OsuInstallDetector.FindLazerOsuFiles();
        if (lazerFiles.Count > 0)
        {
            Console.WriteLine($"[SongSelect] Importing {lazerFiles.Count} maps from osu! lazer...");
            foreach (string osuFile in lazerFiles)
            {
                if (seen.Contains(osuFile)) continue;
                AddSingleFile(osuFile);
            }
            Console.WriteLine($"[SongSelect] Lazer import done ({_beatmaps.Count} total maps)");
        }

        string[] extraDirs = {
            Environment.CurrentDirectory,
            AppDomain.CurrentDomain.BaseDirectory
        };

        if (string.IsNullOrEmpty(_songsPath))
            _songsPath = Path.Combine(Environment.CurrentDirectory, "Songs");
        Directory.CreateDirectory(_songsPath);

        foreach (string dir in extraDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string oszFile in Directory.GetFiles(dir, "*.osz"))
                BeatmapDecoder.ExtractOsz(oszFile, _songsPath);
        }

        if (_beatmaps.Count == 0 && Directory.Exists(_songsPath))
        {
            var fresh = BeatmapDecoder.ScanSongsDirectory(_songsPath);
            foreach (var b in fresh)
                if (!seen.Contains(b.FilePath))
                    _beatmaps.Add(b);
        }

        foreach (string dir in extraDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string osuFile in Directory.GetFiles(dir, "*.osu", SearchOption.AllDirectories))
            {
                if (_beatmaps.Any(b => string.Equals(b.FilePath, osuFile, StringComparison.OrdinalIgnoreCase)))
                    continue;
                AddSingleFile(osuFile);
            }
        }

        // Sort by artist then title for a cleaner list
        _beatmaps.Sort((a, b) =>
        {
            int c = string.Compare(a.Artist, b.Artist, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            c = string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            return a.OD.CompareTo(b.OD);
        });

        BuildBeatmapGroups();
    }

    private void BuildBeatmapGroups()
    {
        _beatmapGroups.Clear();
        var byKey = new Dictionary<string, BeatmapGroup>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < _beatmaps.Count; i++)
        {
            var bm = _beatmaps[i];
            string key = GetGroupKey(bm);
            if (!byKey.TryGetValue(key, out var group))
            {
                group = new BeatmapGroup { Key = key };
                byKey.Add(key, group);
                _beatmapGroups.Add(group);
            }
            group.BeatmapIndices.Add(i);
        }

        foreach (var group in _beatmapGroups)
        {
            group.BeatmapIndices.Sort((a, b) =>
            {
                int c = _beatmaps[a].OD.CompareTo(_beatmaps[b].OD);
                return c != 0
                    ? c
                    : string.Compare(_beatmaps[a].Version, _beatmaps[b].Version,
                        StringComparison.OrdinalIgnoreCase);
            });
            group.SelectedDifficultyIndex = Math.Clamp(
                group.SelectedDifficultyIndex, 0, Math.Max(0, group.BeatmapIndices.Count - 1));
        }

        _beatmapGroups.Sort((a, b) =>
        {
            var ba = GetGroupPrimary(a);
            var bb = GetGroupPrimary(b);
            int c = string.Compare(ba?.Artist, bb?.Artist, StringComparison.OrdinalIgnoreCase);
            return c != 0
                ? c
                : string.Compare(ba?.Title, bb?.Title, StringComparison.OrdinalIgnoreCase);
        });
    }

    private void AddFromDirectory(string path, HashSet<string> seen)
    {
        var found = BeatmapDecoder.ScanSongsDirectory(path);
        foreach (var b in found)
            if (seen.Add(b.FilePath))
                _beatmaps.Add(b);
        if (found.Count > 0)
            Console.WriteLine($"[SongSelect]   -> {found.Count} beatmaps");
    }

    private void AddSingleFile(string osuFile)
    {
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

    // ═══════════════════════════════════════════════════════════════════
    //  Update
    // ═══════════════════════════════════════════════════════════════════

    public override void Update(double dt)
    {
        _time += dt;
        var input = Engine.Input;
        int totalItems = _filteredIndices.Count + 1; // +1 practice mode

        // ── Exit animation ──
        if (_exiting)
        {
            _exitAnim = MathF.Min(1f, _exitAnim + (float)dt * 4f);
            // When exit animation completes, actually transition
            if (_exitAnim >= 1f)
            {
                _exiting = false;
                if (_exitPractice)
                    Game.StartPractice();
                else
                    Game.StartBeatmap(_exitFilePath);
            }
            return; // block all input during exit
        }

        // ── Enter animation ──
        _enterAnim = MathF.Min(1f, _enterAnim + (float)dt * 3.5f);

        // ── Card slide-in animation ──
        if (_cardSlide.Length != totalItems)
            _cardSlide = new float[totalItems];
        for (int i = 0; i < _cardSlide.Length; i++)
            _cardSlide[i] = Lerp(_cardSlide[i], 0f, (float)dt * (8f + i * 0.3f));

        // ── Search input ──
        if (input.IsPressed(Keys.Tab))
        {
            _searchActive = !_searchActive;
            if (!_searchActive && _searchQuery.Length == 0)
                _searchBarReveal = 0f;
        }

        // Typing starts search automatically (exclude space — it's a confirm key)
        if (!_searchActive && input.TextInput.Count > 0)
        {
            foreach (char c in input.TextInput)
            {
                if (c > 32 && c < 127) // > 32 to exclude space
                {
                    _searchActive = true;
                    break;
                }
            }
        }

        if (_searchActive)
        {
            _searchBarReveal = MathF.Min(1f, _searchBarReveal + (float)dt * 8f);
            _searchCursorBlink = (float)((_time * 2.0) % 1.0);

            bool changed = false;
            foreach (char c in input.TextInput)
            {
                if (c >= 32 && c < 127)
                {
                    _searchQuery += c;
                    changed = true;
                }
            }
            if (input.IsPressed(Keys.Backspace))
            {
                if (_searchQuery.Length > 0)
                {
                    _searchQuery = _searchQuery[..^1];
                    changed = true;
                }
                else
                {
                    _searchActive = false;
                }
            }
            if (input.IsPressed(Keys.Escape))
            {
                if (_searchQuery.Length > 0)
                {
                    _searchQuery = "";
                    changed = true;
                }
                else
                {
                    _searchActive = false;
                }
            }

            if (changed)
            {
                int prevGroupIdx = SelectedGroupIndex;
                RebuildFilter();
                // Try to stay on the same beatmap after filtering
                _selectedFilterIdx = 0;
                for (int i = 0; i < _filteredIndices.Count; i++)
                {
                    if (_filteredIndices[i] == prevGroupIdx)
                    {
                        _selectedFilterIdx = i;
                        break;
                    }
                }
                totalItems = _filteredIndices.Count + 1;
                _cardSlide = new float[totalItems];
                for (int i = 0; i < _cardSlide.Length; i++)
                    _cardSlide[i] = 40f + i * 8f;
            }
        }
        else
        {
            _searchBarReveal = MathF.Max(0f, _searchBarReveal - (float)dt * 6f);
        }

        // ── Navigation with key repeat ──
        // Don't process navigation keys if search is active and typing
        bool navAllowed = !_searchActive || true; // allow nav even while searching

        bool upPressed = input.IsPressed(Keys.Up);
        bool downPressed = input.IsPressed(Keys.Down);
        bool upHeld = input.IsDown(Keys.Up);
        bool downHeld = input.IsDown(Keys.Down);

        if (!IsPracticeItem(_selectedFilterIdx))
        {
            if (input.IsPressed(Keys.Left))
                MoveDifficulty(-1);
            if (input.IsPressed(Keys.Right))
                MoveDifficulty(1);
        }

        int selectionStep = Game.LevelSelectGridLayout
            ? Math.Max(1, GetGridLayout(Engine.ScreenWidth, Engine.ScreenHeight, totalItems).Columns)
            : 1;

        if (upPressed)
        {
            MoveSelection(-selectionStep);
            _repeatDir = -selectionStep;
            _repeatTimer = RepeatDelay;
        }
        else if (downPressed)
        {
            MoveSelection(selectionStep);
            _repeatDir = selectionStep;
            _repeatTimer = RepeatDelay;
        }

        // Key repeat while held
        if (_repeatDir != 0)
        {
                bool held = _repeatDir < 0 ? upHeld : downHeld;
            if (!held)
            {
                _repeatDir = 0;
            }
            else
            {
                _repeatTimer -= dt;
                while (_repeatTimer <= 0)
                {
                    MoveSelection(_repeatDir);
                    _repeatTimer += RepeatRate;
                }
            }
        }

        // ── Mouse scroll wheel ──
        float scroll = input.ScrollDelta;
        if (MathF.Abs(scroll) > 0.01f)
        {
            float scrollScale = Game.LevelSelectGridLayout
                ? Math.Max(1, GetGridLayout(Engine.ScreenWidth, Engine.ScreenHeight, totalItems).Columns)
                : 3f;
            int steps = -(int)MathF.Round(scroll * scrollScale);
            if (steps != 0)
                MoveSelection(steps);
        }

        if (input.MousePressed)
        {
            if (Game.LevelSelectGridLayout)
                TrySelectGridItemAt(input.MouseX, input.MouseY);
            else if (!TrySelectDifficultyAt(input.MouseX, input.MouseY))
                TrySelectQueueItemAt(input.MouseX, input.MouseY);
        }

        // ── Page Up / Page Down ──
        if (input.IsPressed(Keys.PageUp))
            MoveSelection(-8);
        if (input.IsPressed(Keys.PageDown))
            MoveSelection(8);
        if (input.IsPressed(Keys.Home))
        {
            _selectedFilterIdx = 0;
            ResetCardSlides();
        }
        if (input.IsPressed(Keys.End))
        {
            _selectedFilterIdx = totalItems - 1;
            ResetCardSlides();
        }

        // ── F2 = Random select (osu! convention) ──
        if (input.IsPressed(Keys.F2) && _filteredIndices.Count > 1)
        {
            int r;
            do { r = Random.Shared.Next(_filteredIndices.Count); }
            while (r == _selectedFilterIdx && _filteredIndices.Count > 1);
            _selectedFilterIdx = r;
            ResetCardSlides();
        }

        // ── Confirm (with exit animation) ──
        bool confirmPressed = input.IsPressed(Keys.Enter)
            || (!_searchActive && input.IsPressed(Keys.Space));
        if (confirmPressed)
        {
            int bmIdx = SelectedBeatmapIndex;
            _exiting = true;
            _exitAnim = 0f;
            if (bmIdx >= _beatmaps.Count)
            {
                _exitPractice = true;
            }
            else
            {
                _exitPractice = false;
                _exitFilePath = _beatmaps[bmIdx].FilePath;
            }
            _previewPlaying = false;
        }

        // ── Refresh ──
        if (input.IsPressed(Keys.F5) || NeedsRescan)
        {
            NeedsRescan = false;
            _scanned = false;
            ScanForBeatmaps();
            _searchQuery = "";
            _searchActive = false;
            RebuildFilter();
            _selectedFilterIdx = 0;
            totalItems = _filteredIndices.Count + 1;
            _cardSlide = new float[totalItems];
            for (int i = 0; i < _cardSlide.Length; i++)
                _cardSlide[i] = 120f + i * 18f;
            _previewLoadedIndex = -1;
        }

        // ── Smooth scrolling ──
        float maxScroll;
        if (Game.LevelSelectGridLayout)
        {
            var grid = GetGridLayout(Engine.ScreenWidth, Engine.ScreenHeight, totalItems);
            int row = Math.Max(0, _selectedFilterIdx) / Math.Max(1, grid.Columns);
            int rows = (int)MathF.Ceiling(totalItems / (float)Math.Max(1, grid.Columns));
            _targetScroll = row * grid.Pitch - grid.Height * 0.28f;
            maxScroll = MathF.Max(0f, rows * grid.Pitch - grid.Height + grid.Gap);
        }
        else
        {
            float listH = Engine.ScreenHeight - TopBarH - BottomBarH - 114f
                         - (_searchBarReveal > 0.01f ? SearchBarH * _searchBarReveal : 0f);
            _targetScroll = _selectedFilterIdx * CardItemH - listH * 0.35f;
            maxScroll = MathF.Max(0, totalItems * CardItemH - listH + CardItemH);
        }
        _targetScroll = MathF.Max(0, MathF.Min(_targetScroll, maxScroll));
        _scrollOffset = Lerp(_scrollOffset, _targetScroll, (float)dt * 12f);

        // ── Glow pulse ──
        _selectGlow = (MathF.Sin((float)_time * 3.5f) + 1f) * 0.5f;

        // ── Logo wobble ──
        _logoWobble = MathF.Sin((float)_time * 1.2f) * 2f;

        // ── Selection reveal on change ──
        int currentBmIdx = SelectedBeatmapIndex;
        bool selectionChanged = currentBmIdx != _prevSelectedIndex
            || _selectedFilterIdx != _prevSelectedFilterIdx;
        if (selectionChanged)
        {
            int dir = Math.Sign(_selectedFilterIdx - _prevSelectedFilterIdx);
            if (dir != 0)
                _selectionDirection = dir;
            _infoReveal = 0f;
            _selectionFlash = 1f;
            _prevSelectedIndex = currentBmIdx;
            _prevSelectedFilterIdx = _selectedFilterIdx;
            _previewDelay = 0.4; // delay before starting new preview
        }
        _infoReveal = MathF.Min(1f, _infoReveal + (float)dt * SkinConfig.InfoRevealSpeed);
        _selectionFlash = MathF.Max(0f, _selectionFlash - (float)dt * 5f);

        // ── Load background for selected beatmap ──
        if (_bgLoadedIndex != currentBmIdx)
        {
            _bgLoadedIndex = currentBmIdx;
            LoadSelectedBackground();
        }

        // ── Audio preview ──
        UpdateAudioPreview(dt);
    }

    private void LoadSelectedBackground()
    {
        _background.Unload();

        int bmIdx = SelectedBeatmapIndex;
        if (bmIdx >= _beatmaps.Count) return;

        var bm = _beatmaps[bmIdx];
        if (string.IsNullOrEmpty(bm.BackgroundFilename)) return;

        var stub = new BeatmapData
        {
            FilePath = bm.FilePath,
            FolderPath = string.IsNullOrEmpty(bm.FolderPath)
                ? (Path.GetDirectoryName(bm.FilePath) ?? "")
                : bm.FolderPath,
            BackgroundFilename = bm.BackgroundFilename
        };
        _background.Load(stub);
    }

    private void MoveSelection(int delta)
    {
        int totalItems = _filteredIndices.Count + 1;
        _selectedFilterIdx = Math.Clamp(_selectedFilterIdx + delta, 0, totalItems - 1);
    }

    private void MoveDifficulty(int delta)
    {
        var group = GetSelectedGroup();
        if (group == null || group.BeatmapIndices.Count <= 1) return;

        int count = group.BeatmapIndices.Count;
        group.SelectedDifficultyIndex = (group.SelectedDifficultyIndex + delta + count) % count;
        _selectionDirection = delta < 0 ? -1 : 1;
        _selectionFlash = 1f;
        _previewDelay = 0.18;
    }

    private bool TrySelectDifficultyAt(float mx, float my)
    {
        var group = GetSelectedGroup();
        if (group == null || group.BeatmapIndices.Count == 0) return false;

        var layout = GetDifficultyChipLayout(Engine.ScreenWidth, group.BeatmapIndices.Count,
            _difficultyChipY);
        if (my < layout.Y || my > layout.Y + layout.Height) return false;

        for (int i = 0; i < group.BeatmapIndices.Count; i++)
        {
            float x = layout.X + i * (layout.Width + layout.Gap);
            if (mx < x || mx > x + layout.Width) continue;

            group.SelectedDifficultyIndex = i;
            _selectionFlash = 1f;
            _previewDelay = 0.18;
            return true;
        }

        return false;
    }

    private bool TrySelectQueueItemAt(float mx, float my)
    {
        int totalItems = _filteredIndices.Count + 1;
        float y = Engine.ScreenHeight - 140f;
        float itemW = 210f;
        float itemH = 52f;
        float gap = 30f;
        float centerX = Engine.ScreenWidth * 0.5f;

        if (my < y || my > y + itemH) return false;

        for (int offset = -2; offset <= 2; offset++)
        {
            int idx = _selectedFilterIdx + offset;
            if (idx < 0 || idx >= totalItems) continue;

            float x = centerX + offset * (itemW + gap) - itemW * 0.5f;
            if (mx < x || mx > x + itemW) continue;

            _selectedFilterIdx = idx;
            _selectionDirection = offset < 0 ? -1 : 1;
            _selectionFlash = 1f;
            ResetCardSlides();
            return true;
        }

        return false;
    }

    private bool TrySelectGridItemAt(float mx, float my)
    {
        int totalItems = _filteredIndices.Count + 1;
        var grid = GetGridLayout(Engine.ScreenWidth, Engine.ScreenHeight, totalItems);
        if (mx < grid.X || mx > grid.X + grid.Columns * grid.Pitch - grid.Gap)
            return false;
        if (my < grid.Y || my > grid.Bottom)
            return false;

        for (int idx = 0; idx < totalItems; idx++)
        {
            int row = idx / grid.Columns;
            int col = idx % grid.Columns;
            float x = grid.X + col * grid.Pitch;
            float y = grid.Y + row * grid.Pitch - _scrollOffset;

            if (mx < x || mx > x + grid.Tile || my < y || my > y + grid.Tile)
                continue;

            _selectionDirection = Math.Sign(idx - _selectedFilterIdx);
            if (_selectionDirection == 0)
                _selectionDirection = 1;
            _selectedFilterIdx = idx;
            _selectionFlash = 1f;
            _previewDelay = 0.22;
            ResetCardSlides();
            return true;
        }

        return false;
    }

    private void ResetCardSlides()
    {
        for (int i = 0; i < _cardSlide.Length; i++)
            _cardSlide[i] = 80f;
    }

    // ── Audio preview ──

    private void UpdateAudioPreview(double dt)
    {
        if (Game.TopBarOwnsMusic)
        {
            _previewPlaying = false;
            _previewLoadedIndex = -1;
            return;
        }

        int bmIdx = SelectedBeatmapIndex;

        // Fade preview volume
        if (_previewPlaying)
            _previewVolume = MathF.Min(1f, _previewVolume + (float)dt * 2f);
        else
            _previewVolume = MathF.Max(0f, _previewVolume - (float)dt * 4f);

        if (Engine.Audio.IsMusicLoaded)
            Engine.Audio.SetMusicVolume(_previewVolume * 0.35f * Game.Settings.MasterVolume * Game.Settings.MusicVolume);

        // Stop audio once faded out
        if (_previewVolume <= 0f && !_previewPlaying && Engine.Audio.IsPlaying)
            Engine.Audio.StopMusic();

        // Delay before starting preview
        if (_previewDelay > 0)
        {
            _previewDelay -= dt;
            return;
        }

        // Load new preview when selection changes
        if (_previewLoadedIndex != bmIdx)
        {
            _previewLoadedIndex = bmIdx;
            _previewPlaying = false;
            _previewVolume = 0f;

            if (bmIdx < _beatmaps.Count)
            {
                var bm = _beatmaps[bmIdx];
                string audioPath = ResolveAudioPath(bm);
                if (!string.IsNullOrEmpty(audioPath))
                {
                    Engine.Audio.StopMusic();
                    if (Engine.Audio.LoadMusic(audioPath))
                    {
                        // Seek to preview point (or 40% of duration)
                        double seekMs = bm.PreviewTime > 0 ? bm.PreviewTime : 0;
                        Engine.Audio.PlayMusic();
                        if (seekMs > 0)
                            Engine.Audio.SeekMusic(seekMs);
                        else
                        {
                            // Seek to 40% if no preview time set
                            double dur = Engine.Audio.MusicDuration;
                            if (dur > 0)
                                Engine.Audio.SeekMusic(dur * 0.4);
                        }
                        _previewPlaying = true;
                    }
                }
                else
                {
                    Engine.Audio.StopMusic();
                }
            }
            else
            {
                Engine.Audio.StopMusic();
            }
        }
    }

    private void StopPreview()
    {
        _previewPlaying = false;
        if (!Game.TopBarOwnsMusic)
            Engine.Audio.StopMusic();
    }

    private string ResolveAudioPath(BeatmapInfo bm)
    {
        if (string.IsNullOrEmpty(bm.AudioFilename)) return "";
        string folder = string.IsNullOrEmpty(bm.FolderPath)
            ? (Path.GetDirectoryName(bm.FilePath) ?? "")
            : bm.FolderPath;
        string direct = Path.Combine(folder, bm.AudioFilename);
        if (File.Exists(direct)) return direct;

        // Try lazer audio resolver
        var resolved = LazerAudioResolver.ResolveAudio(bm.FilePath, bm.AudioFilename);
        return resolved ?? "";
    }

    // ── Filter / search ──

    private void RebuildFilter()
    {
        _filteredIndices.Clear();
        if (string.IsNullOrEmpty(_searchQuery))
        {
            for (int i = 0; i < _beatmapGroups.Count; i++)
                _filteredIndices.Add(i);
            return;
        }

        string query = _searchQuery.ToLowerInvariant();
        string[] tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < _beatmapGroups.Count; i++)
        {
            string hay = BuildGroupSearchText(_beatmapGroups[i]).ToLowerInvariant();
            bool match = true;
            foreach (string tok in tokens)
            {
                if (!hay.Contains(tok))
                {
                    match = false;
                    break;
                }
            }
            if (match) _filteredIndices.Add(i);
        }

        if (_selectedFilterIdx >= _filteredIndices.Count + 1)
            _selectedFilterIdx = Math.Max(0, _filteredIndices.Count);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Render
    // ═══════════════════════════════════════════════════════════════════

    public override void Render(double dt)
    {
        RenderRedesigned(dt);
    }

    private void RenderRedesigned(double dt)
    {
        var batch = Engine.SpriteBatch;
        var font  = Engine.Font;
        var px    = Engine.PixelTex;
        var proj  = Engine.Projection;
        int sw    = Engine.ScreenWidth;
        int sh    = Engine.ScreenHeight;

        float enterA = EaseOutCubic(MathF.Min(1f, _enterAnim));
        float exitT = _exiting ? EaseOutCubic(MathF.Min(1f, _exitAnim)) : 0f;
        float exitFade = 1f - exitT;
        float fadeA = enterA * exitFade;
        float exitSlide = exitT * 58f;

        int totalItems = _filteredIndices.Count + 1;

        batch.Begin(proj);

        DrawMinimalBackdrop(batch, px, sw, sh, fadeA);
        DrawMinimalHeader(batch, font, px, sw, fadeA);
        if (Game.LevelSelectGridLayout)
            DrawGridLevelList(batch, font, px, sw, sh, totalItems, fadeA, exitSlide);
        else
        {
            DrawMinimalSelection(batch, font, px, sw, sh, fadeA, exitSlide);
            DrawMinimalQueue(batch, font, px, sw, sh, totalItems, fadeA, exitSlide);
        }
        DrawMinimalControls(batch, font, sw, sh, fadeA);

        if (_exiting)
        {
            batch.Draw(px, 0, 0, sw, sh, 0f, 0f, 0f, exitT * 0.60f);
        }

        batch.End();
    }

    private void DrawMinimalBackdrop(SpriteBatch batch, Texture2D px, int sw, int sh, float fadeA)
    {
        if (_background.HasBackground)
            _background.Render();
        else
            batch.Draw(px, 0, 0, sw, sh, 0.018f, 0.019f, 0.026f, 1f);

        batch.Draw(px, 0, 0, sw, sh, 0f, 0f, 0f, 0.30f * fadeA);
        batch.Draw(px, 0, 0, sw, 132f, 0f, 0f, 0f, 0.26f * fadeA);
        batch.Draw(px, 0, sh - 180f, sw, 180f, 0f, 0f, 0f, 0.34f * fadeA);
    }

    private void DrawMinimalHeader(SpriteBatch batch, Engine.Text.BitmapFont font,
        Texture2D px, int sw, float fadeA)
    {
        float headerY = TaikoGame.GlobalTopBarHeight + 18f;
        font.DrawText(batch, "TaikoNova", 42f, headerY, 0.76f,
            0.90f, 0.91f, 0.96f, 0.88f * fadeA);

        string count = _searchQuery.Length > 0
            ? $"{_filteredIndices.Count}/{_beatmapGroups.Count}"
            : $"{_beatmapGroups.Count}";
        font.DrawTextRight(batch, $"{count} songs", sw - 42f, headerY, 0.58f,
            0.56f, 0.58f, 0.64f, 0.76f * fadeA);

        if (_searchActive || _searchQuery.Length > 0)
        {
            float w = 460f;
            float x = (sw - w) * 0.5f;
            float searchY = TaikoGame.GlobalTopBarHeight + 14f;
            DrawRoundedRect(batch, px, x, searchY, w, 42f, 21f,
                0.04f, 0.045f, 0.060f, 0.82f * fadeA);
            string text = _searchQuery.Length > 0 ? _searchQuery : "search";
            text = TruncateToFit(font, text, 0.58f, w - 62f);
            font.DrawText(batch, text, x + 28f, searchY + 13f, 0.58f,
                _searchQuery.Length > 0 ? 0.84f : 0.45f,
                _searchQuery.Length > 0 ? 0.86f : 0.46f,
                _searchQuery.Length > 0 ? 0.92f : 0.52f,
                fadeA);
            if (_searchActive && _searchCursorBlink < 0.5f)
            {
                float curX = x + 30f + font.MeasureWidth(text, 0.58f);
                batch.Draw(px, curX, searchY + 12f, 2f, 20f, 1f, 1f, 1f, 0.55f * fadeA);
            }
        }
    }

    private void DrawMinimalSelection(SpriteBatch batch, Engine.Text.BitmapFont font,
        Texture2D px, int sw, int sh, float fadeA, float exitSlide)
    {
        float centerX = sw * 0.5f - exitSlide * 0.25f;
        float reveal = Clamp01(_infoReveal);
        float titleT = EaseOutCubic(reveal);
        float subtitleT = EaseOutCubic(Clamp01((reveal - 0.08f) / 0.72f));
        float detailsT = EaseOutCubic(Clamp01((reveal - 0.18f) / 0.64f));
        float diffT = EaseOutCubic(Clamp01((reveal - 0.26f) / 0.60f));
        float playT = EaseOutCubic(Clamp01((reveal - 0.38f) / 0.54f));
        float y = 242f + (1f - titleT) * 16f;

        string title = GetItemTitle(_selectedFilterIdx);
        float titleScale = 1.72f + _selectionFlash * 0.035f;
        float maxTitleW = sw - 260f;
        while (titleScale > 0.96f && font.MeasureWidth(title, titleScale) > maxTitleW)
            titleScale -= 0.06f;
        if (font.MeasureWidth(title, titleScale) > maxTitleW)
            title = TruncateToFit(font, title, titleScale, maxTitleW);

        float titleW = font.MeasureWidth(title, titleScale);
        float titleX = centerX - titleW * 0.5f + _selectionDirection * (1f - titleT) * 48f;
        font.DrawTextShadow(batch, title, titleX, y, titleScale,
            0.96f, 0.97f, 1f, fadeA * titleT, 3f);

        float underlineW = MathF.Min(titleW, 360f) * titleT;
        if (underlineW > 2f)
        {
            DrawRoundedRect(batch, px, centerX - underlineW * 0.5f, y + font.MeasureHeight(titleScale) + 8f,
                underlineW, 2f, 1f,
                SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2],
                (0.42f + _selectionFlash * 0.24f) * fadeA);
        }
        y += font.MeasureHeight(titleScale) + 20f;

        string subtitle = GetItemSubtitle(_selectedFilterIdx);
        subtitle = TruncateToFit(font, subtitle, 0.72f, sw - 360f);
        float subtitleW = font.MeasureWidth(subtitle, 0.72f);
        font.DrawText(batch, subtitle,
            centerX - subtitleW * 0.5f + _selectionDirection * (1f - subtitleT) * 24f,
            y + (1f - subtitleT) * 8f, 0.72f,
            0.62f, 0.64f, 0.70f, 0.88f * fadeA * subtitleT);
        y += 56f;

        string details = $"{GetItemVersion(_selectedFilterIdx)}    {GetItemDifficultyLabel(_selectedFilterIdx)}    {GetItemOdLabel(_selectedFilterIdx)}";
        details = TruncateToFit(font, details, 0.56f, sw - 460f);
        float detailsW = font.MeasureWidth(details, 0.56f);
        font.DrawText(batch, details,
            centerX - detailsW * 0.5f + _selectionDirection * (1f - detailsT) * 16f,
            y + (1f - detailsT) * 6f, 0.56f,
            0.48f, 0.50f, 0.56f, 0.82f * fadeA * detailsT);

        y += 48f;
        if (!IsPracticeItem(_selectedFilterIdx))
        {
            DrawDifficultySelector(batch, font, px, sw, y + (1f - diffT) * 8f,
                fadeA * diffT);
            y += 82f;
        }
        else
        {
            y += 48f;
        }

        string play = IsPracticeItem(_selectedFilterIdx) ? "Enter practice" : "Enter to play";
        float playW = font.MeasureWidth(play, 0.62f);
        float playY = y + (1f - playT) * 10f;
        DrawRoundedRect(batch, px, centerX - playW * 0.5f - 24f, playY - 12f,
            playW + 48f, 42f, 21f,
            0.06f, 0.064f, 0.080f, 0.70f * fadeA * playT);
        font.DrawText(batch, play, centerX - playW * 0.5f, playY, 0.62f,
            0.86f, 0.88f, 0.94f, fadeA * playT);
    }

    private void DrawMinimalQueue(SpriteBatch batch, Engine.Text.BitmapFont font,
        Texture2D px, int sw, int sh, int totalItems, float fadeA, float exitSlide)
    {
        float y = sh - 132f;
        float itemW = 210f;
        float gap = 30f;
        float centerX = sw * 0.5f;
        float reveal = EaseOutCubic(Clamp01(_infoReveal));
        float queueSlide = -_selectionDirection * (1f - reveal) * (itemW + gap) * 0.42f;

        for (int offset = -2; offset <= 2; offset++)
        {
            int idx = _selectedFilterIdx + offset;
            if (idx < 0 || idx >= totalItems) continue;

            bool selected = offset == 0;
            float x = centerX + offset * (itemW + gap) - itemW * 0.5f
                + exitSlide * 0.18f + queueSlide;
            float distanceFade = selected ? 1f : 0.42f;
            float a = fadeA * distanceFade * (0.70f + reveal * 0.30f);

            string title = TruncateToFit(font, GetItemTitle(idx), selected ? 0.58f : 0.48f, itemW);
            float tw = font.MeasureWidth(title, selected ? 0.58f : 0.48f);
            float itemY = y + (selected ? (1f - reveal) * 6f : 0f);
            font.DrawText(batch, title, x + (itemW - tw) * 0.5f, itemY, selected ? 0.58f : 0.48f,
                selected ? 0.92f : 0.54f,
                selected ? 0.93f : 0.56f,
                selected ? 1.00f : 0.62f,
                a);

            if (selected)
            {
                float underlineW = (itemW - 68f) * reveal;
                DrawRoundedRect(batch, px, x + itemW * 0.5f - underlineW * 0.5f,
                    itemY + 34f, underlineW, 3f, 1.5f,
                    SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2], 0.76f * fadeA);
            }
        }

        string pos = $"{Math.Min(_selectedFilterIdx + 1, totalItems)}/{Math.Max(1, totalItems)}";
        float posW = font.MeasureWidth(pos, 0.46f);
        font.DrawText(batch, pos, centerX - posW * 0.5f, y + 58f, 0.46f,
            0.44f, 0.46f, 0.52f, 0.70f * fadeA);
    }

    private void DrawGridLevelList(SpriteBatch batch, Engine.Text.BitmapFont font,
        Texture2D px, int sw, int sh, int totalItems, float fadeA, float exitSlide)
    {
        var grid = GetGridLayout(sw, sh, totalItems);
        float reveal = EaseOutCubic(Clamp01(_infoReveal));

        if (_filteredIndices.Count == 0 && _searchQuery.Length > 0)
        {
            string empty = "No maps match";
            float ew = font.MeasureWidth(empty, 0.70f);
            font.DrawText(batch, empty, (sw - ew) * 0.5f, grid.Y + 38f, 0.70f,
                0.70f, 0.72f, 0.80f, 0.70f * fadeA);
            return;
        }

        for (int idx = 0; idx < totalItems; idx++)
        {
            int row = idx / grid.Columns;
            int col = idx % grid.Columns;
            float tileX = grid.X + col * grid.Pitch;
            float tileY = grid.Y + row * grid.Pitch - _scrollOffset;
            if (tileY + grid.Tile < grid.Y - 12f || tileY > grid.Bottom + 12f)
                continue;

            bool selected = idx == _selectedFilterIdx;
            float edgeFade = MathF.Min(
                Clamp01((tileY + grid.Tile - grid.Y) / 36f),
                Clamp01((grid.Bottom - tileY) / 36f));
            if (edgeFade <= 0.01f) continue;

            float slide = idx < _cardSlide.Length ? _cardSlide[idx] * 0.16f : 0f;
            float pop = selected ? EaseOutCubic(_selectionFlash) * 4f : 0f;
            DrawGridTile(batch, font, px,
                tileX + slide + exitSlide * 0.12f,
                tileY - pop + (selected ? (1f - reveal) * 5f : 0f),
                grid.Tile,
                idx,
                selected,
                fadeA * edgeFade);
        }

        DrawGridScroll(batch, px, grid, totalItems, fadeA);
    }

    private void DrawGridTile(SpriteBatch batch, Engine.Text.BitmapFont font,
        Texture2D px, float x, float y, float size, int filterIdx, bool selected, float fadeA)
    {
        bool practice = IsPracticeItem(filterIdx);
        float[] accent = practice ? SkinConfig.DrumrollColor : GetOdColor(GetItemOd(filterIdx));
        float glowA = selected ? (0.15f + _selectGlow * 0.06f) * fadeA : 0f;

        if (selected)
        {
            DrawRoundedRect(batch, px, x - 5f, y - 5f, size + 10f, size + 10f, 8f,
                accent[0], accent[1], accent[2], glowA);
        }

        DrawRoundedRect(batch, px, x, y, size, size, 8f,
            selected ? 0.074f : 0.044f,
            selected ? 0.078f : 0.046f,
            selected ? 0.096f : 0.060f,
            selected ? 0.92f * fadeA : 0.74f * fadeA);
        DrawRoundedRect(batch, px, x + 12f, y + 12f, size - 24f, 1f, 0.5f,
            1f, 1f, 1f, (selected ? 0.13f : 0.06f) * fadeA);

        if (selected && _selectionFlash > 0.01f)
        {
            DrawRoundedRect(batch, px, x, y, size, size, 8f,
                1f, 1f, 1f, _selectionFlash * 0.08f * fadeA);
        }

        string index = (Math.Min(filterIdx + 1, _filteredIndices.Count + 1)).ToString("00");
        font.DrawText(batch, index, x + 14f, y + 18f, 0.36f,
            accent[0], accent[1], accent[2], 0.72f * fadeA);

        string title = TruncateToFit(font, GetItemTitle(filterIdx), selected ? 0.58f : 0.50f, size - 28f);
        font.DrawText(batch, title, x + 14f, y + 46f, selected ? 0.58f : 0.50f,
            selected ? 0.95f : 0.76f,
            selected ? 0.96f : 0.78f,
            selected ? 1.00f : 0.86f,
            fadeA);

        string sub = TruncateToFit(font, GetItemSubtitle(filterIdx), 0.38f, size - 28f);
        font.DrawText(batch, sub, x + 14f, y + 72f, 0.38f,
            0.46f, 0.48f, 0.56f, 0.78f * fadeA);

        string diff = $"{GetItemDifficultyLabel(filterIdx)} / {GetItemOdLabel(filterIdx)}";
        diff = TruncateToFit(font, diff, 0.38f, size - 28f);
        font.DrawText(batch, diff, x + 14f, y + size - 50f, 0.38f,
            accent[0], accent[1], accent[2], 0.86f * fadeA);

        DrawGridDifficultyStrip(batch, px, x + 14f, y + size - 22f, size - 28f,
            filterIdx, selected, fadeA);
    }

    private void DrawGridDifficultyStrip(SpriteBatch batch, Texture2D px,
        float x, float y, float w, int filterIdx, bool selected, float fadeA)
    {
        var group = GetGroupAtFilterIndex(filterIdx);
        if (group == null || group.BeatmapIndices.Count == 0)
        {
            DrawRoundedRect(batch, px, x, y, w, 5f, 2.5f,
                SkinConfig.DrumrollColor[0], SkinConfig.DrumrollColor[1], SkinConfig.DrumrollColor[2], 0.72f * fadeA);
            return;
        }

        int count = Math.Min(group.BeatmapIndices.Count, 8);
        float gap = 5f;
        float segW = (w - gap * (count - 1)) / count;
        for (int i = 0; i < count; i++)
        {
            int bmIdx = group.BeatmapIndices[i];
            if (bmIdx < 0 || bmIdx >= _beatmaps.Count) continue;
            var style = GetDifficultyStyle(_beatmaps[bmIdx].OD);
            bool current = i == group.SelectedDifficultyIndex;
            float sx = x + i * (segW + gap);
            DrawRoundedRect(batch, px, sx, y, segW, current && selected ? 7f : 5f, 2.5f,
                style.Color[0], style.Color[1], style.Color[2],
                (current ? 0.92f : 0.46f) * fadeA);
            if (style.Label == "NO LOGIC")
            {
                DrawRoundedRect(batch, px, sx + segW * 0.50f, y, segW * 0.50f,
                    current && selected ? 7f : 5f, 2.5f,
                    1f, 1f, 1f, (current ? 0.70f : 0.34f) * fadeA);
            }
        }
    }

    private void DrawGridScroll(SpriteBatch batch, Texture2D px,
        (float X, float Y, float Tile, float Gap, float Pitch, float Bottom, float Height, int Columns) grid,
        int totalItems, float fadeA)
    {
        int rows = (int)MathF.Ceiling(totalItems / (float)Math.Max(1, grid.Columns));
        if (rows <= 1) return;

        float contentH = rows * grid.Pitch;
        if (contentH <= grid.Height) return;

        float trackX = grid.X + grid.Columns * grid.Pitch - grid.Gap + 12f;
        float thumbH = MathF.Max(30f, grid.Height * (grid.Height / contentH));
        float maxScroll = MathF.Max(1f, contentH - grid.Height);
        float thumbY = grid.Y + (grid.Height - thumbH) * Clamp01(_scrollOffset / maxScroll);

        DrawRoundedRect(batch, px, trackX, grid.Y, 4f, grid.Height, 2f,
            1f, 1f, 1f, 0.06f * fadeA);
        DrawRoundedRect(batch, px, trackX - 1f, thumbY, 6f, thumbH, 3f,
            SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2], 0.58f * fadeA);
    }

    private void DrawDifficultySelector(SpriteBatch batch, Engine.Text.BitmapFont font,
        Texture2D px, int sw, float y, float fadeA)
    {
        var group = GetSelectedGroup();
        if (group == null || group.BeatmapIndices.Count == 0) return;

        _difficultyChipY = y;
        var layout = GetDifficultyChipLayout(sw, group.BeatmapIndices.Count, y);
        float railW = layout.Width * group.BeatmapIndices.Count
            + layout.Gap * (group.BeatmapIndices.Count - 1);

        DrawRoundedRect(batch, px, layout.X, y + 28f, railW, 1f, 0.5f,
            1f, 1f, 1f, 0.08f * fadeA);

        for (int i = 0; i < group.BeatmapIndices.Count; i++)
        {
            int bmIdx = group.BeatmapIndices[i];
            if (bmIdx < 0 || bmIdx >= _beatmaps.Count) continue;

            var bm = _beatmaps[bmIdx];
            var style = GetDifficultyStyle(bm.OD);
            bool selected = i == group.SelectedDifficultyIndex;
            float x = layout.X + i * (layout.Width + layout.Gap);
            float markY = y + 25f;
            float textY = y + (selected ? -1f - _selectionFlash * 1.5f : 2f);
            float lineA = selected ? 0.86f : 0.34f;
            float textA = selected ? 0.98f : 0.58f;

            float dotSize = selected ? 7f : 5f;
            batch.Draw(_uiCircle, x + layout.Width * 0.5f - dotSize * 0.5f,
                markY - dotSize * 0.5f, dotSize, dotSize,
                style.Color[0], style.Color[1], style.Color[2], lineA * fadeA);

            if (style.Label == "NO LOGIC")
            {
                batch.Draw(px, x + layout.Width * 0.5f, markY - 0.5f,
                    dotSize * 0.7f, 1f, 1f, 1f, 1f, 0.70f * fadeA);
            }

            string label = style.Label;
            float labelScale = selected ? 0.50f : 0.42f;
            if (label.Length > 8) labelScale -= selected ? 0.06f : 0.04f;
            float labelW = font.MeasureWidth(label, labelScale);
            font.DrawText(batch, label, x + (layout.Width - labelW) * 0.5f, textY,
                labelScale,
                selected ? 0.96f : style.Color[0],
                selected ? 0.97f : style.Color[1],
                selected ? 1.00f : style.Color[2],
                textA * fadeA);

            string od = $"OD {bm.OD:F1}";
            float odW = font.MeasureWidth(od, 0.34f);
            font.DrawText(batch, od, x + (layout.Width - odW) * 0.5f,
                y + 34f, 0.34f,
                0.48f, 0.50f, 0.56f, (selected ? 0.78f : 0.42f) * fadeA);

            if (selected)
            {
                float underlineW = layout.Width * (0.52f + _selectionFlash * 0.08f);
                DrawRoundedRect(batch, px, x + (layout.Width - underlineW) * 0.5f,
                    y + 27f, underlineW, 2f, 1f,
                    style.Color[0], style.Color[1], style.Color[2], 0.92f * fadeA);
                if (style.Label == "NO LOGIC")
                {
                    DrawRoundedRect(batch, px, x + layout.Width * 0.5f,
                        y + 27f, underlineW * 0.5f, 2f, 1f,
                        1f, 1f, 1f, 0.62f * fadeA);
                }
            }
        }
    }

    private void DrawMinimalControls(SpriteBatch batch, Engine.Text.BitmapFont font,
        int sw, int sh, float fadeA)
    {
        string controls = "Up/Down song     Left/Right difficulty     Tab search     F2 random     Esc back";
        float w = font.MeasureWidth(controls, 0.44f);
        font.DrawText(batch, controls, (sw - w) * 0.5f, sh - 42f, 0.44f,
            0.40f, 0.42f, 0.48f, 0.66f * fadeA);
    }

    private void DrawStageBackdrop(SpriteBatch batch, Texture2D px, int sw, int sh, float fadeA)
    {
        if (_background.HasBackground)
            _background.Render();
        else
            batch.Draw(px, 0, 0, sw, sh, 0.018f, 0.020f, 0.032f, 1f);

        batch.Draw(px, 0, 0, sw, sh, 0f, 0f, 0f, 0.20f * fadeA);
        batch.Draw(px, 0, 0, sw, 190f, 0f, 0f, 0f, 0.50f * fadeA);
        batch.Draw(px, 0, sh - 260f, sw, 260f, 0f, 0f, 0f, 0.58f * fadeA);
        batch.Draw(px, 0, 0, 360f, sh, 0f, 0f, 0f, 0.28f * fadeA);
        batch.Draw(px, sw - 420f, 0, 420f, sh, 0f, 0f, 0f, 0.30f * fadeA);

        for (int i = 0; i < 14; i++)
        {
            float x = -160f + i * 150f + MathF.Sin((float)_time * 0.55f + i) * 10f;
            float y = 112f + i * 34f;
            batch.Draw(px, x, y, 420f, 1.5f,
                SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2],
                0.045f * fadeA, -0.10f);
        }
    }

    private void DrawStageHeader(SpriteBatch batch, Engine.Text.BitmapFont font,
        Texture2D px, int sw, int totalItems, float fadeA)
    {
        DrawRoundedRect(batch, px, 34f, 24f, 214f, 48f, 22f,
            0.045f, 0.050f, 0.072f, 0.82f * fadeA);
        batch.Draw(_uiCircle, 48f, 38f, 20f, 20f,
            SkinConfig.DonColor[0], SkinConfig.DonColor[1], SkinConfig.DonColor[2], 0.95f * fadeA);
        batch.Draw(_uiCircle, 72f, 38f, 20f, 20f,
            SkinConfig.KatColor[0], SkinConfig.KatColor[1], SkinConfig.KatColor[2], 0.95f * fadeA);
        font.DrawText(batch, "TAIKO NOVA", 104f, 37f, 0.72f,
            0.94f, 0.95f, 1f, fadeA);

        float searchW = _searchActive || _searchQuery.Length > 0 ? 474f : 270f;
        float searchX = (sw - searchW) * 0.5f;
        DrawRoundedRect(batch, px, searchX, 25f, searchW, 46f, 23f,
            0.045f, 0.050f, 0.072f, 0.78f * fadeA);
        DrawRoundedRect(batch, px, searchX + 12f, 67f, searchW - 24f, 2f, 1f,
            SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2],
            (_searchActive ? 0.72f : 0.28f) * fadeA);

        string searchText = _searchQuery.Length > 0 ? _searchQuery : "TAB  SEARCH";
        float searchScale = 0.62f;
        searchText = TruncateToFit(font, searchText, searchScale, searchW - 78f);
        font.DrawText(batch, ">", searchX + 22f, 38f, searchScale,
            SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2], 0.90f * fadeA);
        font.DrawText(batch, searchText, searchX + 50f, 38f, searchScale,
            _searchQuery.Length > 0 ? 0.92f : 0.54f,
            _searchQuery.Length > 0 ? 0.93f : 0.56f,
            _searchQuery.Length > 0 ? 1.00f : 0.64f,
            fadeA);
        if (_searchActive && _searchCursorBlink < 0.5f)
        {
            float curX = searchX + 52f + font.MeasureWidth(searchText, searchScale);
            batch.Draw(px, curX, 37f, 2f, 20f, 1f, 1f, 1f, 0.70f * fadeA);
        }

        string countText = _searchQuery.Length > 0
            ? $"{_filteredIndices.Count}/{_beatmaps.Count}"
            : $"{_beatmaps.Count}";
        DrawRoundedRect(batch, px, sw - 224f, 25f, 190f, 46f, 23f,
            0.045f, 0.050f, 0.072f, 0.78f * fadeA);
        font.DrawTextRight(batch, $"{countText} MAPS", sw - 54f, 38f, 0.62f,
            0.72f, 0.74f, 0.82f, fadeA);
    }

    private void DrawSelectedStage(SpriteBatch batch, Engine.Text.BitmapFont font,
        Texture2D px, float x, float y, float w, float h, float fadeA)
    {
        bool practice = IsPracticeItem(_selectedFilterIdx);
        float[] accent = practice ? SkinConfig.DrumrollColor : SkinConfig.Accent;

        float reveal = EaseOutCubic(MathF.Min(1f, _infoReveal));
        float lift = (1f - reveal) * 28f;
        float flash = _selectionFlash * 0.12f * fadeA;

        DrawRoundedRect(batch, px, x + 16f, y + 22f + lift, w, h, 34f,
            0f, 0f, 0f, 0.32f * fadeA);
        DrawRoundedRect(batch, px, x, y + lift, w, h, 34f,
            0.030f, 0.034f, 0.052f, 0.74f * fadeA);
        DrawRoundedRect(batch, px, x, y + lift, w, h, 34f,
            accent[0] * 0.10f, accent[1] * 0.10f, accent[2] * 0.10f, 0.50f * fadeA);
        if (flash > 0.01f)
            DrawRoundedRect(batch, px, x, y + lift, w, h, 34f, 1f, 1f, 1f, flash);

        DrawRoundedRect(batch, px, x + 32f, y + 30f + lift, 6f, h - 60f, 3f,
            accent[0], accent[1], accent[2], 0.78f * fadeA);

        float contentX = x + 66f;
        float contentY = y + 58f + lift;
        float contentW = w - 126f;

        string eyebrow = practice ? "PRACTICE STAGE" : "NOW SELECTING";
        DrawRoundedRect(batch, px, contentX, contentY, font.MeasureWidth(eyebrow, 0.50f) + 28f, 28f, 14f,
            accent[0] * 0.20f, accent[1] * 0.20f, accent[2] * 0.20f, 0.92f * fadeA);
        font.DrawText(batch, eyebrow, contentX + 14f, contentY + 7f, 0.50f,
            accent[0], accent[1], accent[2], fadeA);
        contentY += 52f;

        string title = GetItemTitle(_selectedFilterIdx);
        float titleScale = 1.82f;
        while (titleScale > 1.05f && font.MeasureWidth(title, titleScale) > contentW)
            titleScale -= 0.07f;
        if (font.MeasureWidth(title, titleScale) > contentW)
            title = TruncateToFit(font, title, titleScale, contentW);

        font.DrawTextShadow(batch, title, contentX, contentY, titleScale,
            1f, 1f, 1f, reveal * fadeA, 4f);
        contentY += font.MeasureHeight(titleScale) + 18f;

        string subtitle = GetItemSubtitle(_selectedFilterIdx);
        subtitle = TruncateToFit(font, subtitle, 0.82f, contentW);
        font.DrawText(batch, subtitle, contentX, contentY, 0.82f,
            0.70f, 0.73f, 0.84f, 0.88f * reveal * fadeA);
        contentY += 58f;

        DrawStageChip(batch, font, px, contentX, contentY, "DIFF", GetItemVersion(_selectedFilterIdx), accent, fadeA);
        DrawStageChip(batch, font, px, contentX + 256f, contentY, "OD", GetItemOdLabel(_selectedFilterIdx), GetOdColor(GetItemOd(_selectedFilterIdx)), fadeA);
        DrawStageChip(batch, font, px, contentX + 462f, contentY, "SRC", GetItemSource(_selectedFilterIdx), accent, fadeA);

        float bannerY = y + h - 92f + lift;
        DrawRoundedRect(batch, px, contentX, bannerY, contentW, 54f, 24f,
            0.08f, 0.09f, 0.12f, 0.58f * fadeA);
        float pulse = 0.50f + MathF.Sin((float)_time * 3.4f) * 0.16f;
        batch.Draw(_uiCircle, contentX + 24f, bannerY + 14f, 26f, 26f,
            accent[0], accent[1], accent[2], pulse * fadeA);
        font.DrawText(batch, _previewPlaying ? "PREVIEW PLAYING" : "READY", contentX + 66f, bannerY + 16f, 0.62f,
            0.78f, 0.80f, 0.90f, 0.86f * fadeA);
    }

    private void DrawActionModule(SpriteBatch batch, Engine.Text.BitmapFont font,
        Texture2D px, float x, float y, float w, float h, float fadeA)
    {
        bool practice = IsPracticeItem(_selectedFilterIdx);
        float[] accent = practice ? SkinConfig.DrumrollColor : SkinConfig.Accent;
        float od = GetItemOd(_selectedFilterIdx);
        float[] odColor = GetOdColor(od);

        DrawRoundedRect(batch, px, x + 12f, y + 18f, w, h, 32f,
            0f, 0f, 0f, 0.30f * fadeA);
        DrawRoundedRect(batch, px, x, y, w, h, 32f,
            0.034f, 0.038f, 0.058f, 0.78f * fadeA);

        font.DrawText(batch, practice ? "PRACTICE" : "PLAY", x + 34f, y + 34f, 1.10f,
            1f, 1f, 1f, fadeA);
        font.DrawText(batch, "ENTER", x + 36f, y + 76f, 0.54f,
            accent[0], accent[1], accent[2], 0.88f * fadeA);

        float drumY = y + 122f;
        batch.Draw(_uiCircle, x + 54f, drumY, 104f, 104f,
            SkinConfig.DonColor[0], SkinConfig.DonColor[1], SkinConfig.DonColor[2], 0.94f * fadeA);
        batch.Draw(_uiCircle, x + 176f, drumY, 104f, 104f,
            SkinConfig.KatColor[0], SkinConfig.KatColor[1], SkinConfig.KatColor[2], 0.94f * fadeA);
        batch.Draw(_uiCircle, x + 80f, drumY + 26f, 52f, 52f, 1f, 1f, 1f, 0.16f * fadeA);
        batch.Draw(_uiCircle, x + 202f, drumY + 26f, 52f, 52f, 1f, 1f, 1f, 0.16f * fadeA);

        DrawRoundedRect(batch, px, x + 34f, y + 266f, w - 68f, 74f, 22f,
            odColor[0] * 0.16f, odColor[1] * 0.16f, odColor[2] * 0.16f, 0.90f * fadeA);
        font.DrawText(batch, GetItemOdLabel(_selectedFilterIdx), x + 56f, y + 286f, 0.90f,
            odColor[0], odColor[1], odColor[2], fadeA);

        float meterX = x + 54f;
        float meterY = y + 360f;
        float segW = (w - 108f - 7f * 9f) / 10f;
        int filled = (int)MathF.Ceiling(Math.Clamp(od, 0f, 10f));
        for (int i = 0; i < 10; i++)
        {
            bool on = i < filled;
            DrawRoundedRect(batch, px, meterX + i * (segW + 7f), meterY, segW, 12f, 6f,
                on ? odColor[0] : 0.16f,
                on ? odColor[1] : 0.16f,
                on ? odColor[2] : 0.19f,
                on ? 0.86f * fadeA : 0.40f * fadeA);
        }
    }

    private void DrawBottomCarousel(SpriteBatch batch, Engine.Text.BitmapFont font,
        Texture2D px, int sw, int sh, int totalItems, float fadeA, float exitSlide)
    {
        float y = sh - 210f;
        float cardW = 196f;
        float cardH = 104f;
        float gap = 20f;
        float centerX = sw * 0.5f - cardW * 0.5f;

        DrawRoundedRect(batch, px, 54f, y - 48f, sw - 108f, 164f, 34f,
            0.030f, 0.034f, 0.052f, 0.60f * fadeA);
        font.DrawText(batch, "QUEUE", 84f, y - 24f, 0.56f,
            0.58f, 0.61f, 0.70f, 0.82f * fadeA);

        for (int offset = -3; offset <= 3; offset++)
        {
            int idx = _selectedFilterIdx + offset;
            if (idx < 0 || idx >= totalItems) continue;

            bool selected = offset == 0;
            float t = selected ? 1f : MathF.Max(0.36f, 1f - MathF.Abs(offset) * 0.18f);
            float slide = exitSlide * (0.25f + MathF.Abs(offset) * 0.08f);
            float cardX = centerX + offset * (cardW + gap) + slide;
            float cardY = y + (selected ? -18f - EaseOutCubic(_selectionFlash) * 6f : 12f);
            float w = selected ? cardW + 34f : cardW;
            float h = selected ? cardH + 22f : cardH;
            float[] accent = IsPracticeItem(idx) ? SkinConfig.DrumrollColor : SkinConfig.Accent;

            DrawRoundedRect(batch, px, cardX + 8f, cardY + 10f, w, h, 24f,
                0f, 0f, 0f, 0.24f * fadeA * t);
            DrawRoundedRect(batch, px, cardX, cardY, w, h, 24f,
                selected ? 0.12f : 0.055f,
                selected ? 0.13f : 0.060f,
                selected ? 0.17f : 0.082f,
                fadeA * t);
            if (selected)
            {
                DrawRoundedRect(batch, px, cardX - 6f, cardY - 6f, w + 12f, h + 12f, 28f,
                    accent[0], accent[1], accent[2], 0.12f * fadeA);
            }

            DrawRoundedRect(batch, px, cardX + 16f, cardY + 14f, 36f, 36f, 18f,
                accent[0] * 0.18f, accent[1] * 0.18f, accent[2] * 0.18f, 0.95f * fadeA * t);
            font.DrawCentered(batch, (idx + 1).ToString("00"), cardX + 34f, cardY + 32f, 0.42f,
                accent[0], accent[1], accent[2], fadeA * t);

            float textX = cardX + 64f;
            float textW = w - 82f;
            string title = TruncateToFit(font, GetItemTitle(idx), selected ? 0.62f : 0.54f, textW);
            font.DrawText(batch, title, textX, cardY + 18f, selected ? 0.62f : 0.54f,
                0.92f, 0.93f, 1f, fadeA * t);
            string sub = TruncateToFit(font, GetItemSubtitle(idx), 0.44f, textW);
            font.DrawText(batch, sub, textX, cardY + 46f, 0.44f,
                0.50f, 0.53f, 0.62f, fadeA * t * 0.88f);

            if (selected)
            {
                DrawRoundedRect(batch, px, cardX + 20f, cardY + h - 25f, w - 40f, 4f, 2f,
                    accent[0], accent[1], accent[2], 0.78f * fadeA);
            }
        }

        string pos = $"{Math.Min(_selectedFilterIdx + 1, totalItems)}/{Math.Max(1, totalItems)}";
        float posW = font.MeasureWidth(pos, 0.54f) + 28f;
        DrawRoundedRect(batch, px, sw - posW - 84f, y - 30f, posW, 28f, 14f,
            0.070f, 0.075f, 0.105f, 0.78f * fadeA);
        font.DrawText(batch, pos, sw - posW - 70f, y - 22f, 0.54f,
            0.74f, 0.76f, 0.86f, fadeA);
    }

    private void DrawCommandDock(SpriteBatch batch, Engine.Text.BitmapFont font,
        Texture2D px, int sw, int sh, float fadeA)
    {
        float dockW = 760f;
        float dockX = (sw - dockW) * 0.5f;
        float dockY = sh - 66f;
        DrawRoundedRect(batch, px, dockX, dockY, dockW, 42f, 21f,
            0.034f, 0.038f, 0.058f, 0.84f * fadeA);

        float cx = dockX + 22f;
        cx = DrawKeyHint(batch, font, px, cx, dockY + 11f, "UP/DOWN", "Move", fadeA);
        cx = DrawKeyHint(batch, font, px, cx + 16f, dockY + 11f, "ENTER", "Play", fadeA);
        cx = DrawKeyHint(batch, font, px, cx + 16f, dockY + 11f, "TAB", "Search", fadeA);
        cx = DrawKeyHint(batch, font, px, cx + 16f, dockY + 11f, "F2", "Random", fadeA);
        DrawKeyHint(batch, font, px, cx + 16f, dockY + 11f, "ESC", "Back", fadeA);
    }

    private void DrawStageChip(SpriteBatch batch, Engine.Text.BitmapFont font,
        Texture2D px, float x, float y, string label, string value, float[] accent, float fadeA)
    {
        value = string.IsNullOrEmpty(value) ? "Unknown" : value;
        value = TruncateToFit(font, value, 0.54f, 142f);
        DrawRoundedRect(batch, px, x, y, 220f, 52f, 20f,
            0.070f, 0.076f, 0.105f, 0.72f * fadeA);
        font.DrawText(batch, label, x + 18f, y + 9f, 0.42f,
            accent[0], accent[1], accent[2], 0.92f * fadeA);
        font.DrawText(batch, value, x + 18f, y + 28f, 0.54f,
            0.84f, 0.86f, 0.94f, fadeA);
    }

    private int SelectedGroupIndex
        => _selectedFilterIdx >= 0 && _selectedFilterIdx < _filteredIndices.Count
            ? _filteredIndices[_selectedFilterIdx]
            : -1;

    private BeatmapGroup? GetSelectedGroup()
        => GetGroupAtFilterIndex(_selectedFilterIdx);

    private BeatmapGroup? GetGroupAtFilterIndex(int filterIdx)
    {
        if (filterIdx < 0 || filterIdx >= _filteredIndices.Count) return null;
        int groupIdx = _filteredIndices[filterIdx];
        return groupIdx >= 0 && groupIdx < _beatmapGroups.Count ? _beatmapGroups[groupIdx] : null;
    }

    private BeatmapInfo? GetGroupPrimary(BeatmapGroup group)
    {
        if (group.BeatmapIndices.Count == 0) return null;
        int bmIdx = group.BeatmapIndices[Math.Clamp(
            group.SelectedDifficultyIndex, 0, group.BeatmapIndices.Count - 1)];
        return bmIdx >= 0 && bmIdx < _beatmaps.Count ? _beatmaps[bmIdx] : null;
    }

    private static string GetGroupKey(BeatmapInfo bm)
    {
        string title = bm.Title.Trim().ToLowerInvariant();
        string artist = bm.Artist.Trim().ToLowerInvariant();

        if (LazerAudioResolver.IsLazerPath(bm.FilePath))
            return $"lazer|{artist}|{title}";

        string folder = string.IsNullOrWhiteSpace(bm.FolderPath)
            ? Path.GetDirectoryName(bm.FilePath) ?? ""
            : bm.FolderPath;
        return $"{folder.Trim().ToLowerInvariant()}|{artist}|{title}";
    }

    private string BuildGroupSearchText(BeatmapGroup group)
    {
        var primary = GetGroupPrimary(group);
        var parts = new List<string>();
        if (primary != null)
        {
            parts.Add(primary.Title);
            parts.Add(primary.Artist);
        }

        foreach (int bmIdx in group.BeatmapIndices)
        {
            if (bmIdx < 0 || bmIdx >= _beatmaps.Count) continue;
            var bm = _beatmaps[bmIdx];
            parts.Add(bm.Version);
            parts.Add(bm.Creator);
            parts.Add(GetDifficultyLabel(bm.OD));
        }

        return string.Join(' ', parts);
    }

    private bool IsPracticeItem(int filterIdx)
        => filterIdx < 0 || filterIdx >= _filteredIndices.Count || _beatmapGroups.Count == 0;

    private BeatmapInfo? GetItemBeatmap(int filterIdx)
    {
        var group = GetGroupAtFilterIndex(filterIdx);
        if (group == null || group.BeatmapIndices.Count == 0) return null;

        group.SelectedDifficultyIndex = Math.Clamp(
            group.SelectedDifficultyIndex, 0, group.BeatmapIndices.Count - 1);
        int bmIdx = group.BeatmapIndices[group.SelectedDifficultyIndex];
        return bmIdx >= 0 && bmIdx < _beatmaps.Count ? _beatmaps[bmIdx] : null;
    }

    private string GetItemTitle(int filterIdx)
    {
        var group = GetGroupAtFilterIndex(filterIdx);
        return GetGroupPrimary(group ?? new BeatmapGroup())?.Title ?? "Practice Mode";
    }

    private string GetItemSubtitle(int filterIdx)
    {
        var group = GetGroupAtFilterIndex(filterIdx);
        var bm = group == null ? null : GetGroupPrimary(group);
        if (bm == null) return "Generated patterns / 160 BPM";
        int diffCount = group?.BeatmapIndices.Count ?? 1;
        return diffCount > 1 ? $"{bm.Artist} / {diffCount} difficulties" : bm.Artist;
    }

    private string GetItemVersion(int filterIdx)
        => GetItemBeatmap(filterIdx)?.Version ?? "Warmup";

    private float GetItemOd(int filterIdx)
        => GetItemBeatmap(filterIdx)?.OD ?? 5.0f;

    private string GetItemOdLabel(int filterIdx)
        => IsPracticeItem(filterIdx) ? "OD 5.0" : $"OD {GetItemOd(filterIdx):F1}";

    private string GetItemDifficultyLabel(int filterIdx)
        => IsPracticeItem(filterIdx) ? "Easy" : GetDifficultyLabel(GetItemOd(filterIdx));

    private string GetItemSource(int filterIdx)
    {
        var bm = GetItemBeatmap(filterIdx);
        return bm == null ? "Generated" : GetFriendlySource(bm.FilePath);
    }

    private void DrawSelectBackdrop(SpriteBatch batch, Texture2D px, int sw, int sh, float fadeA)
    {
        if (_background.HasBackground)
        {
            _background.Render();
            batch.Draw(px, 0, 0, sw, sh, 0.01f, 0.01f, 0.02f, 0.24f * fadeA);
        }
        else
        {
            batch.Draw(px, 0, 0, sw, sh, 0.025f, 0.025f, 0.045f, 1f);
            for (int i = 0; i < 16; i++)
            {
                float x = i * 112f + MathF.Sin((float)_time * 0.65f + i) * 8f;
                float h = 90f + (i % 5) * 34f;
                batch.Draw(px, x, sh - h - 84f, 44f, h,
                    i % 2 == 0 ? 0.10f : 0.16f,
                    i % 3 == 0 ? 0.04f : 0.11f,
                    i % 2 == 0 ? 0.06f : 0.04f,
                    0.30f * fadeA);
            }
        }

        batch.Draw(px, 0, 0, sw, 140f, 0f, 0f, 0f, 0.34f * fadeA);
        batch.Draw(px, 0, sh - 170f, sw, 170f, 0f, 0f, 0f, 0.46f * fadeA);

        for (int i = 0; i < 9; i++)
        {
            float y = 142f + i * 72f;
            batch.Draw(px, 0, y, sw, 1f, 1f, 1f, 1f, 0.025f * fadeA);
        }
    }

    private void DrawTopChrome(SpriteBatch batch, Engine.Text.BitmapFont font,
        Texture2D px, int sw, float fadeA)
    {
        batch.Draw(px, 0, 0, sw, TopBarH, 0.028f, 0.030f, 0.048f, 0.90f * fadeA);
        batch.Draw(px, 0, TopBarH - 3f, sw, 3f,
            SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2], 0.70f * fadeA);
        batch.Draw(px, 0, TopBarH, sw, 1f, 1f, 1f, 1f, 0.08f * fadeA);

        DrawRoundedRect(batch, px, 18f, 12f, 184f, 44f, 20f,
            0.09f, 0.095f, 0.13f, 0.82f * fadeA);
        font.DrawTextShadow(batch, "TAIKO NOVA", 28f, 18f, 1.0f,
            1f, 1f, 1f, fadeA, 2f);
        font.DrawText(batch, "LEVEL SELECT", 214f, 28f, 0.58f,
            0.58f, 0.60f, 0.68f, 0.78f * fadeA);

        string count = _searchQuery.Length > 0
            ? $"{_filteredIndices.Count}/{_beatmaps.Count} MAPS"
            : $"{_beatmaps.Count} MAPS";
        float countW = font.MeasureWidth(count, 0.66f);
        DrawRoundedRect(batch, px, sw - countW - 54f, 18f, countW + 30f, 32f, 16f,
            0.07f, 0.075f, 0.105f, 0.76f * fadeA);
        font.DrawTextRight(batch, count, sw - 30f, 26f, 0.66f,
            0.68f, 0.70f, 0.78f, 0.82f * fadeA);
    }

    private void DrawBrowserFrame(SpriteBatch batch, Engine.Text.BitmapFont font,
        Texture2D px, float x, float y, float w, float h, float fadeA)
    {
        DrawRoundedRect(batch, px, x, y, w, h, 22f,
            0.030f, 0.032f, 0.052f, 0.74f * fadeA);
        DrawRoundedRect(batch, px, x + 8f, y + 12f, 5f, h - 24f, 3f,
            SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2], 0.55f * fadeA);
        DrawRoundedRect(batch, px, x + 12f, y + 8f, w - 24f, 1f, 0.5f,
            1f, 1f, 1f, 0.08f * fadeA);
        DrawRoundedRect(batch, px, x + 12f, y + h - 9f, w - 24f, 1f, 0.5f,
            0f, 0f, 0f, 0.40f * fadeA);

        font.DrawText(batch, "BROWSE", x + 18f, y + 18f, 0.72f,
            0.86f, 0.88f, 0.94f, 0.92f * fadeA);

        string status = _searchQuery.Length > 0
            ? $"{_filteredIndices.Count} MATCHES"
            : $"{_beatmaps.Count} SONGS";
        font.DrawTextRight(batch, status, x + w - 18f, y + 20f, 0.52f,
            0.46f, 0.48f, 0.56f, 0.75f * fadeA);

        DrawRoundedRect(batch, px, x + 18f, y + 42f, w - 36f, 1f, 0.5f,
            1f, 1f, 1f, 0.08f * fadeA);
    }

    private void DrawSearchDock(SpriteBatch batch, Engine.Text.BitmapFont font,
        Texture2D px, float x, float y, float w, float h, float fadeA)
    {
        if (h <= 1f) return;

        float a = fadeA * Clamp01(h / SearchBarH);
        DrawRoundedRect(batch, px, x, y, w, h, 18f,
            0.08f, 0.08f, 0.12f, 0.94f * a);
        DrawRoundedRect(batch, px, x + 10f, y + h - 3f, w - 20f, 2f, 1f,
            SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2], 0.55f * a);

        float textScale = 0.60f;
        float ty = y + MathF.Max(0f, (h - font.MeasureHeight(textScale)) * 0.5f);
        string display = _searchQuery.Length > 0 ? _searchQuery : "type to filter";
        float textW = w - 92f;
        if (font.MeasureWidth(display, textScale) > textW)
            display = TruncateToFit(font, display, textScale, textW);

        font.DrawText(batch, ">", x + 12f, ty, textScale,
            SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2], 0.95f * a);
        font.DrawText(batch, display, x + 36f, ty, textScale,
            _searchQuery.Length > 0 ? 0.92f : 0.45f,
            _searchQuery.Length > 0 ? 0.92f : 0.45f,
            _searchQuery.Length > 0 ? 0.98f : 0.52f,
            a);

        if (_searchActive && _searchCursorBlink < 0.5f)
        {
            float curX = x + 38f + font.MeasureWidth(display, textScale);
            batch.Draw(px, curX, ty + 1f, 2f, font.MeasureHeight(textScale) - 2f,
                1f, 1f, 1f, 0.75f * a);
        }
    }

    private void DrawBrowserRows(SpriteBatch batch, Engine.Text.BitmapFont font,
        Texture2D px, float railX, float railW, float listTop, float listBottom,
        int totalItems, float fadeA, float exitSlide)
    {
        if (_filteredIndices.Count == 0 && _searchQuery.Length > 0)
        {
            string empty = "NO MAPS MATCH";
            float ew = font.MeasureWidth(empty, 0.72f);
            font.DrawText(batch, empty, railX + (railW - ew) * 0.5f, listTop + 18f,
                0.72f, 0.72f, 0.74f, 0.82f, 0.55f * fadeA);
        }

        for (int fi = 0; fi < totalItems; fi++)
        {
            float baseY = listTop + fi * CardItemH - _scrollOffset;
            if (baseY + CardH < listTop || baseY > listBottom) continue;

            float edgeFade = MathF.Min(
                Clamp01((baseY + CardH - listTop) / 42f),
                Clamp01((listBottom - baseY) / 42f));
            if (edgeFade <= 0.01f) continue;

            bool selected = fi == _selectedFilterIdx;
            bool isPractice = fi >= _filteredIndices.Count;
            float slideX = fi < _cardSlide.Length ? _cardSlide[fi] : 0f;
            float rowX = railX + 18f + slideX + exitSlide + (selected ? 10f : 0f);
            float rowW = railW - 36f - (selected ? 0f : 18f);
            float rowA = fadeA * edgeFade;
            float pop = selected ? EaseOutCubic(_selectionFlash) : 0f;
            float rowY = baseY - pop * 4f;

            if (isPractice)
            {
                DrawPracticeRow(batch, font, px, rowX, rowY, rowW, selected, rowA);
                continue;
            }

            int bmIdx = _filteredIndices[fi];
            var bm = _beatmaps[bmIdx];
            float[] odColor = GetOdColor(bm.OD);

            if (selected)
            {
                float glowA = (0.14f + _selectGlow * 0.08f) * rowA;
                DrawRoundedRect(batch, px, rowX - 12f, rowY - 7f, rowW + 24f, CardH + 14f, 22f,
                    SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2], glowA);
                DrawRoundedRect(batch, px, rowX, rowY, rowW, CardH, 18f,
                    0.13f, 0.135f, 0.17f, 0.98f * rowA);
                DrawRoundedRect(batch, px, rowX + 8f, rowY + 10f, 5f, CardH - 20f, 3f,
                    SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2], rowA);
                DrawRoundedRect(batch, px, rowX + 16f, rowY + 4f, rowW - 32f, 1f, 0.5f,
                    1f, 1f, 1f, 0.12f * rowA);
                if (_selectionFlash > 0.01f)
                {
                    DrawRoundedRect(batch, px, rowX, rowY, rowW, CardH, 18f,
                        1f, 1f, 1f, _selectionFlash * 0.10f * rowA);
                }
            }
            else
            {
                int dist = Math.Abs(fi - _selectedFilterIdx);
                float dim = MathF.Max(0.32f, 0.82f - dist * 0.10f);
                DrawRoundedRect(batch, px, rowX, rowY, rowW, CardH, 16f,
                    0.065f, 0.066f, 0.086f, dim * rowA);
                DrawRoundedRect(batch, px, rowX + 16f, rowY + CardH - 2f, rowW - 32f, 1f, 0.5f,
                    1f, 1f, 1f, 0.035f * rowA);
            }

            string idx = (fi + 1).ToString(fi < 99 ? "00" : "000");
            float idxScale = selected ? 0.60f : 0.50f;
            font.DrawCentered(batch, idx, rowX + 30f, rowY + CardH * 0.5f, idxScale,
                selected ? SkinConfig.Accent[0] : 0.42f,
                selected ? SkinConfig.Accent[1] : 0.43f,
                selected ? SkinConfig.Accent[2] : 0.50f,
                rowA * (selected ? 0.95f : 0.70f));

            float textX = rowX + 58f;
            float badgeW = selected ? 82f : 58f;
            float textW = rowW - 76f - badgeW;
            float titleScale = selected ? 0.82f : 0.68f;
            string title = TruncateToFit(font, bm.Title, titleScale, textW);
            font.DrawText(batch, title, textX, rowY + (selected ? 10f : 12f), titleScale,
                selected ? 1f : 0.72f,
                selected ? 1f : 0.73f,
                selected ? 1f : 0.80f,
                rowA);

            string subtitle = bm.Artist;
            if (!string.IsNullOrEmpty(bm.Version))
                subtitle += selected ? $" / {bm.Version}" : $" [{bm.Version}]";
            subtitle = TruncateToFit(font, subtitle, 0.52f, textW);
            font.DrawText(batch, subtitle, textX, rowY + 37f, 0.52f,
                0.48f, 0.50f, 0.58f, rowA * 0.90f);

            string odText = bm.OD > 0 ? $"OD {bm.OD:F1}" : "OD --";
            float odX = rowX + rowW - badgeW - 12f;
            float odY = rowY + (CardH - 22f) * 0.5f;
            DrawRoundedRect(batch, px, odX, odY, badgeW, 22f, 11f,
                odColor[0] * 0.20f, odColor[1] * 0.20f, odColor[2] * 0.20f, 0.90f * rowA);
            font.DrawCentered(batch, odText, odX + badgeW * 0.5f, odY + 11f, 0.48f,
                odColor[0], odColor[1], odColor[2], rowA);
        }
    }

    private void DrawPracticeRow(SpriteBatch batch, Engine.Text.BitmapFont font,
        Texture2D px, float rowX, float rowY, float rowW, bool selected, float rowA)
    {
        float[] accent = SkinConfig.DrumrollColor;
        if (selected)
        {
            DrawRoundedRect(batch, px, rowX - 12f, rowY - 7f, rowW + 24f, CardH + 14f, 22f,
                accent[0], accent[1], accent[2], (0.12f + _selectGlow * 0.07f) * rowA);
            DrawRoundedRect(batch, px, rowX, rowY, rowW, CardH, 18f,
                0.13f, 0.12f, 0.08f, 0.96f * rowA);
            DrawRoundedRect(batch, px, rowX + 8f, rowY + 10f, 5f, CardH - 20f, 3f,
                accent[0], accent[1], accent[2], rowA);
            if (_selectionFlash > 0.01f)
            {
                DrawRoundedRect(batch, px, rowX, rowY, rowW, CardH, 18f,
                    1f, 1f, 1f, _selectionFlash * 0.10f * rowA);
            }
        }
        else
        {
            DrawRoundedRect(batch, px, rowX, rowY, rowW, CardH, 16f,
                0.075f, 0.070f, 0.050f, 0.68f * rowA);
        }

        font.DrawCentered(batch, "P", rowX + 30f, rowY + CardH * 0.5f, selected ? 0.62f : 0.52f,
            accent[0], accent[1], accent[2], rowA);
        font.DrawText(batch, "Practice Mode", rowX + 58f, rowY + 11f, selected ? 0.82f : 0.68f,
            accent[0], accent[1], accent[2], rowA);
        font.DrawText(batch, "160 BPM / generated patterns", rowX + 58f, rowY + 37f, 0.52f,
            0.56f, 0.50f, 0.36f, rowA * 0.88f);
    }

    private void DrawRailScroll(SpriteBatch batch, Texture2D px,
        float railX, float railW, float listTop, float listBottom, int totalItems, float fadeA)
    {
        if (totalItems <= 1) return;

        float trackX = railX + railW - 9f;
        float trackTop = listTop;
        float trackH = listBottom - listTop;
        float thumbPct = MathF.Min(1f, trackH / MathF.Max(trackH, totalItems * CardItemH));
        float thumbH = MathF.Max(28f, trackH * thumbPct);
        float scrollPct = (float)_selectedFilterIdx / MathF.Max(1, totalItems - 1);
        float thumbY = trackTop + (trackH - thumbH) * scrollPct;

        DrawRoundedRect(batch, px, trackX, trackTop, 4f, trackH, 2f,
            1f, 1f, 1f, 0.07f * fadeA);
        DrawRoundedRect(batch, px, trackX - 1f, thumbY, 6f, thumbH, 3f,
            SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2], 0.58f * fadeA);
    }

    private void DrawHeroDetails(SpriteBatch batch, Engine.Text.BitmapFont font,
        Texture2D px, float x, float y, float w, float h, float fadeA)
    {
        bool practice = _selectedFilterIdx >= _filteredIndices.Count || SelectedBeatmapIndex >= _beatmaps.Count;
        float[] accent = practice ? SkinConfig.DrumrollColor : SkinConfig.Accent;

        DrawRoundedRect(batch, px, x + 10f, y + 12f, w, h, 28f,
            0f, 0f, 0f, 0.24f * fadeA);
        DrawRoundedRect(batch, px, x, y, w, h, 28f,
            0.026f, 0.028f, 0.046f, 0.68f * fadeA);
        DrawRoundedRect(batch, px, x + 16f, y + 10f, w - 32f, 1f, 0.5f,
            1f, 1f, 1f, 0.10f * fadeA);
        DrawRoundedRect(batch, px, x + 16f, y + h - 11f, w - 32f, 1f, 0.5f,
            0f, 0f, 0f, 0.45f * fadeA);
        DrawRoundedRect(batch, px, x + 10f, y + 14f, 6f, h - 28f, 3f,
            accent[0], accent[1], accent[2], 0.68f * fadeA);

        for (int i = 0; i < 10; i++)
        {
            float dotX = x + w - 38f - i * 24f;
            float dotY = y + 24f + MathF.Sin((float)_time * 2.0f + i * 0.8f) * 3f;
            float[] c = i % 3 == 0 ? SkinConfig.KatColor : (i % 3 == 1 ? SkinConfig.DonColor : accent);
            batch.Draw(_uiCircle, dotX, dotY, 12f, 12f, c[0], c[1], c[2], 0.38f * fadeA);
        }

        if (practice)
            DrawPracticeHero(batch, font, px, x, y, w, h, fadeA);
        else
            DrawSongHero(batch, font, px, x, y, w, h, fadeA);
    }

    private void DrawSongHero(SpriteBatch batch, Engine.Text.BitmapFont font,
        Texture2D px, float x, float y, float w, float h, float fadeA)
    {
        var bm = _beatmaps[SelectedBeatmapIndex];
        float contentX = x + 32f;
        float contentW = w - 64f;
        float cy = y + 40f + (1f - EaseOutCubic(MathF.Min(1f, _infoReveal))) * 18f;
        float revealA = fadeA * EaseOutCubic(MathF.Min(1f, _infoReveal));

        font.DrawText(batch, "SELECTED LEVEL", contentX, cy, 0.52f,
            SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2], 0.84f * revealA);
        cy += 30f;

        string title = bm.Title;
        float titleScale = 1.46f;
        while (titleScale > 0.92f && font.MeasureWidth(title, titleScale) > contentW)
            titleScale -= 0.06f;
        if (font.MeasureWidth(title, titleScale) > contentW)
            title = TruncateToFit(font, title, titleScale, contentW);
        font.DrawTextShadow(batch, title, contentX, cy, titleScale,
            1f, 1f, 1f, revealA, 3f);
        cy += font.MeasureHeight(titleScale) + 12f;

        string artist = TruncateToFit(font, bm.Artist, 0.78f, contentW);
        font.DrawText(batch, artist, contentX, cy, 0.78f,
            0.68f, 0.70f, 0.80f, 0.90f * revealA);
        cy += 42f;

        batch.Draw(px, contentX, cy, contentW, 1f,
            SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2], 0.38f * revealA);
        cy += 26f;

        cy = DrawHeroRow(batch, font, px, contentX, cy, contentW,
            "DIFFICULTY", string.IsNullOrEmpty(bm.Version) ? "Unknown" : bm.Version, revealA);
        cy = DrawHeroRow(batch, font, px, contentX, cy, contentW,
            "MAPPER", string.IsNullOrEmpty(bm.Creator) ? "Unknown" : bm.Creator, revealA);
        cy = DrawHeroRow(batch, font, px, contentX, cy, contentW,
            "SOURCE", GetFriendlySource(bm.FilePath), revealA);

        cy += 16f;
        DrawOdMeter(batch, font, px, contentX, cy, contentW, bm.OD, revealA);

        if (_previewPlaying && _previewVolume > 0.01f)
            DrawPreviewPulse(batch, font, px, contentX, y + h - 132f, contentW, revealA * _previewVolume);

        DrawPlayStrip(batch, font, px, contentX, y + h - 82f, contentW, "PLAY", SkinConfig.Accent, revealA);
    }

    private void DrawPracticeHero(SpriteBatch batch, Engine.Text.BitmapFont font,
        Texture2D px, float x, float y, float w, float h, float fadeA)
    {
        float contentX = x + 32f;
        float contentW = w - 64f;
        float cy = y + 40f + (1f - EaseOutCubic(MathF.Min(1f, _infoReveal))) * 18f;
        float revealA = fadeA * EaseOutCubic(MathF.Min(1f, _infoReveal));

        font.DrawText(batch, "WARMUP LEVEL", contentX, cy, 0.52f,
            SkinConfig.DrumrollColor[0], SkinConfig.DrumrollColor[1],
            SkinConfig.DrumrollColor[2], 0.84f * revealA);
        cy += 30f;

        font.DrawTextShadow(batch, "Practice Mode", contentX, cy, 1.46f,
            SkinConfig.DrumrollColor[0], SkinConfig.DrumrollColor[1],
            SkinConfig.DrumrollColor[2], revealA, 3f);
        cy += font.MeasureHeight(1.46f) + 14f;

        font.DrawText(batch, "Generated taiko patterns", contentX, cy, 0.78f,
            0.70f, 0.64f, 0.46f, 0.90f * revealA);
        cy += 42f;

        batch.Draw(px, contentX, cy, contentW, 1f,
            SkinConfig.DrumrollColor[0], SkinConfig.DrumrollColor[1],
            SkinConfig.DrumrollColor[2], 0.38f * revealA);
        cy += 26f;

        cy = DrawHeroRow(batch, font, px, contentX, cy, contentW, "BPM", "160", revealA);
        cy = DrawHeroRow(batch, font, px, contentX, cy, contentW, "AUDIO", "None", revealA);
        cy = DrawHeroRow(batch, font, px, contentX, cy, contentW, "PATTERNS", "Auto-generated", revealA);

        DrawPlayStrip(batch, font, px, contentX, y + h - 82f, contentW,
            "PRACTICE", SkinConfig.DrumrollColor, revealA);
    }

    private float DrawHeroRow(SpriteBatch batch, Engine.Text.BitmapFont font,
        Texture2D px, float x, float y, float w, string label, string value, float fadeA)
    {
        DrawRoundedRect(batch, px, x, y, w, 32f, 12f,
            0.08f, 0.08f, 0.11f, 0.45f * fadeA);
        font.DrawText(batch, label, x + 12f, y + 9f, 0.50f,
            0.42f, 0.44f, 0.52f, 0.82f * fadeA);

        float labelW = 150f;
        string fitted = TruncateToFit(font, value, 0.58f, w - labelW - 18f);
        font.DrawTextRight(batch, fitted, x + w - 12f, y + 8f, 0.58f,
            0.82f, 0.84f, 0.92f, fadeA);
        return y + 40f;
    }

    private void DrawOdMeter(SpriteBatch batch, Engine.Text.BitmapFont font,
        Texture2D px, float x, float y, float w, float od, float fadeA)
    {
        string label = $"OD {od:F1}";
        float[] odColor = GetOdColor(od);
        font.DrawText(batch, label, x, y, 0.66f,
            odColor[0], odColor[1], odColor[2], fadeA);

        float meterX = x;
        float meterY = y + 30f;
        float gap = 5f;
        float segW = (w - gap * 9f) / 10f;
        int filled = (int)MathF.Ceiling(Math.Clamp(od, 0f, 10f));
        for (int i = 0; i < 10; i++)
        {
            bool on = i < filled;
            float sx = meterX + i * (segW + gap);
            DrawRoundedRect(batch, px, sx, meterY, segW, 10f, 5f,
                on ? odColor[0] : 0.16f,
                on ? odColor[1] : 0.16f,
                on ? odColor[2] : 0.19f,
                on ? 0.88f * fadeA : 0.42f * fadeA);
        }
    }

    private void DrawPreviewPulse(SpriteBatch batch, Engine.Text.BitmapFont font,
        Texture2D px, float x, float y, float w, float fadeA)
    {
        font.DrawText(batch, "PREVIEW", x, y, 0.52f,
            SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2], 0.72f * fadeA);
        float barsX = x + 96f;
        for (int i = 0; i < 8; i++)
        {
            float t = (float)_time * 4.2f + i * 0.7f;
            float barH = 8f + (MathF.Sin(t) * 0.5f + 0.5f) * 18f;
            DrawRoundedRect(batch, px, barsX + i * 9f, y + 24f - barH, 5f, barH, 2.5f,
                SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2], 0.65f * fadeA);
        }
    }

    private void DrawPlayStrip(SpriteBatch batch, Engine.Text.BitmapFont font,
        Texture2D px, float x, float y, float w, string action, float[] accent, float fadeA)
    {
        DrawRoundedRect(batch, px, x, y, w, 54f, 20f,
            accent[0] * 0.20f, accent[1] * 0.20f, accent[2] * 0.20f, 0.92f * fadeA);
        DrawRoundedRect(batch, px, x + 10f, y + 10f, 5f, 34f, 3f,
            accent[0], accent[1], accent[2], fadeA);
        DrawRoundedRect(batch, px, x + 18f, y + 6f, w - 36f, 1f, 0.5f,
            1f, 1f, 1f, 0.10f * fadeA);

        DrawRoundedRect(batch, px, x + 22f, y + 15f, 68f, 24f, 12f,
            0.02f, 0.02f, 0.03f, 0.64f * fadeA);
        font.DrawCentered(batch, "ENTER", x + 56f, y + 27f, 0.48f,
            0.92f, 0.94f, 1f, fadeA);

        font.DrawText(batch, action, x + 108f, y + 13f, 0.96f,
            1f, 1f, 1f, fadeA);

        batch.Draw(_uiCircle, x + w - 76f, y + 12f, 30f, 30f,
            SkinConfig.DonColor[0], SkinConfig.DonColor[1], SkinConfig.DonColor[2], 0.86f * fadeA);
        batch.Draw(_uiCircle, x + w - 42f, y + 12f, 30f, 30f,
            SkinConfig.KatColor[0], SkinConfig.KatColor[1], SkinConfig.KatColor[2], 0.86f * fadeA);
    }

    private void DrawFooter(SpriteBatch batch, Engine.Text.BitmapFont font,
        Texture2D px, int sw, int sh, float fadeA)
    {
        float bottomY = sh - BottomBarH;
        batch.Draw(px, 0, bottomY, sw, BottomBarH, 0.028f, 0.030f, 0.048f, 0.94f * fadeA);
        batch.Draw(px, 0, bottomY, sw, 1f, 1f, 1f, 1f, 0.08f * fadeA);

        float ctrlY = bottomY + 15f;
        float cx = 28f;
        cx = DrawKeyHint(batch, font, px, cx, ctrlY, "UP/DOWN", "Move", fadeA);
        cx = DrawKeyHint(batch, font, px, cx + 18f, ctrlY, "ENTER", "Play", fadeA);
        cx = DrawKeyHint(batch, font, px, cx + 18f, ctrlY, "TAB", "Search", fadeA);
        cx = DrawKeyHint(batch, font, px, cx + 18f, ctrlY, "F2", "Random", fadeA);
        cx = DrawKeyHint(batch, font, px, cx + 18f, ctrlY, "F5", "Refresh", fadeA);
        DrawKeyHint(batch, font, px, cx + 18f, ctrlY, "ESC", "Back", fadeA);
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

        batch.Draw(_uiCircle, x, y, d, d, r, g, b, a);
        batch.Draw(_uiCircle, x + w - d, y, d, d, r, g, b, a);
        batch.Draw(_uiCircle, x, y + h - d, d, d, r, g, b, a);
        batch.Draw(_uiCircle, x + w - d, y + h - d, d, d, r, g, b, a);
    }

    private void DrawRoundedRect(SpriteBatch batch, Texture2D px,
        float x, float y, float w, float h, float radius, float[] color, float alphaMul = 1f)
    {
        DrawRoundedRect(batch, px, x, y, w, h, radius,
            color[0], color[1], color[2], color[3] * alphaMul);
    }

    private static float Clamp01(float v)
        => MathF.Max(0f, MathF.Min(1f, v));

    private void RenderLegacy(double dt)
    {
        var batch = Engine.SpriteBatch;
        var font  = Engine.Font;
        var px    = Engine.PixelTex;
        var proj  = Engine.Projection;
        int sw    = Engine.ScreenWidth;
        int sh    = Engine.ScreenHeight;
        float fadeA = EaseOutCubic(_enterAnim);

        // Exit animation: fade + slide
        float exitFade = 1f;
        float exitSlide = 0f;
        if (_exiting)
        {
            float t = EaseOutCubic(_exitAnim);
            exitFade = 1f - t;
            exitSlide = t * 60f; // cards slide right
        }
        fadeA *= exitFade;

        float infoPanelW = sw * InfoPanelPct;
        float listW = sw - infoPanelW;

        batch.Begin(proj);

        // ── Full-screen background ──
        if (_background.HasBackground)
        {
            _background.Render();
            // Exit: fade to black
            if (_exiting)
                batch.Draw(px, 0, 0, sw, sh, 0f, 0f, 0f, _exitAnim * 0.7f);
        }
        else
        {
            batch.Draw(px, 0, 0, sw, sh, 0f, 0f, 0f, 1f);
        }

        // Subtle gradient overlay on the right side
        batch.Draw(px, listW, 0, infoPanelW, sh, 0.04f, 0.04f, 0.08f, 0.95f * exitFade);

        // ── Search bar (below top bar, above cards) ──
        float searchBarOffset = 0f;
        if (_searchBarReveal > 0.01f)
        {
            float sbH = SearchBarH * _searchBarReveal;
            float sbY = TopBarH;
            searchBarOffset = sbH;

            // Background
            batch.Draw(px, 0, sbY, listW, sbH, 0.06f, 0.06f, 0.10f, 0.95f * fadeA);
            // Bottom edge
            batch.Draw(px, 0, sbY + sbH - 1, listW, 1,
                SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2], 0.3f * fadeA);

            // Search icon (magnifying glass as text)
            float searchTextY = sbY + (sbH - font.MeasureHeight(0.7f)) * 0.5f;
            font.DrawText(batch, ">", 16, searchTextY, 0.7f,
                SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2], fadeA * 0.9f);

            // Query text
            string displayQuery = _searchQuery;
            float cursorAlpha = _searchCursorBlink < 0.5f ? 0.9f : 0.2f;
            font.DrawText(batch, displayQuery, 38, searchTextY, 0.7f,
                0.9f, 0.9f, 0.95f, fadeA);

            // Cursor
            float cursorX = 38 + font.MeasureWidth(displayQuery, 0.7f) + 2;
            batch.Draw(px, cursorX, searchTextY, 2, font.MeasureHeight(0.7f),
                1f, 1f, 1f, cursorAlpha * fadeA);

            // Result count
            if (_searchQuery.Length > 0)
            {
                string resultStr = $"{_filteredIndices.Count} found";
                font.DrawTextRight(batch, resultStr, (int)(listW - 16), searchTextY, 0.6f,
                    0.5f, 0.5f, 0.6f, fadeA * 0.7f);
            }
        }

        // ── Song card list ──
        int totalItems = _filteredIndices.Count + 1;
        float listTop = TopBarH + 4 + searchBarOffset;
        float listBottom = sh - BottomBarH;

        // Empty state
        if (_filteredIndices.Count == 0 && _searchQuery.Length > 0)
        {
            float emptyY = (listTop + listBottom) * 0.5f - 30f;
            font.DrawTextShadow(batch, "No maps found", listW * 0.5f - font.MeasureWidth("No maps found", 1.0f) * 0.5f,
                emptyY, 1.0f, 0.5f, 0.5f, 0.6f, fadeA * 0.8f, 2f);
            string hint = "Try different search terms";
            font.DrawText(batch, hint, listW * 0.5f - font.MeasureWidth(hint, 0.6f) * 0.5f,
                emptyY + 30, 0.6f, 0.4f, 0.4f, 0.5f, fadeA * 0.5f);
        }
        else if (_beatmaps.Count == 0)
        {
            float emptyY = (listTop + listBottom) * 0.5f - 40f;
            font.DrawTextShadow(batch, "No songs found", listW * 0.5f - font.MeasureWidth("No songs found", 1.0f) * 0.5f,
                emptyY, 1.0f, 0.6f, 0.6f, 0.7f, fadeA * 0.9f, 2f);
            string[] hints = {
                "Drop .osz files into the Songs folder",
                "or install osu! stable/lazer",
                "Press F5 to rescan"
            };
            for (int h = 0; h < hints.Length; h++)
            {
                float hw = font.MeasureWidth(hints[h], 0.6f);
                font.DrawText(batch, hints[h], listW * 0.5f - hw * 0.5f,
                    emptyY + 34 + h * 22, 0.6f, 0.4f, 0.4f, 0.5f, fadeA * 0.6f);
            }
        }

        for (int fi = 0; fi < totalItems; fi++)
        {
            float baseY = listTop + fi * CardItemH - _scrollOffset;

            // Strict culling
            if (baseY + CardH < listTop || baseY > listBottom) continue;

            bool selected = fi == _selectedFilterIdx;
            float slideX = fi < _cardSlide.Length ? _cardSlide[fi] : 0;
            slideX += exitSlide; // exit animation slides cards right

            // Distance from selected card for depth effect
            int dist = Math.Abs(fi - _selectedFilterIdx);
            float depthDim = MathF.Max(0.45f, 1f - dist * 0.12f);
            float depthScale = selected ? 1f : MathF.Max(0.92f, 1f - dist * 0.015f);

            float cardX = ListPadX + slideX + (selected ? 8f : 4f + dist * 1f);
            float cardW = (listW - ListPadX * 2 - slideX - (selected ? 0f : 8f + dist * 1f)) * depthScale;
            float cardY = baseY;

            // ── Card background ──
            if (selected)
            {
                // Glow behind selected card
                float glowA = 0.10f + _selectGlow * 0.08f;
                batch.Draw(px, cardX - 6, cardY - 4, cardW + 12, CardH + 8,
                    SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2], glowA * fadeA);

                // Selection flash overlay
                if (_selectionFlash > 0.01f)
                {
                    batch.Draw(px, cardX - 6, cardY - 4, cardW + 12, CardH + 8,
                        1f, 1f, 1f, _selectionFlash * 0.12f * fadeA);
                }

                // Card fill
                batch.Draw(px, cardX, cardY, cardW, CardH,
                    0.14f, 0.14f, 0.18f, 0.98f * fadeA);

                // Left accent bar
                batch.Draw(px, cardX, cardY, 5f, CardH,
                    SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2], fadeA);

                // Top highlight line
                batch.Draw(px, cardX, cardY, cardW, 1f,
                    1f, 1f, 1f, 0.10f * fadeA);

                // Bottom subtle highlight
                batch.Draw(px, cardX, cardY + CardH - 1, cardW, 1f,
                    SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2], 0.15f * fadeA);
            }
            else
            {
                batch.Draw(px, cardX, cardY, cardW, CardH,
                    0.10f, 0.10f, 0.13f, (0.55f + depthDim * 0.30f) * fadeA);
            }

            // ── Card content ──
            float textX = cardX + 18f;
            float textAlpha = fadeA * (selected ? 1f : 0.35f + depthDim * 0.35f);

            bool isPractice = fi >= _filteredIndices.Count;

            if (!isPractice)
            {
                int bmIdx = _filteredIndices[fi];
                var bm = _beatmaps[bmIdx];
                float titleScale = selected ? 1.0f : 0.85f;
                float detailScale = 0.65f;

                // Title
                string title = bm.Title;
                if (font.MeasureWidth(title, titleScale) > cardW - 40)
                    title = TruncateToFit(font, title, titleScale, cardW - 50);

                font.DrawText(batch, title, textX, cardY + 10, titleScale,
                    1f, 1f, 1f, textAlpha);

                // Artist - Version
                string subtitle = $"{bm.Artist}";
                if (!string.IsNullOrEmpty(bm.Version))
                    subtitle += $"  [{bm.Version}]";
                if (font.MeasureWidth(subtitle, detailScale) > cardW - 40)
                    subtitle = TruncateToFit(font, subtitle, detailScale, cardW - 50);

                font.DrawText(batch, subtitle, textX, cardY + 34, detailScale,
                    0.6f, 0.6f, 0.7f, textAlpha * 0.9f);

                // OD badge on the right
                if (selected)
                {
                    string odStr = $"OD {bm.OD:F1}";
                    float odW = font.MeasureWidth(odStr, 0.6f) + 12;
                    float odX = cardX + cardW - odW - 12;
                    float odY = cardY + 10;

                    float[] odColor = GetOdColor(bm.OD);
                    batch.Draw(px, odX, odY, odW, 20,
                        odColor[0], odColor[1], odColor[2], 0.25f * fadeA);
                    font.DrawText(batch, odStr, odX + 6, odY + 3, 0.6f,
                        odColor[0], odColor[1], odColor[2], fadeA);
                }

                // Bottom separator
                if (!selected)
                    batch.Draw(px, cardX + 12, cardY + CardH - 1, cardW - 24, 1,
                        0.2f, 0.2f, 0.25f, 0.3f * fadeA);
            }
            else
            {
                // ── Practice Mode card ──
                float titleScale = selected ? 1.0f : 0.85f;
                font.DrawText(batch, "Practice Mode", textX, cardY + 10, titleScale,
                    SkinConfig.DrumrollColor[0], SkinConfig.DrumrollColor[1],
                    SkinConfig.DrumrollColor[2], textAlpha);
                font.DrawText(batch, "160 BPM  |  Generated patterns  |  No audio", textX, cardY + 34,
                    0.6f, 0.5f, 0.5f, 0.55f, textAlpha * 0.8f);
            }
        }

        // ── Scroll position indicator (thin bar on left edge) ──
        if (totalItems > 1)
        {
            float trackTop = listTop + 4;
            float trackH = listBottom - listTop - 8;
            float thumbPct = MathF.Min(1f, (listBottom - listTop) / (totalItems * CardItemH));
            float thumbH = MathF.Max(20f, trackH * thumbPct);
            float scrollPct = totalItems > 1
                ? (float)_selectedFilterIdx / (totalItems - 1)
                : 0f;
            float thumbY = trackTop + (trackH - thumbH) * scrollPct;

            // Track
            batch.Draw(px, 4, trackTop, 3, trackH, 0.2f, 0.2f, 0.25f, 0.15f * fadeA);
            // Thumb
            batch.Draw(px, 4, thumbY, 3, thumbH,
                SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2], 0.5f * fadeA);
        }

        // ── Top bar (drawn AFTER cards so it covers scrolled items) ──
        batch.Draw(px, 0, 0, sw, TopBarH, 0.08f, 0.08f, 0.12f, 1f * exitFade);
        batch.Draw(px, 0, TopBarH - 8, sw, 8, 0.08f, 0.08f, 0.12f, 0.6f * exitFade);
        batch.Draw(px, 0, TopBarH - 2, sw, 2, SkinConfig.Accent[0], SkinConfig.Accent[1],
            SkinConfig.Accent[2], 0.7f * fadeA);
        batch.Draw(px, 0, TopBarH, sw, 1, SkinConfig.Accent[0], SkinConfig.Accent[1],
            SkinConfig.Accent[2], 0.15f * fadeA);

        // Title
        font.DrawTextShadow(batch, "SELECT A SONG", 20, 12,
            0.9f, 1f, 1f, 1f, fadeA, 2f);

        // Song count + filter indicator
        string countStr = _searchQuery.Length > 0
            ? $"{_filteredIndices.Count}/{_beatmaps.Count} songs"
            : $"{_beatmaps.Count} songs";
        font.DrawTextRightShadow(batch, countStr, sw - 20, 14, 0.8f,
            0.6f, 0.6f, 0.7f, fadeA * 0.7f);

        // ── Bottom bar ──
        float bottomY = sh - BottomBarH;
        batch.Draw(px, 0, bottomY, sw, BottomBarH, 0.08f, 0.08f, 0.12f, 1f * exitFade);
        batch.Draw(px, 0, bottomY, sw, 1, 0.25f, 0.25f, 0.30f, 0.5f * fadeA);
        batch.Draw(px, 0, bottomY - 6, sw, 6, 0.08f, 0.08f, 0.12f, 0.4f * exitFade);

        // Controls
        float ctrlY = bottomY + 12;
        float cx = 20;
        cx = DrawKeyHint(batch, font, px, cx, ctrlY, "ARROWS", "Navigate", fadeA);
        cx = DrawKeyHint(batch, font, px, cx + 16, ctrlY, "ENTER", "Play", fadeA);
        cx = DrawKeyHint(batch, font, px, cx + 16, ctrlY, "TAB", "Search", fadeA);
        cx = DrawKeyHint(batch, font, px, cx + 16, ctrlY, "F2", "Random", fadeA);
        cx = DrawKeyHint(batch, font, px, cx + 16, ctrlY, "F5", "Refresh", fadeA);
        cx = DrawKeyHint(batch, font, px, cx + 16, ctrlY, "ESC", "Back", fadeA);

        // ── Audio preview indicator (small) ──
        if (_previewPlaying && _previewVolume > 0.01f)
        {
            string nowPlaying = "NOW PLAYING";
            float npW = font.MeasureWidth(nowPlaying, 0.45f);
            // Subtle animated bars
            float barAnim = MathF.Sin((float)_time * 4f);
            font.DrawText(batch, nowPlaying, sw - npW - 20, bottomY - 16, 0.45f,
                SkinConfig.Accent[0], SkinConfig.Accent[1], SkinConfig.Accent[2],
                fadeA * 0.4f * _previewVolume);
        }

        // ── Right panel: Song info ──
        RenderInfoPanel(batch, font, px, listW, infoPanelW, sh, fadeA);

        // ── Exit flash overlay ──
        if (_exiting && _exitAnim > 0.5f)
        {
            float flashA = (_exitAnim - 0.5f) * 2f; // 0→1 over second half
            batch.Draw(px, 0, 0, sw, sh, 1f, 1f, 1f, flashA * 0.15f);
        }

        batch.End();
    }

    /// <summary>Render the right-side info panel for the selected song.</summary>
    private void RenderInfoPanel(SpriteBatch batch, Engine.Text.BitmapFont font,
        Texture2D px, float panelX, float panelW, float sh, float fadeA)
    {
        float py = TopBarH + 20;
        float margin = 24f;
        float contentX = panelX + margin;
        float contentW = panelW - margin * 2;
        float revealAlpha = fadeA * EaseOutCubic(_infoReveal);
        float slideIn = (1f - EaseOutCubic(_infoReveal)) * 20f; // content slides up as it reveals

        // Panel separator line
        batch.Draw(px, panelX, TopBarH, 1f, sh - TopBarH - BottomBarH,
            0.2f, 0.2f, 0.25f, 0.3f * fadeA);

        if (_selectedFilterIdx >= _filteredIndices.Count)
        {
            // Practice mode info
            font.DrawTextShadow(batch, "PRACTICE MODE", contentX, py + slideIn, 1.1f,
                SkinConfig.DrumrollColor[0], SkinConfig.DrumrollColor[1],
                SkinConfig.DrumrollColor[2], revealAlpha, 2f);
            py += 40;

            batch.Draw(px, contentX, py + slideIn, contentW, 1, 0.3f, 0.3f, 0.35f, 0.5f * revealAlpha);
            py += 16;

            DrawInfoRow(batch, font, px, contentX, py + slideIn, contentW, "BPM", "160", revealAlpha); py += 32;
            DrawInfoRow(batch, font, px, contentX, py + slideIn, contentW, "Patterns", "Auto-generated", revealAlpha); py += 32;
            DrawInfoRow(batch, font, px, contentX, py + slideIn, contentW, "Audio", "None", revealAlpha); py += 32;

            py += 20;
            font.DrawText(batch, "Great for warming up", contentX, py + slideIn, 0.7f,
                0.5f, 0.5f, 0.55f, revealAlpha * 0.7f);
            py += 22;
            font.DrawText(batch, "and practicing hits!", contentX, py + slideIn, 0.7f,
                0.5f, 0.5f, 0.55f, revealAlpha * 0.7f);
            return;
        }

        var bm = _beatmaps[SelectedBeatmapIndex];

        // ── Song title (large, wraps to two lines if needed) ──
        float titleScale = 1.1f;
        py = DrawWrappedText(batch, font, bm.Title, contentX, py + slideIn, contentW,
            titleScale, 1f, 1f, 1f, revealAlpha, shadow: true);
        py += 8;

        // ── Artist ──
        string artist = bm.Artist;
        if (font.MeasureWidth(artist, 0.85f) > contentW)
            artist = TruncateToFit(font, artist, 0.85f, contentW);
        font.DrawText(batch, artist, contentX, py + slideIn, 0.85f,
            0.7f, 0.7f, 0.8f, revealAlpha * 0.9f);
        py += 28;

        // ── Divider ──
        batch.Draw(px, contentX, py + slideIn, contentW, 1, 0.3f, 0.3f, 0.35f, 0.5f * revealAlpha);
        py += 16;

        // ── Info rows (staggered reveal) ──
        int rowIdx = 0;
        if (!string.IsNullOrEmpty(bm.Version))
        {
            float rowAlpha = GetStaggeredAlpha(_infoReveal, rowIdx++);
            DrawInfoRow(batch, font, px, contentX, py + slideIn, contentW, "Difficulty", bm.Version, rowAlpha * fadeA);
            py += 32;
        }
        if (!string.IsNullOrEmpty(bm.Creator))
        {
            float rowAlpha = GetStaggeredAlpha(_infoReveal, rowIdx++);
            DrawInfoRow(batch, font, px, contentX, py + slideIn, contentW, "Mapper", bm.Creator, rowAlpha * fadeA);
            py += 32;
        }

        {
            float rowAlpha = GetStaggeredAlpha(_infoReveal, rowIdx++);
            DrawInfoRow(batch, font, px, contentX, py + slideIn, contentW, "OD", $"{bm.OD:F1}", rowAlpha * fadeA);
            py += 32;
        }

        // ── OD difficulty bar ──
        py += 4;
        float barW = contentW * 0.7f;
        float barH = 6f;
        float barReveal = GetStaggeredAlpha(_infoReveal, rowIdx);
        batch.Draw(px, contentX, py + slideIn, barW, barH, 0.15f, 0.15f, 0.18f, barReveal * fadeA * 0.8f);

        float fillPct = MathF.Min(1f, bm.OD / 10f);
        float animatedFill = fillPct * EaseOutCubic(MathF.Min(1f, _infoReveal * 1.5f));
        float[] odCol = GetOdColor(bm.OD);
        batch.Draw(px, contentX, py + slideIn, barW * animatedFill, barH,
            odCol[0], odCol[1], odCol[2], barReveal * fadeA * 0.9f);
        // Bar shine
        if (animatedFill > 0.01f)
            batch.Draw(px, contentX, py + slideIn, barW * animatedFill, 2f, 1f, 1f, 1f, barReveal * fadeA * 0.15f);
        py += 24;

        // ── Divider ──
        batch.Draw(px, contentX, py + slideIn, contentW, 1, 0.3f, 0.3f, 0.35f, 0.3f * revealAlpha);
        py += 20;

        // ── Source hint (small) ──
        string fileHint = GetFriendlySource(bm.FilePath);
        if (font.MeasureWidth(fileHint, 0.5f) > contentW)
            fileHint = TruncateToFit(font, fileHint, 0.5f, contentW);
        font.DrawText(batch, fileHint, contentX, py + slideIn, 0.5f,
            0.35f, 0.35f, 0.4f, revealAlpha * 0.5f);
    }

    /// <summary>Draw a label: value row in the info panel.</summary>
    private static void DrawInfoRow(SpriteBatch batch, Engine.Text.BitmapFont font,
        Texture2D px, float x, float y, float w, string label, string value, float fadeA)
    {
        font.DrawText(batch, label, x, y, 0.7f, 0.45f, 0.45f, 0.5f, fadeA * 0.8f);
        font.DrawTextRight(batch, value, x + w, y, 0.75f, 0.85f, 0.85f, 0.9f, fadeA);
    }

    /// <summary>Draw a key hint badge (key label + action name).</summary>
    private float DrawKeyHint(SpriteBatch batch, Engine.Text.BitmapFont font,
        Texture2D px, float x, float y, string key, string action, float fadeA)
    {
        float keyW = font.MeasureWidth(key, 0.6f) + 10;
        float keyH = 20f;

        // Key badge background
        DrawRoundedRect(batch, px, x, y, keyW, keyH, 8f,
            0.2f, 0.2f, 0.25f, fadeA * 0.9f);
        font.DrawText(batch, key, x + 5, y + 3, 0.6f, 0.9f, 0.9f, 0.95f, fadeA);

        // Action label
        float actionX = x + keyW + 6;
        font.DrawText(batch, action, actionX, y + 3, 0.6f, 0.5f, 0.5f, 0.55f, fadeA * 0.8f);

        return actionX + font.MeasureWidth(action, 0.6f);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static (float X, float Y, float Tile, float Gap, float Pitch,
        float Bottom, float Height, int Columns) GetGridLayout(int sw, int sh, int totalItems)
    {
        float sidePad = Math.Clamp(sw * 0.055f, 52f, 92f);
        float top = TaikoGame.GlobalTopBarHeight + 88f;
        float bottom = sh - 86f;
        float gap = 16f;
        float available = MathF.Max(260f, sw - sidePad * 2f);
        int columns = Math.Clamp((int)MathF.Floor((available + gap) / (170f + gap)), 2, 8);
        float tile = MathF.Floor((available - gap * (columns - 1)) / columns);
        tile = Math.Clamp(tile, 124f, 184f);

        float totalW = tile * columns + gap * (columns - 1);
        float x = (sw - totalW) * 0.5f;
        float pitch = tile + gap;
        float height = MathF.Max(tile, bottom - top);

        return (x, top, tile, gap, pitch, bottom, height, columns);
    }

    private static (float X, float Y, float Width, float Height, float Gap)
        GetDifficultyChipLayout(int sw, int count, float y)
    {
        count = Math.Max(1, count);
        float gap = count > 5 ? 8f : 12f;
        float maxTotal = MathF.Max(260f, MathF.Min(900f, sw - 280f));
        float width = (maxTotal - gap * (count - 1)) / count;
        width = Math.Clamp(width, 96f, 156f);

        float total = width * count + gap * (count - 1);
        if (total > sw - 160f)
        {
            maxTotal = MathF.Max(220f, sw - 160f);
            width = MathF.Max(76f, (maxTotal - gap * (count - 1)) / count);
            total = width * count + gap * (count - 1);
        }

        return ((sw - total) * 0.5f, y, width, 54f, gap);
    }

    private static string GetDifficultyLabel(float od)
        => GetDifficultyStyle(od).Label;

    private static (string Label, float[] Color) GetDifficultyStyle(float od)
    {
        if (od <= 3.5f)
            return ("Easy", new[] { 0.28f, 0.88f, 0.42f, 1f });
        if (od <= 5.5f)
            return ("Medium", new[] { 1.00f, 0.56f, 0.16f, 1f });
        if (od <= 7.5f)
            return ("Hard", new[] { 1.00f, 0.25f, 0.24f, 1f });
        if (od <= 9.0f)
            return ("Logic?!", new[] { 0.68f, 0.34f, 1.00f, 1f });
        return ("NO LOGIC", new[] { 0.90f, 0.84f, 1.00f, 1f });
    }

    /// <summary>Get a color based on the custom difficulty tier.</summary>
    private static float[] GetOdColor(float od)
        => GetDifficultyStyle(od).Color;

    /// <summary>Truncate text to fit within maxWidth, appending "..".</summary>
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

    private static float Lerp(float a, float b, float t)
        => a + (b - a) * MathF.Min(1f, t);

    private static float EaseOutCubic(float t)
    {
        float t1 = 1f - t;
        return 1f - t1 * t1 * t1;
    }

    /// <summary>Get alpha for staggered row reveal. Each row delays slightly.</summary>
    private static float GetStaggeredAlpha(float masterT, int rowIndex)
    {
        float delay = rowIndex * 0.12f;
        float t = MathF.Max(0f, (masterT - delay) / 0.5f);
        return MathF.Min(1f, t);
    }

    /// <summary>Draw text that wraps to multiple lines if it exceeds maxWidth. Returns the Y after the last line.</summary>
    private static float DrawWrappedText(SpriteBatch batch, Engine.Text.BitmapFont font,
        string text, float x, float y, float maxWidth, float scale,
        float r, float g, float b, float a, bool shadow = false)
    {
        if (font.MeasureWidth(text, scale) <= maxWidth)
        {
            if (shadow)
                font.DrawTextShadow(batch, text, x, y, scale, r, g, b, a, 2f);
            else
                font.DrawText(batch, text, x, y, scale, r, g, b, a);
            return y + font.MeasureHeight(scale);
        }

        // Word wrap
        float lineH = font.MeasureHeight(scale) + 2f;
        string[] words = text.Split(' ');
        string line = "";

        foreach (string word in words)
        {
            string test = line.Length == 0 ? word : line + " " + word;
            if (font.MeasureWidth(test, scale) > maxWidth && line.Length > 0)
            {
                if (shadow)
                    font.DrawTextShadow(batch, line, x, y, scale, r, g, b, a, 2f);
                else
                    font.DrawText(batch, line, x, y, scale, r, g, b, a);
                y += lineH;
                line = word;
            }
            else
            {
                line = test;
            }
        }

        if (line.Length > 0)
        {
            // Truncate last line if still too long
            if (font.MeasureWidth(line, scale) > maxWidth)
                line = TruncateToFit(font, line, scale, maxWidth);
            if (shadow)
                font.DrawTextShadow(batch, line, x, y, scale, r, g, b, a, 2f);
            else
                font.DrawText(batch, line, x, y, scale, r, g, b, a);
            y += lineH;
        }

        return y;
    }

    /// <summary>Get a friendly source label from a file path (e.g. "osu! lazer" instead of a hash).</summary>
    private static string GetFriendlySource(string filePath)
    {
        // Lazer hash store paths contain /files/ with hash-named files
        if (filePath.Contains("/files/") || filePath.Contains("\\files\\"))
            return "osu! lazer";

        // osu! stable Songs folder
        if (filePath.Contains("/Songs/") || filePath.Contains("\\Songs\\"))
        {
            // Try to extract the folder name (e.g. "12345 Artist - Title")
            string? dir = Path.GetDirectoryName(filePath);
            if (dir != null)
                return Path.GetFileName(dir);
        }

        return Path.GetFileName(filePath);
    }

    public override void OnEscape()
    {
        // If search is active, Escape already handled in Update.
        // Otherwise, go back to main menu.
        if (_searchActive) return;
        _background.Unload();
        StopPreview();
        Game.GoToMainMenu();
    }

    public override void Dispose()
    {
        StopPreview();
        _background?.Dispose();
        _uiCircle?.Dispose();
    }
}
