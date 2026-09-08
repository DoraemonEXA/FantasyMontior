using LibreHardwareMonitor.Hardware;

namespace FantasyMontior.Core;

public sealed class LibreHardwareBackend : IHardwareBackend
{
    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true,
        IsMotherboardEnabled = true, IsStorageEnabled = true,
        IsNetworkEnabled = true, IsControllerEnabled = true
    };

    public void Open() => _computer.Open();
    public IReadOnlyList<IMonitorHardware> Hardware => _computer.Hardware.Select(h => (IMonitorHardware)new Node(h)).ToArray();
    public void Dispose() => _computer.Close();

    private sealed class Node(IHardware hardware) : IMonitorHardware
    {
        public string Id => hardware.Identifier.ToString();
        public string Name => hardware.Name;
        public string Kind => hardware.HardwareType.ToString();
        public IReadOnlyList<IMonitorHardware> Children => hardware.SubHardware.Select(h => (IMonitorHardware)new Node(h)).ToArray();
        public IReadOnlyList<RawSensor> Sensors => hardware.Sensors.Select(s => new RawSensor(
            s.Identifier.ToString(), s.Name, s.SensorType.ToString(), SensorUnits.For(s.SensorType.ToString()), s.Value)).ToArray();
        public void Update() => hardware.Update();
    }
}

public static class SensorUnits
{
    public static string For(string kind) => kind switch
    {
        "Voltage" => "V", "Current" => "A", "Clock" => "MHz", "Frequency" => "Hz",
        "Temperature" => "°C", "Load" or "Control" or "Level" or "Humidity" => "%",
        "Fan" => "RPM", "Flow" => "L/h", "Power" => "W", "Data" => "GB",
        "SmallData" => "MB", "Throughput" => "B/s", "TimeSpan" => "s", "Timing" => "ns",
        "Energy" => "mWh", "Noise" => "dBA", "Conductivity" => "µS/cm",
        "Factor" => "", _ => "Unknown"
    };
}
