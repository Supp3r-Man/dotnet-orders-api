namespace OrdersApi.Domain;

/// <summary>A business rule was violated. <see cref="Code"/> is machine readable and ends up in the problem document.</summary>
public sealed class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
