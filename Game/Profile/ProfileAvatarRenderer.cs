using TaikoNova.Engine.GL;

namespace TaikoNova.Game.Profile;

public static class ProfileAvatarRenderer
{
    public static void Draw(SpriteBatch batch, Texture2D px, Texture2D circle,
        PlayerProfile? profile, float x, float y, float size, float alpha = 1f)
    {
        int seed = profile?.AvatarSeed ?? 0x4257;
        var accent = GetAccent(seed);

        batch.Draw(circle, x, y, size, size,
            0.030f, 0.034f, 0.048f, 0.92f * alpha);
        batch.Draw(circle, x + size * 0.09f, y + size * 0.09f,
            size * 0.82f, size * 0.82f,
            accent.R * 0.18f, accent.G * 0.18f, accent.B * 0.18f, 0.96f * alpha);

        float cell = size * 0.105f;
        float gap = size * 0.030f;
        float gridW = cell * 5f + gap * 4f;
        float startX = x + (size - gridW) * 0.5f;
        float startY = y + (size - gridW) * 0.5f;

        for (int row = 0; row < 5; row++)
        {
            for (int col = 0; col < 3; col++)
            {
                uint bit = Hash((uint)seed, row * 7 + col * 13);
                if ((bit & 1u) == 0u) continue;

                DrawCell(batch, px, startX, startY, cell, gap, row, col,
                    accent.R, accent.G, accent.B, alpha);
                if (col != 2)
                {
                    DrawCell(batch, px, startX, startY, cell, gap, row, 4 - col,
                        accent.R, accent.G, accent.B, alpha);
                }
            }
        }

        if (profile == null)
        {
            batch.Draw(px, x + size * 0.49f, y + size * 0.28f,
                size * 0.06f, size * 0.44f, 0.92f, 0.94f, 1f, 0.88f * alpha);
            batch.Draw(px, x + size * 0.30f, y + size * 0.47f,
                size * 0.44f, size * 0.06f, 0.92f, 0.94f, 1f, 0.88f * alpha);
        }
    }

    public static (float R, float G, float B) GetAccent(int seed)
    {
        uint h = Hash((uint)seed, 41);
        float r = 0.48f + ((h >> 0) & 0xFF) / 255f * 0.44f;
        float g = 0.24f + ((h >> 8) & 0xFF) / 255f * 0.48f;
        float b = 0.32f + ((h >> 16) & 0xFF) / 255f * 0.58f;
        return (MathF.Min(1f, r), MathF.Min(1f, g), MathF.Min(1f, b));
    }

    private static void DrawCell(SpriteBatch batch, Texture2D px,
        float startX, float startY, float cell, float gap, int row, int col,
        float r, float g, float b, float a)
    {
        float x = startX + col * (cell + gap);
        float y = startY + row * (cell + gap);
        batch.Draw(px, x, y, cell, cell, r, g, b, 0.92f * a);
    }

    private static uint Hash(uint seed, int salt)
    {
        uint x = seed + (uint)salt * 0x9E3779B9u;
        x ^= x >> 16;
        x *= 0x7FEB352Du;
        x ^= x >> 15;
        x *= 0x846CA68Bu;
        x ^= x >> 16;
        return x;
    }
}
