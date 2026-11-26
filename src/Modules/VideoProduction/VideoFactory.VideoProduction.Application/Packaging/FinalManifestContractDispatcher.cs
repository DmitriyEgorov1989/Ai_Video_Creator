using System.Text.Json;
using VideoFactory.VideoProduction.Domain.Validation;

namespace VideoFactory.VideoProduction.Application.Packaging;

/// <summary>Результат выбора и структурной проверки версии final manifest.</summary>
/// <param name="Version">Версия документа либо <see langword="null"/>, если она не прочитана.</param>
/// <param name="Status">Результат dispatch.</param>
/// <param name="Issues">Ожидаемые ошибки контракта; пустой список означает принятый документ.</param>
public sealed record FinalManifestContractDispatch(
    int? Version,
    FinalManifestContractStatus Status,
    IReadOnlyList<ValidationIssue> Issues);

/// <summary>Состояние final manifest после dispatch.</summary>
public enum FinalManifestContractStatus
{
    /// <summary>Документ поддерживается и структурно корректен.</summary>
    Accepted,

    /// <summary>Документ поддерживаемой версии некорректен.</summary>
    Invalid,

    /// <summary>Версия документа не поддерживается.</summary>
    UnsupportedVersion
}

/// <summary>Проверяет versioned manifest без доступа к хранилищу артефактов.</summary>
public static class FinalManifestContractDispatcher
{
    /// <summary>Проверяет legacy V1 или доказательный Manifest V2.</summary>
    /// <param name="document">JSON manifest; должен быть объектом.</param>
    /// <returns>Результат проверки с диагностикой ожидаемых ошибок.</returns>
    public static FinalManifestContractDispatch Dispatch(JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object
            || !document.TryGetProperty("schemaVersion", out JsonElement version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out int schemaVersion))
        {
            return Invalid(null, "manifest.schema_version_required", "schemaVersion", "Manifest schemaVersion must be an integer.");
        }

        return schemaVersion switch
        {
            1 => new(1, FinalManifestContractStatus.Accepted, []),
            2 => ValidateV2(document),
            _ => new(schemaVersion, FinalManifestContractStatus.UnsupportedVersion,
                [new ValidationIssue("manifest.schema_version_unsupported", "schemaVersion", "Unsupported manifest schema version.")])
        };
    }

    private static FinalManifestContractDispatch ValidateV2(JsonElement document)
    {
        List<ValidationIssue> issues = [];
        foreach (string property in new[] { "jobId", "variantId", "planRevisionId", "planHash" })
            RequiredString(document, property, property, issues);

        if (!document.TryGetProperty("audit", out JsonElement audit) || audit.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new ValidationIssue("manifest.audit_required", "audit", "Manifest V2 requires an audit digest."));
        }
        else
        {
            if (!audit.TryGetProperty("schemaVersion", out JsonElement auditVersion)
                || auditVersion.ValueKind != JsonValueKind.Number
                || !auditVersion.TryGetInt32(out int parsedAuditVersion)
                || parsedAuditVersion != 1)
                issues.Add(new ValidationIssue("manifest.audit_invalid", "audit.schemaVersion", "Audit schema version 1 is required."));
            if (!audit.TryGetProperty("skillCount", out JsonElement count)
                || count.ValueKind != JsonValueKind.Number
                || !count.TryGetInt32(out int skillCount)
                || skillCount != 11)
                issues.Add(new ValidationIssue("manifest.audit_invalid", "audit.skillCount", "Audit must contain 11 editing skills."));
            RequiredString(audit, "sha256", "audit.sha256", issues);
        }

        if (!document.TryGetProperty("criticChain", out JsonElement chain)
            || chain.ValueKind != JsonValueKind.Array
            || chain.GetArrayLength() == 0)
        {
            issues.Add(new ValidationIssue("manifest.critic_chain_required", "criticChain", "Manifest V2 requires a non-empty critic chain."));
        }
        else
        {
            ValidateCriticChain(document, chain, issues);
        }

        foreach (string property in new[] { "render", "qa", "final" })
            ValidateArtifactReference(document, property, issues);

        return new(2, issues.Count == 0 ? FinalManifestContractStatus.Accepted : FinalManifestContractStatus.Invalid, issues);
    }

    private static void ValidateCriticChain(JsonElement document, JsonElement chain, List<ValidationIssue> issues)
    {
        int index = 0;
        JsonElement last = default;
        string? expectedSourceRevision = null;
        foreach (JsonElement attempt in chain.EnumerateArray())
        {
            last = attempt;
            string path = $"criticChain[{index}]";
            if (attempt.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new ValidationIssue("manifest.critic_attempt_invalid", path, "Critic attempt must be an object."));
                index++;
                continue;
            }
            if (!attempt.TryGetProperty("remountNumber", out JsonElement remount)
                || remount.ValueKind != JsonValueKind.Number
                || !remount.TryGetInt32(out int number) || number != index)
                issues.Add(new ValidationIssue("manifest.remount_sequence_invalid", $"{path}.remountNumber", "Remount numbers must be contiguous from zero."));
            foreach (string property in new[] { "sourceRevisionId", "sourcePlanHash", "status" })
                RequiredString(attempt, property, $"{path}.{property}", issues);
            string? sourceRevisionId = attempt.TryGetProperty("sourceRevisionId", out JsonElement sourceRevision)
                && sourceRevision.ValueKind == JsonValueKind.String
                    ? sourceRevision.GetString()
                    : null;
            if (index > 0 && sourceRevisionId != expectedSourceRevision)
                issues.Add(new ValidationIssue("manifest.remount_chain_broken", $"{path}.sourceRevisionId", "A remount correction must be the next attempt source revision."));
            if (!attempt.TryGetProperty("render", out JsonElement render) || render.ValueKind != JsonValueKind.Object)
                issues.Add(new ValidationIssue("manifest.render_required", $"{path}.render", "Every critic attempt requires its render artifact."));
            else
                ValidateArtifactReferenceValue(render, $"{path}.render", issues);
            if (!attempt.TryGetProperty("criticReport", out JsonElement report) || report.ValueKind != JsonValueKind.Object)
                issues.Add(new ValidationIssue("manifest.critic_report_required", $"{path}.criticReport", "Every critic attempt requires its report artifact."));
            else
                ValidateArtifactReferenceValue(report, $"{path}.criticReport", issues);
            string? status = attempt.TryGetProperty("status", out JsonElement statusElement)
                && statusElement.ValueKind == JsonValueKind.String
                    ? statusElement.GetString()
                    : null;
            if (status is not ("accepted" or "remount"))
                issues.Add(new ValidationIssue("manifest.critic_status_invalid", $"{path}.status", "Critic status must be accepted or remount."));
            if (!attempt.TryGetProperty("correctionRevisionId", out JsonElement correctionRevision))
            {
                issues.Add(new ValidationIssue("manifest.correction_revision_required", $"{path}.correctionRevisionId", "Correction revision must be present and null only for accepted attempts."));
                expectedSourceRevision = null;
            }
            else if (status == "remount")
            {
                if (correctionRevision.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(correctionRevision.GetString()))
                    issues.Add(new ValidationIssue("manifest.correction_revision_required", $"{path}.correctionRevisionId", "Remount attempt requires a correction revision."));
                expectedSourceRevision = correctionRevision.ValueKind == JsonValueKind.String ? correctionRevision.GetString() : null;
            }
            else
            {
                if (correctionRevision.ValueKind != JsonValueKind.Null)
                    issues.Add(new ValidationIssue("manifest.correction_revision_invalid", $"{path}.correctionRevisionId", "Accepted attempt cannot create a correction revision."));
                expectedSourceRevision = null;
            }
            if (status == "accepted" && index != chain.GetArrayLength() - 1)
                issues.Add(new ValidationIssue("manifest.critic_sequence_invalid", path, "Accepted attempt must be the final critic attempt."));
            index++;
        }

        if (last.ValueKind == JsonValueKind.Object)
        {
            string? status = last.TryGetProperty("status", out JsonElement value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            string? sourceRevision = last.TryGetProperty("sourceRevisionId", out JsonElement source)
                && source.ValueKind == JsonValueKind.String ? source.GetString() : null;
            string? approvedRevision = document.TryGetProperty("planRevisionId", out JsonElement approved)
                && approved.ValueKind == JsonValueKind.String ? approved.GetString() : null;
            if (status != "accepted" || sourceRevision != approvedRevision)
                issues.Add(new ValidationIssue("manifest.final_critic_mismatch", "criticChain", "The final critic attempt must accept the approved revision."));
        }
    }

    private static void ValidateArtifactReference(JsonElement document, string property, List<ValidationIssue> issues)
    {
        if (!document.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
            issues.Add(new ValidationIssue("manifest.artifact_required", property, "Artifact reference is required."));
        else
            ValidateArtifactReferenceValue(value, property, issues);
    }

    private static void ValidateArtifactReferenceValue(JsonElement value, string path, List<ValidationIssue> issues)
    {
        RequiredString(value, "artifactId", $"{path}.artifactId", issues);
        RequiredString(value, "sha256", $"{path}.sha256", issues);
        if (!value.TryGetProperty("sizeBytes", out JsonElement size)
            || size.ValueKind != JsonValueKind.Number
            || !size.TryGetInt64(out long bytes)
            || bytes <= 0)
            issues.Add(new ValidationIssue("manifest.artifact_size_invalid", $"{path}.sizeBytes", "Artifact size must be positive."));
    }

    private static void RequiredString(JsonElement parent, string property, string path, List<ValidationIssue> issues)
    {
        if (!parent.TryGetProperty(property, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            issues.Add(new ValidationIssue("manifest.required_string", path, "A non-empty string is required."));
    }

    private static FinalManifestContractDispatch Invalid(int? version, string code, string path, string message) =>
        new(version, FinalManifestContractStatus.Invalid, [new ValidationIssue(code, path, message)]);
}
