namespace Bermuda.Api.GraphQL;

public class Query
{
    public IEnumerable<Incident> Incidents([Service] IncidentRepository repo) =>
        repo.GetAll();

    public Incident? Incident(string id, [Service] IncidentRepository repo) =>
        repo.GetById(id);
}
