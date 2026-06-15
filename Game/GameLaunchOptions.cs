namespace TaikoNova.Game;

/// <summary>
/// Command-line options that affect a game session.
/// </summary>
public sealed class GameLaunchOptions
{
    public static GameLaunchOptions Default { get; } = new();

    public bool AutoPlay { get; init; }

    public static GameLaunchOptions FromArgs(string[] args)
    {
        bool autoPlay = false;

        foreach (string arg in args)
        {
            string normalized = arg.Trim().ToLowerInvariant();
            if (normalized is "--auto" or "--autoplay" or "-a" or "auto")
                autoPlay = true;
        }

        return new GameLaunchOptions
        {
            AutoPlay = autoPlay
        };
    }
}
