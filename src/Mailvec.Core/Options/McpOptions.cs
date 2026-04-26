namespace Mailvec.Core.Options;

public sealed class McpOptions
{
    public const string SectionName = "Mcp";

    public string BindAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 3333;
    public int SearchDefaultLimit { get; set; } = 20;
    public int SearchMaxLimit { get; set; } = 100;
}
