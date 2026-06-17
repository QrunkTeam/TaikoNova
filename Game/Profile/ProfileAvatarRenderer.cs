using TaikoNova.Engine.GL;
using StbImageSharp;

namespace TaikoNova.Game.Profile;

public static class ProfileAvatarRenderer
{
    private static readonly Dictionary<string, Texture2D?> AvatarCache = new(StringComparer.OrdinalIgnoreCase);

    public static void Draw(SpriteBatch batch, Texture2D px, Texture2D circle,
        PlayerProfile? profile, float x, float y, float size, float alpha = 1f)
    {
        int seed = profile?.AvatarSeed ?? 0x4257;
        var accent = GetAccent(seed);

        if (TryDrawImageAvatar(batch, circle, profile, x, y, size, accent, alpha))
            return;

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

    private static bool TryDrawImageAvatar(SpriteBatch batch, Texture2D circle,
        PlayerProfile? profile, float x, float y, float size,
        (float R, float G, float B) accent, float alpha)
    {
        string? path = profile?.AvatarImagePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        if (!AvatarCache.TryGetValue(path, out var avatar))
        {
            avatar = LoadCircularAvatar(path);
            AvatarCache[path] = avatar;
        }

        if (avatar == null)
            return false;

        batch.Draw(circle, x, y, size, size,
            0.030f, 0.034f, 0.048f, 0.96f * alpha);
        batch.Draw(circle, x + size * 0.05f, y + size * 0.05f,
            size * 0.90f, size * 0.90f,
            accent.R * 0.20f, accent.G * 0.20f, accent.B * 0.20f, 0.72f * alpha);
        batch.Draw(avatar, x + size * 0.08f, y + size * 0.08f,
            size * 0.84f, size * 0.84f, 1f, 1f, 1f, alpha);
        return true;
    }

    private static Texture2D? LoadCircularAvatar(string path)
    {
        try
        {
            StbImage.stbi_set_flip_vertically_on_load(0);
            using var stream = File.OpenRead(path);
            var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            int srcSize = Math.Min(image.Width, image.Height);
            if (srcSize <= 0) return null;

            int srcX = (image.Width - srcSize) / 2;
            int srcY = (image.Height - srcSize) / 2;
            byte[] pixels = new byte[srcSize * srcSize * 4];
            float center = srcSize * 0.5f;
            float radius = center - 1.5f;

            for (int y = 0; y < srcSize; y++)
            {
                for (int x = 0; x < srcSize; x++)
                {
                    int si = ((srcY + y) * image.Width + srcX + x) * 4;
                    int di = (y * srcSize + x) * 4;
                    pixels[di] = image.Data[si];
                    pixels[di + 1] = image.Data[si + 1];
                    pixels[di + 2] = image.Data[si + 2];

                    float dx = x - center + 0.5f;
                    float dy = y - center + 0.5f;
                    float dist = MathF.Sqrt(dx * dx + dy * dy);
                    float mask = Math.Clamp(radius - dist + 1.0f, 0f, 1f);
                    pixels[di + 3] = (byte)(image.Data[si + 3] * mask);
                }
            }

            return Texture2D.FromPixels(srcSize, srcSize, pixels);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Profiles] Failed to load online avatar: {ex.Message}");
            return null;
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
