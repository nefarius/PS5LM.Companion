namespace PS5LM.Companion.Core.Models.Planning;

public enum HubFitKind
{
    Fits,
    TooBig,
    Unknown,
    NotApplicable,
}

/// <summary>The fit verdict for one file in a repository.</summary>
/// <param name="Label">Chip text. Empty when there is nothing to show.</param>
/// <param name="Detail">The planner's line, or why the check could not decide.</param>
public sealed record HubFileFit(string Name, HubFitKind Kind, string Label, string Detail)
{
    public static HubFileFit Fits(string name, ModelPlan plan) =>
        new(name, HubFitKind.Fits, $"Fits - {plan.Ctx / 1024}k, {plan.KvType}", plan.Why);

    public static HubFileFit TooBig(string name, string detail) => new(name, HubFitKind.TooBig, "Too big", detail);

    public static HubFileFit Unknown(string name, string detail) =>
        new(name, HubFitKind.Unknown, "Unknown", string.IsNullOrWhiteSpace(detail) ? "The GGUF header could not be read." : detail);

    public static HubFileFit LaterPart(string name) =>
        new(name, HubFitKind.NotApplicable, string.Empty, "Part of a split model; the first part carries the verdict.");
}
