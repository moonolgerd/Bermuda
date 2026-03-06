namespace Bermuda;

public record IpcRequest(string Id, string Command, object? Payload);
