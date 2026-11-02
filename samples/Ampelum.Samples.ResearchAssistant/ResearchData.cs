namespace Ampelum.Samples.ResearchAssistant;

/// <summary>Where the sample documents live and the question the sample asks by default.</summary>
public static class ResearchData
{
    /// <summary>The markdown files are copied next to the executable by the project file.</summary>
    public static string KnowledgeDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "knowledge");

    public const string SampleQuestion =
        "How much electricity does a 6 kW rooftop solar array make in a year in Central Europe, "
        + "and how many home battery modules would it take to store one average day of that output?";
}
