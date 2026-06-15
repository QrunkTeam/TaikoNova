using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using TaikoNova.Engine.Audio;
using TaikoNova.Engine.GL;
using TaikoNova.Engine.Input;
using TaikoNova.Engine.Text;
using TaikoNova.Game;

namespace TaikoNova.Engine;

/// <summary>
/// Core game engine — creates the OpenGL window, manages subsystems,
/// drives the update/render loop.
/// </summary>
public sealed class GameEngine : GameWindow
{
    // ── Subsystems ──
    public SpriteBatch SpriteBatch { get; private set; } = null!;
    public InputManager Input { get; } = new();
    public AudioManager Audio { get; } = new();
    public BitmapFont Font { get; private set; } = null!;

    // ── Shared textures ──
    public Texture2D PixelTex { get; private set; } = null!;
    public Texture2D CircleTex { get; private set; } = null!;
    public Texture2D RingTex { get; private set; } = null!;
    public Texture2D GlowTex { get; private set; } = null!;

    // ── Projection ──
    public Matrix4 Projection { get; private set; }

    /// <summary>Virtual (logical) resolution — all game code uses these.</summary>
    public int ScreenWidth { get; private set; }
    public int ScreenHeight { get; private set; }

    // Viewport offset & scale for mapping mouse coords → virtual coords
    private int _vpX, _vpY, _vpW, _vpH;
    // Framebuffer dimensions (physical pixels — may differ from client size on Retina/HiDPI)
    private int _fbW, _fbH;

    // ── Game ──
    private readonly GameLaunchOptions _launchOptions;
    private TaikoGame? _game;

    // ── Timing ──
    public double TotalTime { get; private set; }
    public double DeltaTime { get; private set; }

    public GameEngine(int width, int height, string title,
        GameLaunchOptions? launchOptions = null)
        : base(
            new GameWindowSettings { UpdateFrequency = 480 },
            new NativeWindowSettings
            {
                ClientSize = new Vector2i(width, height),
                Title = title,
                APIVersion = new Version(3, 3),
                Flags = ContextFlags.ForwardCompatible,
                NumberOfSamples = 4 // MSAA
            })
    {
        _launchOptions = launchOptions ?? GameLaunchOptions.Default;
        ScreenWidth = width;
        ScreenHeight = height;
        VSync = VSyncMode.Adaptive;
    }

    protected override void OnLoad()
    {
        base.OnLoad();

        // OpenGL state
        OpenTK.Graphics.OpenGL4.GL.ClearColor(0.08f, 0.08f, 0.14f, 1f);
        OpenTK.Graphics.OpenGL4.GL.Enable(EnableCap.Blend);
        OpenTK.Graphics.OpenGL4.GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        OpenTK.Graphics.OpenGL4.GL.Disable(EnableCap.DepthTest);

        // Subsystems
        SpriteBatch = new SpriteBatch();
        Font = new BitmapFont();

        // Generate pixel-art textures (small + nearest-neighbor = chunky)
        PixelTex = Texture2D.CreateWhitePixel();
        CircleTex = Texture2D.CreatePixelCircle(16);
        RingTex = Texture2D.CreatePixelRing(16, 2);
        GlowTex = Texture2D.CreatePixelCircle(12); // reuse circle for flash

        // Projection
        UpdateProjection();

        // Create game
        _game = new TaikoGame(this, _launchOptions);

        // Wire up input events
        MouseWheel += e => Input.OnMouseWheel(e.OffsetY);
        TextInput += e => Input.OnTextInput((char)e.Unicode);
        FileDrop += e => _game?.OnFileDrop(e.FileNames);

        Console.WriteLine("[Engine] TaikoNova initialized");
        Console.WriteLine($"[Engine] OpenGL {OpenTK.Graphics.OpenGL4.GL.GetString(StringName.Version)}");
        Console.WriteLine($"[Engine] Renderer: {OpenTK.Graphics.OpenGL4.GL.GetString(StringName.Renderer)}");
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        // ScreenWidth / ScreenHeight stay at virtual resolution (1600×900)
        // Use actual framebuffer size for viewport (handles Retina/HiDPI)
        UpdateProjection();
    }

    protected override void OnUpdateFrame(FrameEventArgs args)
    {
        base.OnUpdateFrame(args);

        DeltaTime = args.Time;
        TotalTime += args.Time;

        // Pass viewport info + content scale for mouse remapping
        float scaleX = (float)_fbW / ClientSize.X;
        float scaleY = (float)_fbH / ClientSize.Y;
        Input.Update(KeyboardState, MouseState, _vpX, _vpY, _vpW, _vpH, ScreenWidth, ScreenHeight, scaleX, scaleY);

        // Global hotkeys
        if (Input.IsPressed(Keys.Escape))
            _game?.OnEscape();

        if (Input.IsPressed(Keys.F11))
        {
            WindowState = WindowState == WindowState.Fullscreen
                ? WindowState.Normal
                : WindowState.Fullscreen;
        }

        _game?.Update(args.Time);
    }

    // ── FPS display ──
    private double _fpsTimer;
    private int _fpsCount;
    private int _fpsDisplay;

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);

        // Clear the entire framebuffer (including letterbox bars)
        OpenTK.Graphics.OpenGL4.GL.Viewport(0, 0, _fbW, _fbH);
        OpenTK.Graphics.OpenGL4.GL.Clear(ClearBufferMask.ColorBufferBit);

        // Set viewport to the letterboxed game area
        OpenTK.Graphics.OpenGL4.GL.Viewport(_vpX, _vpY, _vpW, _vpH);

        _game?.Render(args.Time);

        // FPS counter
        _fpsCount++;
        _fpsTimer += args.Time;
        if (_fpsTimer >= 1.0)
        {
            _fpsDisplay = _fpsCount;
            _fpsCount = 0;
            _fpsTimer -= 1.0;
        }
        if (_game?.Settings.ShowFps == true)
        {
            SpriteBatch.Begin(Projection);
            Font.DrawText(SpriteBatch, $"{_fpsDisplay} FPS",
                ScreenWidth - 100, ScreenHeight - 22, 0.55f,
                0.5f, 0.9f, 0.5f, 0.8f);
            SpriteBatch.End();
        }

        SwapBuffers();
    }

    protected override void OnUnload()
    {
        _game?.Dispose();
        SpriteBatch.Dispose();
        Font.Dispose();
        Audio.Dispose();
        PixelTex.Dispose();
        CircleTex.Dispose();
        RingTex.Dispose();
        GlowTex.Dispose();

        base.OnUnload();
    }

    private void UpdateProjection()
    {
        // Use the actual framebuffer size (handles Retina/HiDPI where fb != client size)
        var fb = FramebufferSize;
        _fbW = fb.X;
        _fbH = fb.Y;

        // Always fill the entire framebuffer — no letterboxing
        _vpX = 0;
        _vpY = 0;
        _vpW = _fbW;
        _vpH = _fbH;

        OpenTK.Graphics.OpenGL4.GL.Viewport(_vpX, _vpY, _vpW, _vpH);

        // Orthographic in virtual coordinates: top-left origin
        // All game code still uses ScreenWidth×ScreenHeight (1600×900)
        Projection = Matrix4.CreateOrthographicOffCenter(0, ScreenWidth, ScreenHeight, 0, -1, 1);
    }
}
