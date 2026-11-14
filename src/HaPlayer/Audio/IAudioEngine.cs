namespace HaPlayer.Audio;

/// <summary>
/// Воспроизведение одного файла. Интерфейс не зависит от Windows:
/// реализацию на NAudio позже можно заменить на другую для macOS и Linux.
/// </summary>
public interface IAudioEngine : IDisposable
{
    /// <summary>Открывает файл и готовит его к воспроизведению. Бросает исключение, если файл не читается.</summary>
    void Open(string path);

    void Play();
    void Pause();
    void Stop();

    bool IsPlaying { get; }
    TimeSpan Position { get; set; }
    TimeSpan Duration { get; }

    /// <summary>Громкость от 0 до 1.</summary>
    float Volume { get; set; }

    /// <summary>Настройки эквалайзера. Применяются сразу, в том числе к играющему треку.</summary>
    void SetEqualizer(EqualizerSettings settings);

    /// <summary>Устройства вывода звука, подключённые сейчас.</summary>
    IReadOnlyList<AudioDevice> GetDevices();

    /// <summary>Выбранное устройство. null — устройство Windows по умолчанию.</summary>
    string? DeviceId { get; }

    /// <summary>Переключает вывод на другое устройство, не прерывая трек.</summary>
    void SetDevice(string? id);

    /// <summary>Трек доиграл до конца. Может прийти не из UI-потока.</summary>
    event EventHandler? TrackEnded;

    /// <summary>Ошибка вывода звука во время воспроизведения. Может прийти не из UI-потока.</summary>
    event EventHandler<Exception>? PlaybackFailed;

    /// <summary>
    /// Заранее открывает следующий трек. Когда текущий кончится, движок перейдёт на него без паузы
    /// (или с плавным переходом) и сообщит об этом через AdvancedToNext. null — следующего нет.
    /// </summary>
    void SetNext(string? path);

    /// <summary>Движок сам перешёл на заранее открытый трек (путь к нему). Может прийти не из UI-потока.</summary>
    event EventHandler<string>? AdvancedToNext;

    /// <summary>Длительность плавного перехода между треками в секундах. 0 — переход без паузы и без наложения.</summary>
    double CrossfadeSeconds { get; set; }

    /// <summary>Частота отсчётов, которые отдаёт CopyRecentSamples.</summary>
    int SampleRate { get; }

    /// <summary>Последние звучавшие отсчёты (моно) для визуализатора.</summary>
    void CopyRecentSamples(float[] dest);
}
