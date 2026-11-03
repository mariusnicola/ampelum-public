namespace Ampelum.Samples.ComposedPrograms.Fake;

/// <summary>All scripted answers of the sample in one model. Rules are tried in the order added.</summary>
public static class ReleaseScript
{
    public static ScriptedModel Create() => new ScriptedModel()
        .Include(TuningRules.Create())
        .Include(ReviewRules.Create())
        .Include(StyleRules.Create())
        .Include(DraftingRules.Create());
}
