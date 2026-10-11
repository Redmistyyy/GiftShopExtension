using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Eft.Game;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Utils;
using System.Reflection;

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

[Injectable(TypePriority = OnLoadOrder.Preload + 1)]
public class GiftShopExtensionMain(
    ShopTable shopTable,
    ISptLogger<GiftShopExtensionMain> logger,
    JsonUtil jsonUtil,
    ImageRouter imageRouter
) : IOnLoad
{
    private const string ImagesFolderName = "Images";

    private const string ImageRoutePrefix = "/files/giftshopextension/";

    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        CustomImageRouter();
        await LoadAllContentsAsync(cancellationToken);
    }

    private async Task<T> LoadGiftShopContentAsync<T>(string fileName, CancellationToken ct)
        where T : class
    {
        var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var path = Path.Combine(dir, "GiftContent", fileName);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"[GiftShopExtension] Missing content file: {fileName}", path);
        }

        return await jsonUtil.DeserializeFromFileAsync<T>(path, ct)
                ?? throw new InvalidOperationException($"[GiftShopExtension] Failed to deserialize {fileName}");
    }

    private async Task LoadAllContentsAsync(CancellationToken cancellationToken)
    {
        var content = shopTable.Content;

        var menu = await LoadGiftShopContentAsync<List<ShopMenuItem>>("menu.json", cancellationToken);
        var pages = await LoadGiftShopContentAsync<List<ShopPage>>("pages.json", cancellationToken);
        var offers = await LoadGiftShopContentAsync<List<ShopOffer>>("offers.json", cancellationToken);
        var prices = await LoadGiftShopContentAsync<List<ShopPrice>>("prices.json", cancellationToken);
        var locale = await LoadGiftShopContentAsync<Dictionary<string, Dictionary<string, string>>>("locale.json", cancellationToken);

        content.Menu.AddRange(menu);
        content.Pages.AddRange(pages);
        content.Offers.AddRange(offers);
        content.Prices.AddRange(prices);

        foreach (var (lang, entries) in locale)
        {
            if (!content.Locale.TryGetValue(lang, out var target))
            {
                content.Locale[lang] = target = [];
            }

            foreach (var (key, value) in entries)
            {
                target[key] = value;
            }
        }

        logger.Success($"[GiftShopExtension] Merged {offers.Count} offers", null);
    }

    private void CustomImageRouter()
    {
        var dir = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, ImagesFolderName);

        if (!Directory.Exists(dir))
        {
            logger.Warning($"[GiftShopExtension] No '{ImagesFolderName}' folder at {dir}, custom image routes skipped.", null);
            return;
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(dir, file).Replace('\\', '/');
            var key = ImageRoutePrefix + Path.ChangeExtension(relative, null);

            imageRouter.AddRoute(key, file);
        }
    }
}