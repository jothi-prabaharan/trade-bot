namespace TradeBot.Domain.Common;

/// <summary>Column is stored AES-256-GCM encrypted.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class EncryptedAttribute : Attribute;

public interface IHasUpdatedAt
{
    DateTime UpdatedAt { get; set; }
}

public interface IAuditable : IHasUpdatedAt
{
    DateTime CreatedAt { get; set; }
}

public abstract class AuditableEntity : IAuditable
{
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
