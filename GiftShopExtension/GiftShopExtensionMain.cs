using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace GiftShopExtension;

public record ModMetaData : IModMetadata
{
    /// <summary>
    /// Any string can be used for a modId, but it should ideally be unique and not easily duplicated.
    /// It is recommended (but not mandatory) to use the reverse domain name notation.
    /// </summary>
    public string ModGuid { get; init; } = "com.redmisty.giftshopextension";
    public string Name { get; init; } = "Expansion Hub Gift Extension";
    public string Author { get; init; } = "Redmisty";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("0.1.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~5.0.0");
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
    public bool HasPrepatcher { get; init; } = false;
    public string License { get; init; } = "MIT";
}

[Injectable(TypePriority = OnLoadOrder.Preload)]
public sealed class ShopContentInjector(ShopTable shopTable) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}