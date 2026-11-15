using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace HaPlayer.Audio;

/// <summary>
/// Следит за устройствами вывода Windows. Когда устройство, на котором играем, пропадает
/// (выдернули наушники с отдельным выходом, отключился Bluetooth), сообщает об этом.
/// Колонки и наушники на одном выходе звуковой карты Windows видит одним устройством: такое отключение не заметить.
/// </summary>
internal sealed class DeviceWatcher : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator? _enumerator;

    /// <summary>Пропало устройство с этим id. Приходит из системного потока.</summary>
    public event Action<string>? DeviceGone;

    /// <summary>Сменилось устройство по умолчанию. Приходит из системного потока.</summary>
    public event Action? DefaultChanged;

    public DeviceWatcher()
    {
        try
        {
            _enumerator = new MMDeviceEnumerator();
            _enumerator.RegisterEndpointNotificationCallback(this);
        }
        catch
        {
            _enumerator = null;
        }
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        if (newState != DeviceState.Active) DeviceGone?.Invoke(deviceId);
    }

    public void OnDeviceRemoved(string deviceId) => DeviceGone?.Invoke(deviceId);

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia) DefaultChanged?.Invoke();
    }

    public void OnDeviceAdded(string pwstrDeviceId)
    {
    }

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
    }

    public void Dispose()
    {
        try
        {
            _enumerator?.UnregisterEndpointNotificationCallback(this);
            _enumerator?.Dispose();
        }
        catch
        {
            // система уже закрывает процесс
        }
    }
}
