namespace Bermuda.Api.GraphQL;

public class IncidentRepository
{
    private static readonly List<Incident> _data =
    [
        new("1", "25.0°N 71.0°W", "Vessel disappeared without distress signal",    new DateOnly(2024,  3, 12), IncidentStatus.Investigating, IncidentPriority.High,     2),
        new("2", "24.5°N 77.2°W", "Aircraft lost radar contact mid-flight",          new DateOnly(2024,  6,  4), IncidentStatus.Open,          IncidentPriority.Critical,  0),
        new("3", "26.1°N 72.8°W", "Compass malfunction reported by cargo ship",      new DateOnly(2024,  7, 19), IncidentStatus.Closed,        IncidentPriority.Low,       5),
        new("4", "25.7°N 75.3°W", "Unexplained time discrepancy logged by crew",     new DateOnly(2024,  9,  1), IncidentStatus.Investigating, IncidentPriority.Medium,    3),
        new("5", "23.9°N 76.5°W", "Unusual electromagnetic interference detected",   new DateOnly(2024, 10, 28), IncidentStatus.Open,          IncidentPriority.High,      7),
        new("6", "27.0°N 70.1°W", "Yacht found adrift, crew missing",               new DateOnly(2025,  1,  5), IncidentStatus.Investigating, IncidentPriority.Critical,  0),
        new("7", "24.0°N 73.4°W", "Pilot reported sudden instrument failure",        new DateOnly(2025,  2, 14), IncidentStatus.Closed,        IncidentPriority.High,      1),
        new("8", "26.8°N 74.7°W", "Sonar anomaly detected at 3,000m depth",         new DateOnly(2025,  4, 30), IncidentStatus.Open,          IncidentPriority.Medium,    4),
    ];

    public IEnumerable<Incident> GetAll() => _data;

    public Incident? GetById(string id) =>
        _data.FirstOrDefault(i => i.Id == id);
}
