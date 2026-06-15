using TaikoNova.Engine;
using TaikoNova.Game;

var launchOptions = GameLaunchOptions.FromArgs(args);

using var engine = new GameEngine(1600, 900, "TaikoNova", launchOptions);
engine.Run();
