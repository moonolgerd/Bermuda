namespace Bermuda.Api.GraphQL;

public enum IncidentStatus { Open, Investigating, Closed }

public enum IncidentPriority { Low, Medium, High, Critical }

public record Incident(
    string Id,
    string Location,
    string Description,
    DateOnly Date,
    IncidentStatus Status,
    IncidentPriority Priority,
    int Witnesses
);
