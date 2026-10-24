namespace LinnworksDailySeed.Configuration;

public sealed class LinnworksOptions
{
    public const string SectionName = "Linnworks";

    public string ApplicationId { get; set; } = "";
    public string ApplicationSecret { get; set; } = "";
    public string PermanentToken { get; set; } = "";
}
