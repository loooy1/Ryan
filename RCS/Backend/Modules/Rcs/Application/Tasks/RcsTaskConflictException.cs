namespace RCSBackend.Modules.Rcs.Application.Tasks;

public sealed class RcsTaskConflictException(string message) : Exception(message);
