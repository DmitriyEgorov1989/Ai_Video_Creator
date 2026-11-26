using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VideoFactory.VideoProduction.Domain.Editing;
using VideoFactory.VideoProduction.Domain.Identifiers;

namespace VideoFactory.VideoProduction.Application.Planning;

/// <summary>Вычисляет детерминированную идентичность полного содержимого ревизии Edit Plan.</summary>
public static class EditPlanRevisionIdentity
{
    private static readonly RevisionId IdentityPlaceholder = new(new Guid("00000000-0000-0000-0000-000000000001"));

    /// <summary>Вычисляет детерминированный Revision ID из полного содержимого без циклической зависимости от самого ID.</summary>
    /// <param name="plan">Монтажный план; текущее значение Revision ID игнорируется.</param>
    /// <param name="presentation">Presentation decisions либо <see langword="null"/>.</param>
    /// <param name="audit">Аудит skills либо <see langword="null"/>.</param>
    /// <returns>Идентификатор, производный от полной идентичности ревизии.</returns>
    public static RevisionId CalculateRevisionId(
        EditPlan plan,
        ReelsPresentationPlan? presentation,
        EditPlanAudit? audit)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string canonical = SerializeIdentity(plan with { RevisionId = IdentityPlaceholder }, presentation, audit);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return new RevisionId(new Guid(hash[..16]));
    }

    /// <summary>Вычисляет SHA-256 полного skill audit для ссылки из manifest.</summary>
    /// <param name="audit">Аудит skills; не может быть <see langword="null"/>.</param>
    /// <returns>SHA-256 в шестнадцатеричном формате.</returns>
    public static string CalculateAuditHash(EditPlanAudit audit)
    {
        ArgumentNullException.ThrowIfNull(audit);
        string canonical = JsonSerializer.Serialize(new { auditSchemaVersion = 1, audit });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>Вычисляет SHA-256 timeline, presentation и skill audit.</summary>
    /// <param name="plan">Монтажный план; не может быть <see langword="null"/>.</param>
    /// <param name="presentation">Presentation decisions либо <see langword="null"/> для legacy-плана.</param>
    /// <param name="audit">Аудит skills либо <see langword="null"/> для legacy-плана.</param>
    /// <returns>SHA-256 в шестнадцатеричном формате.</returns>
    public static string CalculateHash(
        EditPlan plan,
        ReelsPresentationPlan? presentation,
        EditPlanAudit? audit)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string canonical = SerializeIdentity(plan, presentation, audit);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string SerializeIdentity(
        EditPlan plan,
        ReelsPresentationPlan? presentation,
        EditPlanAudit? audit) =>
        JsonSerializer.Serialize(new
        {
            identitySchemaVersion = 1,
            plan,
            presentation,
            audit
        });
}
