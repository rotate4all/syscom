namespace Syscom.Server.Sensors;

/// <summary>Stands in for a source that could not start, so its error shows up in the component health view.</summary>
public sealed class FailedSensorSource : ISensorSource
{
    private readonly string _error;

    public FailedSensorSource(string name, string error)
    {
        Name = name;
        _error = error;
    }

    public string Name { get; }
    public IReadOnlyList<SensorInfo> Catalog => Array.Empty<SensorInfo>();
    public bool Update(Dictionary<string, float> values) => throw new InvalidOperationException(_error);
    public void Dispose() { }
}
