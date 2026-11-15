using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlayer.Audio;
using HaPlayer.Library;
using HaPlayer.Ui;

namespace HaPlayer.ViewModels;

/// <summary>Границы очереди: вся очередь или только альбом, исполнитель, папка играющего трека.</summary>
public enum PlayScope
{
    All,
    Album,
    Artist,
    Folder,
}

/// <summary>Сменённая очередь: список, его название, трек и место, на котором её сменили.</summary>
public sealed record QueueSnapshot(IReadOnlyList<TrackItem> List, string? Title, TrackItem? Track, double Position);

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

        _engine.CrossfadeSeconds = settings.CrossfadeSeconds;
        _engine.TrackEnded += (_, _) => Dispatcher.UIThread.Post(() => Advance(auto: true));
        // Движок сам перешёл на заранее открытый трек: обновляем экран и очередь, файл уже играет.
        _engine.AdvancedToNext += (_, path) => Dispatcher.UIThread.Post(() => Advance(auto: true, enginePath: path));
        QueueChanged += PrepareNext;
        _engine.PlaybackFailed += (_, _) => Dispatcher.UIThread.Post(OnTrackFailed);
        _engine.DeviceLost += (_, _) => Dispatcher.UIThread.Post(OnDeviceLost);

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
    public Geometry RepeatIcon => Repeat switch
    {
        RepeatMode.One => Icons.RepeatOne,
        RepeatMode.Folder => Icons.Folder,
        _ => Icons.Repeat,
    };
    public string RepeatTip => Repeat switch
    {
        RepeatMode.All => L.T("Повтор очереди (R)"),
        RepeatMode.One => L.T("Повтор трека (R)"),
        RepeatMode.Folder => L.T("Повтор папки (R)"),
        _ => L.T("Повтор выключен (R)"),
    };

    /// <summary>Очередь идёт по кругу: повтор всей очереди или папки.</summary>
    private bool Loops => Repeat is RepeatMode.All or RepeatMode.Folder;
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
        ApplyVolume();
        _settings.Volume = value;
        OnPropertyChanged(nameof(VolumeIcon));
    }

    partial void OnIsMutedChanged(bool value)
    {
        ApplyVolume();
        OnPropertyChanged(nameof(VolumeIcon));
    }

    // Громкость движка = ползунок × затухание таймера сна × затухание паузы.
    private double _sleepFactor = 1;
    private double _pauseFactor = 1;

    private void ApplyVolume() =>
        _engine.Volume = IsMuted ? 0 : (float)(Volume / 100 * Math.Clamp(_sleepFactor, 0, 1) * Math.Clamp(_pauseFactor, 0, 1));

    // ---------- Плавная пауза ----------

    private DispatcherTimer? _fadeTimer;
    private double _fadeFrom;
    private double _fadeTo;
    private DateTime _fadeStart;
    private double _fadeMs;

    /// <summary>Затухание при паузе идёт: движок ещё звучит, но для интерфейса уже пауза.</summary>
    private bool FadingOut => _fadeTimer?.IsEnabled == true && _fadeTo == 0;

    private void StartFade(double to)
    {
        _fadeFrom = _pauseFactor;
        _fadeTo = to;
        _fadeStart = DateTime.UtcNow;
        _fadeMs = Math.Max(1, _settings.PauseFadeMs * Math.Abs(to - _fadeFrom));
        _fadeTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) => FadeTick());
        _fadeTimer.Start();
        FadeTick();
    }

    private void FadeTick()
    {
        var k = Math.Clamp((DateTime.UtcNow - _fadeStart).TotalMilliseconds / _fadeMs, 0, 1);
        _pauseFactor = _fadeFrom + (_fadeTo - _fadeFrom) * k;
        ApplyVolume();
        if (k < 1) return;
        _fadeTimer?.Stop();
        if (_fadeTo == 0)
        {
            _engine.Pause();
            _pauseFactor = 1;
            ApplyVolume();
        }
    }

    /// <summary>Затухание прерывается: трек сменили, остановили или перемотали из другой программы.</summary>
    private void CancelFade()
    {
        if (_fadeTimer?.IsEnabled != true) return;
        _fadeTimer.Stop();
        if (_fadeTo == 0) _engine.Pause();
        _pauseFactor = 1;
        ApplyVolume();
    }

    // ---------- Устройство пропало ----------

    /// <summary>Музыка встала на паузу, потому что отключились наушники или Bluetooth.</summary>
    public event Action? PausedByDevice;

    private void OnDeviceLost()
    {
        var keepPlaying = IsPlaying && !_settings.PauseOnDisconnect;
        var paused = IsPlaying && !keepPlaying;
        CancelFade();
        _engine.ReopenOutput(keepPlaying);
        if (paused)
        {
            IsPlaying = false;
            PausedByDevice?.Invoke();
        }
    }

    partial void OnShuffleChanged(bool value)
    {
        _settings.Shuffle = value;
        // Перемешивание включили или выключили посреди списка: играющий трек остаётся, остальное перестраивается.
        if (_index >= 0 && _index < _queue.Count) BuildOrder(_index);
        PrepareNext();
    }

    partial void OnRepeatChanged(RepeatMode value)
    {
        _settings.Repeat = value;
        // Повтор папки — это очередь из папки играющего трека, идущая по кругу.
        if (!_scopeChanging)
        {
            if (value == RepeatMode.Folder && Scope != PlayScope.Folder && !SetScope(PlayScope.Folder)) Repeat = RepeatMode.All;
            else if (value != RepeatMode.Folder && Scope == PlayScope.Folder) SetScope(PlayScope.All);
        }
        PrepareNext();
    }

    // ---------- Границы очереди: только альбом, исполнитель или папка ----------

    /// <summary>
    /// Откуда брать треки для «только альбом / исполнитель / папка». Задаёт MainViewModel:
    /// альбом и исполнитель берутся целиком из медиатеки, а не только из текущего списка.
    /// </summary>
    public Func<TrackItem, PlayScope, IReadOnlyList<TrackItem>>? ScopeSource { get; set; }

    [ObservableProperty] private PlayScope _scope;

    private IReadOnlyList<TrackItem>? _scopeBase;
    private string? _scopeBaseTitle;
    private bool _scopeChanging;

    /// <summary>
    /// Очередь не выходит за альбом, исполнителя или папку играющего трека, даже с перемешиванием и повтором.
    /// All возвращает очередь, из которой включали, с того же трека.
    /// </summary>
    public bool SetScope(PlayScope scope)
    {
        if (CurrentTrack is not { } current) return false;
        if (scope == Scope) return true;
        var baseList = _scopeBase ?? _queue;
        var baseTitle = _scopeBase != null ? _scopeBaseTitle : QueueTitle;
        IReadOnlyList<TrackItem> list;
        string? title;
        if (scope == PlayScope.All)
        {
            list = baseList;
            title = baseTitle;
            _scopeBase = null;
        }
        else
        {
            list = ScopeSource?.Invoke(current, scope) ?? [];
            if (!list.Contains(current)) return false;
            _scopeBase = baseList;
            _scopeBaseTitle = baseTitle;
            title = scope switch
            {
                PlayScope.Album => current.AlbumName,
                PlayScope.Artist => current.Artist,
                _ => System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(current.Path)) ?? current.AlbumName,
            };
        }

        _scopeChanging = true;
        Scope = scope;
        if (scope != PlayScope.Folder && Repeat == RepeatMode.Folder) Repeat = RepeatMode.All;
        _scopeChanging = false;

        _queue = list;
        QueueTitle = title;
        OnPropertyChanged(nameof(Queue));
        var at = -1;
        for (var i = 0; i < list.Count; i++)
            if (ReferenceEquals(list[i], current)) { at = i; break; }
        _index = Math.Max(0, at);
        _playingFromUserQueue = false;
        BuildOrder(_index);
        return true;
    }

    /// <summary>Новая очередь: прежние границы к ней не относятся.</summary>
    private void ResetScope()
    {
        _scopeBase = null;
        if (Scope == PlayScope.All) return;
        _scopeChanging = true;
        Scope = PlayScope.All;
        if (Repeat == RepeatMode.Folder) Repeat = RepeatMode.All;
        _scopeChanging = false;
    }

    // ---------- Прежние очереди ----------

    private readonly List<QueueSnapshot> _history = [];

    /// <summary>Очереди этой сессии, которые сменились другими, новые первыми. Не больше десяти.</summary>
    public IReadOnlyList<QueueSnapshot> History => _history;

    public event Action? HistoryChanged;

    private void RememberQueue(IReadOnlyList<TrackItem> next)
    {
        var list = _scopeBase ?? _queue;
        if (list.Count == 0 || CurrentTrack == null || ReferenceEquals(list, next)) return;
        _history.RemoveAll(h => ReferenceEquals(h.List, list));
        _history.Insert(0, new QueueSnapshot(list, _scopeBase != null ? _scopeBaseTitle : QueueTitle, CurrentTrack, _engine.Position.TotalSeconds));
        if (_history.Count > 10) _history.RemoveRange(10, _history.Count - 10);
        HistoryChanged?.Invoke();
    }

    /// <summary>Вернуть прежнюю очередь: тот трек, на котором её сменили, с того же места.</summary>
    public void RestoreQueue(QueueSnapshot snapshot)
    {
        var at = 0;
        for (var i = 0; i < snapshot.List.Count; i++)
            if (ReferenceEquals(snapshot.List[i], snapshot.Track)) { at = i; break; }
        _history.Remove(snapshot);
        PlayList(snapshot.List, at, snapshot.Title);
        if (CurrentTrack == snapshot.Track && snapshot.Position > 3 && snapshot.Position < Duration - 3)
        {
            _engine.Position = TimeSpan.FromSeconds(snapshot.Position);
            SetPositionSilently(snapshot.Position);
        }
        HistoryChanged?.Invoke();
    }

    /// <summary>Таймер сна остановил музыку «после этого трека».</summary>
    public event Action? StoppedBySleepTimer;

    /// <summary>Длительность плавного перехода, секунды. 0 — без паузы и без наложения.</summary>
    public double CrossfadeSeconds
    {
        get => _engine.CrossfadeSeconds;
        set
        {
            _engine.CrossfadeSeconds = value;
            _settings.CrossfadeSeconds = value;
        }
    }

    /// <summary>Таймер сна «после этого трека»: когда трек кончится, музыка остановится.</summary>
    public bool StopAfterCurrent
    {
        get => _stopAfter == 1;
        set => StopAfterTracks = value ? 1 : 0;
    }

    /// <summary>Множитель громкости для плавного затухания таймера сна: 1 — как обычно, 0 — тишина.</summary>
    public void SetFade(double factor)
    {
        _sleepFactor = factor;
        ApplyVolume();
    }

    /// <summary>Сколько треков ещё доиграть до остановки, считая текущий. 0 — не останавливаться.</summary>
    public int StopAfterTracks
    {
        get => _stopAfter;
        set
        {
            _stopAfter = Math.Max(0, value);
            PrepareNext();
            StopCounterChanged?.Invoke();
        }
    }

    private int _stopAfter;

    /// <summary>Счётчик «остановить через N треков» изменился.</summary>
    public event Action? StopCounterChanged;

    /// <summary>Ручной режим: трек доигрывает, следующий открывается на паузе.</summary>
    public bool ManualAdvance
    {
        get => _settings.ManualAdvance;
        set
        {
            _settings.ManualAdvance = value;
            PrepareNext();
        }
    }

    /// <summary>Что заиграет после текущего трека, если ничего не трогать.</summary>
    private TrackItem? PeekNext()
    {
        // Останавливаемся или ждём нажатия: заранее ничего не открываем, иначе движок перейдёт сам.
        if (CurrentTrack == null || _stopAfter == 1 || ManualAdvance) return null;
        if (Repeat == RepeatMode.One) return CurrentTrack;
        if (_userQueue.Count > 0) return _userQueue[0];
        var next = _pos + 1;
        if (next >= 0 && next < _order.Count) return _queue[_order[next]];
        // По кругу без перемешивания начинаем сначала. С перемешиванием новый круг случайный, его заранее не знаем.
        if (Loops && !Shuffle && _order.Count > 0) return _queue[_order[0]];
        return null;
    }

    /// <summary>Сообщает движку следующий трек, чтобы переход был без паузы.</summary>
    private void PrepareNext()
    {
        if (CurrentTrack == null) return;
        var next = PeekNext();
        if (next is { IsMissing: true } && ResolveMissing?.Invoke(next) is { } twin) next = twin;
        _engine.SetNext(next is { IsMissing: false } ? next.Path : null);
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
        RememberQueue(list);
        ResetScope();
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
            // Треки из чёрного списка в перемешивание не попадают. Если такой трек включили сами, он играет.
            _order = _order.Where(i => i == start || !_queue[i].IsBlacklisted).ToList();
            _order.Remove(start);
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
        ResetScope();
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
        if (Repeat == RepeatMode.Folder && !SetScope(PlayScope.Folder)) Repeat = RepeatMode.All;
    }

    /// <summary>Позиция для сохранения сессии.</summary>
    public double CurrentPosition => _engine.Position.TotalSeconds;

    private void LoadAt(int pos, string? enginePath = null)
    {
        _pos = pos;
        _index = _order[pos];
        _playingFromUserQueue = false;
        LoadTrack(_queue[_index], enginePath);
        QueueChanged?.Invoke();
    }

    /// <summary>
    /// Делает трек текущим. enginePath — файл, на который движок уже перешёл сам:
    /// если это тот же трек, открывать его заново не нужно, он уже звучит.
    /// </summary>
    /// <summary>Для пропавшего файла возвращает живую копию той же песни или null. Задаёт MainViewModel.</summary>
    public Func<TrackItem, TrackItem?>? ResolveMissing { get; set; }

    private void LoadTrack(TrackItem track, string? enginePath = null)
    {
        if (enginePath == null && ResolveMissing?.Invoke(track) is { } twin) track = twin;
        CancelFade();
        _listenedReported = false;
        CurrentTrack = track;
        if (enginePath != null && string.Equals(track.Path, enginePath, StringComparison.OrdinalIgnoreCase))
        {
            Duration = _engine.Duration.TotalSeconds > 0 ? _engine.Duration.TotalSeconds : track.Model.DurationSeconds;
            SetPositionSilently(_engine.Position.TotalSeconds);
            IsPlaying = true;
            _errorStreak = 0;
            return;
        }
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
        if (_openPaused)
        {
            IsPlaying = false;
            return;
        }
        _engine.Play();
        IsPlaying = true;
    }

    /// <summary>Ручной режим: следующий трек открывается, но не играет.</summary>
    private bool _openPaused;

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
        var fade = _settings.PauseFadeMs > 0;
        if (IsPlaying && _engine.IsPlaying)
        {
            IsPlaying = false;
            if (fade) StartFade(0);
            else _engine.Pause();
        }
        else
        {
            if (FadingOut)
            {
                // Нажали «играть», пока музыка затихала: нарастает обратно с того же уровня.
                StartFade(1);
            }
            else if (fade)
            {
                _pauseFactor = 0;
                ApplyVolume();
                _engine.Play();
                StartFade(1);
            }
            else _engine.Play();
            IsPlaying = true;
            _errorStreak = 0;
        }
    }

    public void Play()
    {
        if (CurrentTrack != null && !IsPlaying) PlayPause();
    }

    public void Pause()
    {
        if (IsPlaying) PlayPause();
    }

    [RelayCommand]
    public void Stop()
    {
        CancelFade();
        _engine.Stop();
        IsPlaying = false;
        SetPositionSilently(0);
    }

    [RelayCommand]
    public void Next() => Advance(auto: false);

    private void Advance(bool auto, string? enginePath = null)
    {
        // Ручной режим: трек кончился сам — следующий открываем на паузе, он заиграет по нажатию.
        if (auto && ManualAdvance && !_openPaused && Repeat != RepeatMode.One)
        {
            _openPaused = true;
            try
            {
                Advance(auto: true, enginePath);
            }
            finally
            {
                _openPaused = false;
            }
            return;
        }
        if (auto && _stopAfter > 1)
        {
            _stopAfter--;
            StopCounterChanged?.Invoke();
        }
        else if (auto && _stopAfter == 1)
        {
            _stopAfter = 0;
            StopCounterChanged?.Invoke();
            Stop();
            StoppedBySleepTimer?.Invoke();
            return;
        }
        if (auto && Repeat == RepeatMode.One && _errorStreak == 0 && CurrentTrack != null)
        {
            if (enginePath != null)
            {
                // Движок уже начал тот же трек заново.
                _listenedReported = false;
                SetPositionSilently(_engine.Position.TotalSeconds);
                PrepareNext();
                return;
            }
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
            LoadTrack(t, enginePath);
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
            if (!Loops)
            {
                if (auto) Stop();
                return;
            }
            // Круг пройден: при перемешивании новый круг в другом случайном порядке.
            BuildOrder(Shuffle ? _rng.Next(_queue.Count) : 0);
            next = _pos;
        }
        LoadAt(next, enginePath);
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
        if (_pos <= 0 && !Loops)
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

    /// <summary>Всё, что прозвучит после текущего трека: ручная очередь, потом остаток контекста.</summary>
    public List<TrackItem> Upcoming()
    {
        var list = new List<TrackItem>(_userQueue);
        for (var p = _pos + 1; p >= 0 && p < _order.Count; p++) list.Add(_queue[_order[p]]);
        return list;
    }

    /// <summary>
    /// Заменяет всё, что звучит после текущего трека. Список становится ручной очередью,
    /// а остаток контекста отбрасывается: так порядок после перетаскивания остаётся ровно таким, как на экране.
    /// </summary>
    private void SetUpcoming(List<TrackItem> list)
    {
        _userQueue.Clear();
        _userQueue.AddRange(list);
        if (_pos >= 0 && _pos < _order.Count - 1) _order.RemoveRange(_pos + 1, _order.Count - _pos - 1);
        QueueChanged?.Invoke();
    }

    /// <summary>Очистить очередь: после текущего трека ничего не играет.</summary>
    public void ClearUpcoming() => SetUpcoming([]);

    /// <summary>Перетаскивание внутри «Далее в очереди».</summary>
    public void MoveUpcoming(int from, int to)
    {
        var up = Upcoming();
        if (from < 0 || from >= up.Count || from == to) return;
        var t = up[from];
        up.RemoveAt(from);
        up.Insert(Math.Clamp(to, 0, up.Count), t);
        SetUpcoming(up);
    }

    /// <summary>Убрать один трек из «Далее в очереди».</summary>
    public void RemoveUpcoming(int index)
    {
        var up = Upcoming();
        if (index < 0 || index >= up.Count) return;
        up.RemoveAt(index);
        SetUpcoming(up);
    }

    /// <summary>
    /// Треки убраны из медиатеки: они пропадают из очереди и из контекста.
    /// Если среди них текущий, включается следующий, а если играть больше нечего, музыка останавливается.
    /// </summary>
    public void Forget(IReadOnlySet<TrackItem> gone)
    {
        if (gone.Count == 0) return;
        _userQueue.RemoveAll(gone.Contains);
        var currentGone = CurrentTrack != null && gone.Contains(CurrentTrack);

        if (_queue.Any(gone.Contains))
        {
            var map = new int[_queue.Count];
            var kept = new List<TrackItem>(_queue.Count);
            for (var i = 0; i < _queue.Count; i++)
            {
                if (gone.Contains(_queue[i])) { map[i] = -1; continue; }
                map[i] = kept.Count;
                kept.Add(_queue[i]);
            }
            // Сколько уцелевших записей порядка стоит до текущей: с этого места и продолжаем.
            var before = _order.Take(Math.Max(0, _pos)).Count(i => map[i] >= 0);
            var currentKept = _pos >= 0 && _pos < _order.Count && map[_order[_pos]] >= 0;
            _order = _order.Where(i => map[i] >= 0).Select(i => map[i]).ToList();
            _queue = kept;
            OnPropertyChanged(nameof(Queue));
            _pos = currentKept ? before : before - 1;
            _index = _pos >= 0 && _pos < _order.Count ? _order[_pos] : -1;
        }

        if (currentGone)
        {
            var wasPlaying = IsPlaying;
            if (_userQueue.Count > 0 || _pos + 1 < _order.Count)
            {
                Advance(auto: false);
                if (!wasPlaying) Pause();
            }
            else
            {
                _engine.Close();
                IsPlaying = false;
                CurrentTrack = null;
                Duration = 0;
                SetPositionSilently(0);
            }
        }
        QueueChanged?.Invoke();
        PrepareNext();
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
        RepeatMode.One when CanRepeatFolder => RepeatMode.Folder,
        _ => RepeatMode.Off,
    };

    /// <summary>Повтор папки предлагаем, только если в очереди треки из нескольких папок: иначе он не отличается от повтора очереди.</summary>
    private bool CanRepeatFolder => CurrentTrack != null &&
        (_scopeBase ?? _queue).Select(t => System.IO.Path.GetDirectoryName(t.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any();

    [RelayCommand]
    public void ToggleMute() => IsMuted = !IsMuted;
}
