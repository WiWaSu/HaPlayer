namespace HaPlayer.Audio;

public enum DeviceKind
{
    Speakers,
    Headphones,
    Bluetooth,
    Monitor,
}

/// <summary>Устройство вывода звука: колонки, наушники, HDMI-монитор, Bluetooth.</summary>
public sealed record AudioDevice(string Id, string Name, bool IsDefault, DeviceKind Kind)
{
    /// <summary>Тип по названию из Windows. Точного признака у WASAPI нет, поэтому угадываем по словам.</summary>
    public static DeviceKind GuessKind(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("bluetooth") || n.Contains("hands-free") || n.Contains("airpods") || n.Contains("buds")) return DeviceKind.Bluetooth;
        if (n.Contains("наушник") || n.Contains("headphone") || n.Contains("headset") || n.Contains("гарнитур")) return DeviceKind.Headphones;
        if (n.Contains("hdmi") || n.Contains("displayport") || n.Contains("nvidia") || n.Contains("монитор") || n.Contains("display")) return DeviceKind.Monitor;
        return DeviceKind.Speakers;
    }
}
