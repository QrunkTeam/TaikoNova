using ManagedBass;

namespace TaikoNova.Engine.Audio;

/// <summary>
/// Cross-platform audio playback using BASS (via ManagedBass).
/// BASS handles decoding (MP3, OGG, WAV, AIFF, etc.) and output natively on
/// macOS, Windows, and Linux — no separate decoder or output layer needed.
/// </summary>
public sealed class AudioManager : IDisposable
{
    private const int AmbientSampleRate = 44100;

    private int _musicStream;
    private int _ambientStream;
    private bool _bassReady;
    private float _musicVolume = 1.0f;
    private float _ambientVolume = 0.12f;
    private float _musicLevel;
    private double _ambientPhaseA;
    private double _ambientPhaseB;
    private double _ambientPhaseC;
    private double _ambientLfoPhase;
    private double _ambientTexturePhase;
    private StreamProcedure? _ambientProcedure;

    /// <summary>Current music playback position in milliseconds.</summary>
    public double MusicPosition
    {
        get
        {
            if (_musicStream == 0) return 0;
            long pos = Bass.ChannelGetPosition(_musicStream);
            return Bass.ChannelBytes2Seconds(_musicStream, pos) * 1000.0;
        }
    }

    /// <summary>Total music duration in milliseconds.</summary>
    public double MusicDuration
    {
        get
        {
            if (_musicStream == 0) return 0;
            long len = Bass.ChannelGetLength(_musicStream);
            return Bass.ChannelBytes2Seconds(_musicStream, len) * 1000.0;
        }
    }

    /// <summary>Whether music is currently playing.</summary>
    public bool IsPlaying
    {
        get
        {
            if (_musicStream == 0) return false;
            return Bass.ChannelIsActive(_musicStream) == PlaybackState.Playing;
        }
    }

    /// <summary>Whether music has been loaded.</summary>
    public bool IsMusicLoaded => _musicStream != 0;

    /// <summary>Smoothed peak amplitude for the current music stream, 0.0 to 1.0.</summary>
    public float MusicLevel => _musicLevel;

    public AudioManager()
    {
        InitBass();
    }

    private void InitBass()
    {
        try
        {
            // Auto-download the native BASS library if not present
            if (!BassNativeLoader.EnsureNativeLibrary())
            {
                Console.WriteLine("[Audio] Could not obtain BASS native library");
                return;
            }

            bool ok = Bass.Init(-1, 44100, DeviceInitFlags.Default);
            if (!ok)
            {
                var err = Bass.LastError;
                Console.WriteLine($"[Audio] Bass.Init failed: {err}");
                return;
            }

            _bassReady = true;
            Console.WriteLine("[Audio] BASS initialized successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Audio] BASS init failed: {ex.Message}");
            _bassReady = false;
        }
    }

    /// <summary>Load a music file (mp3, wav, ogg, aiff, etc.).</summary>
    public bool LoadMusic(string path)
    {
        StopMusic();

        if (!_bassReady)
        {
            Console.WriteLine("[Audio] BASS not available — cannot play audio");
            return false;
        }

        try
        {
            int stream = Bass.CreateStream(path, 0, 0, BassFlags.Default);
            if (stream == 0)
            {
                var err = Bass.LastError;
                Console.WriteLine($"[Audio] Failed to create stream for '{Path.GetFileName(path)}': {err}");
                return false;
            }

            _musicStream = stream;
            Bass.ChannelSetAttribute(_musicStream, ChannelAttribute.Volume, _musicVolume);

            Console.WriteLine($"[Audio] Loaded: {Path.GetFileName(path)}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Audio] Failed to load '{Path.GetFileName(path)}': {ex.Message}");
            return false;
        }
    }

    /// <summary>Start or resume music playback.</summary>
    public void PlayMusic()
    {
        if (!_bassReady || _musicStream == 0) return;
        Bass.ChannelPlay(_musicStream, false);
    }

    /// <summary>Pause music.</summary>
    public void PauseMusic()
    {
        if (!_bassReady || _musicStream == 0) return;
        Bass.ChannelPause(_musicStream);
    }

    /// <summary>Stop and release the current music stream.</summary>
    public void StopMusic()
    {
        if (_musicStream != 0)
        {
            Bass.ChannelStop(_musicStream);
            Bass.StreamFree(_musicStream);
            _musicStream = 0;
        }

        _musicLevel = 0f;
    }

    /// <summary>Seek to a position in ms.</summary>
    public void SeekMusic(double ms)
    {
        if (_musicStream == 0) return;
        double seconds = Math.Max(0, ms) / 1000.0;
        long bytePos = Bass.ChannelSeconds2Bytes(_musicStream, seconds);
        Bass.ChannelSetPosition(_musicStream, bytePos);
    }

    /// <summary>Set music volume (0.0 – 1.0).</summary>
    public void SetMusicVolume(float volume)
    {
        _musicVolume = Math.Clamp(volume, 0f, 1f);
        if (_musicStream != 0)
            Bass.ChannelSetAttribute(_musicStream, ChannelAttribute.Volume, _musicVolume);
    }

    /// <summary>
    /// Updates the cached music level for simple audio-reactive visuals.
    /// </summary>
    public float UpdateMusicLevel(double deltaTime)
    {
        float target = 0f;
        if (_bassReady && _musicStream != 0 && IsPlaying)
        {
            int level = Bass.ChannelGetLevel(_musicStream);
            if (level >= 0)
            {
                int left = level & 0xFFFF;
                int right = (level >> 16) & 0xFFFF;
                target = Math.Clamp(MathF.Max(left, right) / 32768f, 0f, 1f);
            }
        }

        float speed = target > _musicLevel ? 18f : 5f;
        _musicLevel += (target - _musicLevel) * MathF.Min(1f, (float)deltaTime * speed);
        return _musicLevel;
    }

    /// <summary>Start a quiet generated ambient pad underneath menu/profile music.</summary>
    public bool StartAmbientPad(float volume = 0.12f)
    {
        if (!_bassReady) return false;

        SetAmbientPadVolume(volume);
        if (_ambientStream != 0)
        {
            Bass.ChannelPlay(_ambientStream, false);
            return true;
        }

        try
        {
            _ambientProcedure ??= AmbientPadProcedure;
            _ambientStream = Bass.CreateStream(AmbientSampleRate, 2, BassFlags.Float,
                _ambientProcedure, IntPtr.Zero);
            if (_ambientStream == 0)
            {
                Console.WriteLine($"[Audio] Ambient pad stream failed: {Bass.LastError}");
                return false;
            }

            Bass.ChannelSetAttribute(_ambientStream, ChannelAttribute.Volume, _ambientVolume);
            Bass.ChannelPlay(_ambientStream, false);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Audio] Ambient pad failed: {ex.Message}");
            StopAmbientPad();
            return false;
        }
    }

    public void SetAmbientPadVolume(float volume)
    {
        _ambientVolume = Math.Clamp(volume, 0f, 1f);
        if (_ambientStream != 0)
            Bass.ChannelSetAttribute(_ambientStream, ChannelAttribute.Volume, _ambientVolume);
    }

    public void StopAmbientPad()
    {
        if (_ambientStream == 0) return;
        Bass.ChannelStop(_ambientStream);
        Bass.StreamFree(_ambientStream);
        _ambientStream = 0;
    }

    private unsafe int AmbientPadProcedure(int handle, IntPtr buffer, int length, IntPtr user)
    {
        float* samples = (float*)buffer;
        int sampleCount = length / sizeof(float);
        int frames = sampleCount / 2;

        const double Tau = Math.PI * 2.0;
        const double FreqA = 55.00;
        const double FreqB = 82.41;
        const double FreqC = 110.00;

        for (int frame = 0; frame < frames; frame++)
        {
            float lfo = 0.64f + MathF.Sin((float)_ambientLfoPhase) * 0.22f;
            float texture = MathF.Sin((float)_ambientTexturePhase) * 0.035f
                + MathF.Sin((float)(_ambientTexturePhase * 0.47)) * 0.025f;

            float tone =
                MathF.Sin((float)_ambientPhaseA) * 0.46f +
                MathF.Sin((float)_ambientPhaseB) * 0.30f +
                MathF.Sin((float)_ambientPhaseC) * 0.18f;
            float sample = (tone * lfo + texture) * 0.12f;
            float pan = MathF.Sin((float)(_ambientLfoPhase * 0.61)) * 0.12f;

            samples[frame * 2] = sample * (1f - pan);
            samples[frame * 2 + 1] = sample * (1f + pan);

            _ambientPhaseA += Tau * FreqA / AmbientSampleRate;
            _ambientPhaseB += Tau * FreqB / AmbientSampleRate;
            _ambientPhaseC += Tau * FreqC / AmbientSampleRate;
            _ambientLfoPhase += Tau * 0.055 / AmbientSampleRate;
            _ambientTexturePhase += Tau * 0.37 / AmbientSampleRate;

            if (_ambientPhaseA > Tau) _ambientPhaseA -= Tau;
            if (_ambientPhaseB > Tau) _ambientPhaseB -= Tau;
            if (_ambientPhaseC > Tau) _ambientPhaseC -= Tau;
            if (_ambientLfoPhase > Tau) _ambientLfoPhase -= Tau;
            if (_ambientTexturePhase > Tau) _ambientTexturePhase -= Tau;
        }

        return length;
    }

    /// <summary>
    /// Play a short sound effect (fire and forget).
    /// For hit sounds — don, kat, etc.
    /// </summary>
    public void PlaySfx(string path, float volume = 0.5f)
    {
        if (!_bassReady) return;

        try
        {
            // AutoFree flag makes BASS free the stream automatically when playback ends
            int stream = Bass.CreateStream(path, 0, 0, BassFlags.AutoFree);
            if (stream == 0) return;

            Bass.ChannelSetAttribute(stream, ChannelAttribute.Volume, volume);
            Bass.ChannelPlay(stream, false);
        }
        catch { /* Silently ignore SFX errors */ }
    }

    public void Dispose()
    {
        StopAmbientPad();
        StopMusic();
        if (_bassReady)
        {
            Bass.Free();
            _bassReady = false;
        }
    }
}
