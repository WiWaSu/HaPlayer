using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlayer.Audio;
using HaPlayer.Library;
using HaPlayer.Ui;

namespace HaPlayer.ViewModels;

public sealed partial class PlayerViewModel : ObservableObject
{
    private readonly IAudioEngine _engine;
    private readonly Settings _settings;
    private readonly DispatcherTimer _timer;
    private readonly Random _rng = new();

    private IReadOnlyList<TrackItem> _queue = [];
    private int _index = -1;
    private int _errorStreak;
    private bool _updatingPosition;

    public PlayerViewModel(IAudioEngine engine, Settings settings)
    {
        _engine = engine;
        _settings = settings;
        _volume = settings.Volume;
        _shuffle = settings.Shuffle;
        _repeat = settings.Repeat;
        _engine.Volume = (float)(_volume / 100);

        _engine.TrackEnded += (_, _) => Dispatcher.UIThread.Post(() => Advance(auto: true));
        _engine.PlaybackFailed += (_, _) => Dispatcher.UIThread.Post(OnTrackFailed);

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => Tick());
    }

    /// <summary>Список, из которого сейчас играет очередь. Нужен страницам, чтобы кнопка «играть» ставила паузу.</summary>
    public IReadOnlyList<TrackItem> Queue => _queue;

    public int Index => _index;

    /// <summary>Откуда играет очередь: название альбома, плейлиста или «Все треки».</summary>
    [ObservableProperty] private string? _queueTitle;

    /// <summary>Что делать по сердечку у играющего трека. Задаёт MainViewModel, ведь лайки хранятся там.</summary>
    public Action<TrackItem>? LikeHandler { get; set; }

    [RelayCommand]
    public void ToggleLike()
    {
        if (CurrentTrack != null) LikeHandler?.Invoke(CurrentTrack);
    }

    [ObservableProperty] private TrackItem? _currentTrack;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private double _duration;
    [ObservableProperty] private double _volume;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private bool _shuffle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRepeatOn), nameof(IsRepeatOne), nameof(RepeatIcon), nameof(RepeatTip))]
    private RepeatMode _repeat;

    public bool HasTrack => CurrentTrack != null;
    public bool IsRepeatOn => Repeat != RepeatMode.Off;
    public bool IsRepeatOne => Repeat == RepeatMode.One;
    public Geometry RepeatIcon => Repeat == RepeatMode.One ? Icons.RepeatOne : Icons.Repeat;
    public string RepeatTip => Repeat switch
    {
        RepeatMode.All => "Повтор очереди (R)",
        RepeatMode.One => "Повтор трека (R)",
        _ => "Повтор выключен (R)",
    };
    public Geometry PlayIcon => IsPlaying ? Icons.Pause : Icons.Play;
    public Geometry VolumeIcon => IsMuted || Volume == 0 ? Icons.VolumeOff : Volume < 50 ? Icons.VolumeLow : Icons.Volume;

    private double _position;

    /// <summary>Позиция в секундах. Слайдер пишет сюда, и это перематывает трек.</summary>
    public double Position
    {
        get => _position;
        set
        {
            if (!SetProperty(ref _position, value)) return;
            OnPropertyChanged(nameof(PositionText));
            if (!_updatingPosition) _engine.Position = TimeSpan.FromSeconds(value);
        }
    }

    public string PositionText => Format.Time(_position);
    public string DurationText => Format.Time(Duration);

    /// <summary>Без трека длительность 0, и ползунок с максимумом 0 стоял бы справа. Подставляем 1.</summary>
    public double SliderMaximum => Duration > 0 ? Duration : 1;

    partial void OnDurationChanged(double value)
    {
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(SliderMaximum));
    }

    partial void OnCurrentTrackChanged(TrackItem? oldValue, TrackItem? newValue)
    {
        if (oldValue != null)
        {
            oldValue.IsCurrent = false;
            oldValue.IsPlaying = false;
        }
        if (newValue != null)
        {
            newValue.IsCurrent = true;
            newValue.IsPlaying = IsPlaying;
        }
        OnPropertyChanged(nameof(HasTrack));
    }

    partial void OnIsPlayingChanged(bool value)
    {
        if (CurrentTrack != null) CurrentTrack.IsPlaying = value;
        OnPropertyChanged(nameof(PlayIcon));
        if (value) _timer.Start();
        else _timer.Stop();
    }

    partial void OnVolumeChanged(double value)
    {
        IsMuted = false;
        _engine.Volume = (float)(value / 100);
        _settings.Volume = value;
        OnPropertyChanged(nameof(VolumeIcon));
    }

    partial void OnIsMutedChanged(bool value)
    {
        _engine.Volume = value ? 0 : (float)(Volume / 100);
        OnPropertyChanged(nameof(VolumeIcon));
    }

    partial void OnShuffleChanged(bool value)
    {
        _settings.Shuffle = value;
        // Перемешивание включили или выключили посреди списка: играющий трек остаётся, остальное перестраивается.
        if (_index >= 0 && _index < _queue.Count) BuildOrder(_index);
    }

    partial void OnRepeatChanged(RepeatMode value)
    {
        _settings.Repeat = value;
    }

    private void Tick()
    {
        _updatingPosition = true;
        Position = _engine.Position.TotalSeconds;
        _updatingPosition = false;

        // «Прослушан» после 30 секунд, у коротких треков после половины. Такой трек попадает в историю.
        if (!_listenedReported && CurrentTrack is { } t)
        {
            var threshold = Duration > 0 ? Math.Min(30, Duration / 2) : 30;
            if (Position >= threshold)
            {
                _listenedReported = true;
                Listened?.Invoke(t);
            }
        }
    }

    private bool _listenedReported;

    /// <summary>Трек прослушан достаточно, чтобы записать его в историю.</summary>
    public event Action<TrackItem>? Listened;

    private void SetPositionSilently(double seconds)
    {
        _updatingPosition = true;
        Position = seconds;
        _updatingPosition = false;
    }

    // ---------- Очередь ----------
    // Контекст — список, из которого играем (альбом, плейлист, «Все треки»), и порядок его обхода _order.
    // Без перемешивания порядок прямой, с перемешиванием — одна случайная перестановка без повторов,
    // поэтому «назад» возвращает к тому, что уже звучало.
    // Отдельно _userQueue: «Играть следующим» и «В очередь». Эти треки звучат первыми,
    // потом контекст продолжается с того же места.

    private List<int> _order = [];
    private int _pos = -1;
    private readonly List<TrackItem> _userQueue = [];
    private bool _playingFromUserQueue;

    /// <summary>Очередь изменилась: панели «Далее в очереди» нужно перерисоваться.</summary>
    public event Action? QueueChanged;

    public void PlayList(IReadOnlyList<TrackItem> list, int index, string? title = null)
    {
        if (index < 0 || index >= list.Count) return;
        _queue = list;
        QueueTitle = title ?? QueueTitle;
        OnPropertyChanged(nameof(Queue));
        _errorStreak = 0;
        BuildOrder(index);
        LoadAt(_pos);
    }

    /// <summary>Порядок обхода контекста. Трек start всегда первый, остальные по порядку или перемешаны.</summary>
    private void BuildOrder(int start)
    {
        _order = Enumerable.Range(0, _queue.Count).ToList();
        if (Shuffle && _queue.Count > 1 && start >= 0)
        {
            _order.RemoveAt(start);
            for (var i = _order.Count - 1; i > 0; i--)
            {
                var j = _rng.Next(i + 1);
                (_order[i], _order[j]) = (_order[j], _order[i]);
            }
            _order.Insert(0, start);
            _pos = 0;
        }
        else
        {
            _pos = start;
        }
        QueueChanged?.Invoke();
    }

    /// <summary>
    /// Возвращает очередь после перезапуска: трек открыт и стоит на паузе там, где его закрыли.
    /// Сам не играет: включать музыку при запуске без спроса плохая идея.
    /// </summary>
    public void Restore(IReadOnlyList<TrackItem> list, int index, double position, string? title)
    {
        if (index < 0 || index >= list.Count) return;
        _queue = list;
        QueueTitle = title;
        OnPropertyChanged(nameof(Queue));
        var track = list[index];
        try
        {
            _engine.Open(track.Path);
        }
        catch
        {
            return;
        }
        _index = index;
        BuildOrder(index);
        CurrentTrack = track;
        Duration = _engine.Duration.TotalSeconds > 0 ? _engine.Duration.TotalSeconds : track.Model.DurationSeconds;
        if (position > 0 && position < Duration - 1) _engine.Position = TimeSpan.FromSeconds(position);
        SetPositionSilently(_engine.Position.TotalSeconds);
        IsPlaying = false;
    }

    /// <summary>Позиция для сохранения сессии.</summary>
    public double CurrentPosition => _engine.Position.TotalSeconds;

    private void LoadAt(int pos)
    {
        _pos = pos;
        _index = _order[pos];
        _playingFromUserQueue = false;
        LoadTrack(_queue[_index]);
        QueueChanged?.Invoke();
    }

    private void LoadTrack(TrackItem track)
    {
        _listenedReported = false;
        CurrentTrack = track;
        SetPositionSilently(0);
        try
        {
            _engine.Open(track.Path);
        }
        catch
        {
            OnTrackFailed();
            return;
        }
        Duration = _engine.Duration.TotalSeconds > 0 ? _engine.Duration.TotalSeconds : track.Model.DurationSeconds;
        _engine.Play();
        IsPlaying = true;
    }

    private void OnTrackFailed()
    {
        // Файл удалён или формат не читается: идём дальше, но не по кругу, если битые все треки очереди.
        _errorStreak++;
        if (_errorStreak < _queue.Count + _userQueue.Count) Advance(auto: true);
        else Stop();
    }

    [RelayCommand]
    public void PlayPause()
    {
        if (CurrentTrack == null) return;
        if (_engine.IsPlaying)
        {
            _engine.Pause();
            IsPlaying = false;
        }
        else
        {
            _engine.Play();
            IsPlaying = true;
            _errorStreak = 0;
        }
    }

    public void Play()
    {
        if (CurrentTrack != null && !_engine.IsPlaying) PlayPause();
    }

    public void Pause()
    {
        if (_engine.IsPlaying) PlayPause();
    }

    [RelayCommand]
    public void Stop()
    {
        _engine.Stop();
        IsPlaying = false;
        SetPositionSilently(0);
    }

    [RelayCommand]
    public void Next() => Advance(auto: false);

    private void Advance(bool auto)
    {
        if (auto && Repeat == RepeatMode.One && _errorStreak == 0 && CurrentTrack != null)
        {
            _engine.Position = TimeSpan.Zero;
            _engine.Play();
            return;
        }

        // Сначала то, что пользователь поставил в очередь сам.
        if (_userQueue.Count > 0)
        {
            var t = _userQueue[0];
            _userQueue.RemoveAt(0);
            _playingFromUserQueue = true;
            LoadTrack(t);
            QueueChanged?.Invoke();
            return;
        }

        if (_order.Count == 0)
        {
            if (auto) Stop();
            return;
        }

        var next = _pos + 1;
        if (next >= _order.Count)
        {
            if (Repeat != RepeatMode.All)
            {
                if (auto) Stop();
                return;
            }
            // Круг пройден: при перемешивании новый круг в другом случайном порядке.
            BuildOrder(Shuffle ? _rng.Next(_queue.Count) : 0);
            next = _pos;
        }
        LoadAt(next);
    }

    [RelayCommand]
    public void Previous()
    {
        if (CurrentTrack == null) return;
        if (_engine.Position.TotalSeconds > 3)
        {
            _engine.Position = TimeSpan.Zero;
            SetPositionSilently(0);
            return;
        }
        // Трек из «В очередь» — назад к треку контекста, после которого его включили.
        if (_playingFromUserQueue && _pos >= 0 && _pos < _order.Count)
        {
            LoadAt(_pos);
            return;
        }
        if (_pos <= 0 && Repeat != RepeatMode.All)
        {
            _engine.Position = TimeSpan.Zero;
            SetPositionSilently(0);
            return;
        }
        LoadAt(_pos <= 0 ? _order.Count - 1 : _pos - 1);
    }

    /// <summary>«Играть следующим»: треки встают в начало пользовательской очереди.</summary>
    public void PlayNext(IReadOnlyList<TrackItem> tracks)
    {
        _userQueue.InsertRange(0, tracks);
        QueueChanged?.Invoke();
        if (CurrentTrack == null) Advance(auto: false);
    }

    /// <summary>«В очередь»: треки встают в конец пользовательской очереди.</summary>
    public void AddToQueue(IReadOnlyList<TrackItem> tracks)
    {
        _userQueue.AddRange(tracks);
        QueueChanged?.Invoke();
        if (CurrentTrack == null) Advance(auto: false);
    }

    /// <summary>Что прозвучит дальше: сначала поставленное в очередь, потом продолжение контекста.</summary>
    public List<(TrackItem Track, bool Queued, Action Play)> GetUpNext(int max)
    {
        var result = new List<(TrackItem, bool, Action)>();
        for (var i = 0; i < _userQueue.Count && result.Count < max; i++)
        {
            var t = _userQueue[i];
            var skip = i;
            result.Add((t, true, () =>
            {
                _userQueue.RemoveRange(0, Math.Min(skip + 1, _userQueue.Count));
                _playingFromUserQueue = true;
                LoadTrack(t);
                QueueChanged?.Invoke();
            }));
        }
        for (var p = _pos + 1; p < _order.Count && result.Count < max; p++)
        {
            var at = p;
            result.Add((_queue[_order[p]], false, () => LoadAt(at)));
        }
        return result;
    }

    public void SeekBy(double seconds)
    {
        if (CurrentTrack == null) return;
        Position = Math.Clamp(_engine.Position.TotalSeconds + seconds, 0, Duration);
    }

    public void ChangeVolume(double delta) => Volume = Math.Clamp(Volume + delta, 0, 100);

    [RelayCommand]
    public void ToggleShuffle() => Shuffle = !Shuffle;

    [RelayCommand]
    public void CycleRepeat() => Repeat = Repeat switch
    {
        RepeatMode.Off => RepeatMode.All,
        RepeatMode.All => RepeatMode.One,
        _ => RepeatMode.Off,
    };

    [RelayCommand]
    public void ToggleMute() => IsMuted = !IsMuted;
}
