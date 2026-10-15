using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Eft.Game;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services.Commerce;
using SPTarkov.Server.Core.Utils;
using System.Reflection;
using System.Text.Json;
using SPTarkov.Reflection.Patching;



namespace GiftShopExtension;

public record ModMetaData : IModMetadata
{
    /// <summary>
    /// Any string can be used for a modId, but it should ideally be unique and not easily duplicated.
    /// It is recommended (but not mandatory) to use the reverse domain name notation.
    /// </summary>
    public string ModGuid { get; init; } = "com.redmisty.giftshopextension";
    public string Name { get; init; } = "Expansion Hub Gifts Extension";
    public string Author { get; init; } = "Redmisty";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("0.6.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~5.0.0");
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
    public bool HasPrepatcher { get; init; } = false;
    public string License { get; init; } = "MIT";
}

internal static class GiftShopOfferItemType
{
    public const string GiftMethod = "GIFTMETHOD";
}

[Injectable(TypePriority = OnLoadOrder.Preload + 1)]
public sealed class GiftShopExtensionMain(
    ShopTable shopTable,
    ISptLogger<GiftShopExtensionMain> logger,
    GiftsConfig giftsConfig,
    GiftService giftService,
    JsonUtil jsonUtil,
    ImageRouter imageRouter,
    ProfileHelper profileHelper,
    SaveServer saveServer,
    PatchManager patchManager
) : IOnLoad
{
    private const string ImagesFolderName = "Images";

    private const string ImageRoutePrefix = "/files/giftshopextension/";
    private const string PatcherName = "com.redmisty.giftshopextension";

    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        CustomImageRouter();
        await MergeCustomGiftsAsync(cancellationToken);
        var giftByOffer = await LoadAllContentsAsync(cancellationToken);

        GiftShopMiddleware.Configure(
            giftByOffer,
            giftService,
            profileHelper,
            saveServer,
            logger
        );

        patchManager.PatcherName = PatcherName;
        patchManager.AutoPatch = true;
        patchManager.EnablePatches();

        logger.Success($"[GiftShopExtension] Linked {giftByOffer.Count} " +"shop offer(s) to native gifts", null);
        
    }

    private async Task<T> LoadGiftShopContentAsync<T>(string fileName, CancellationToken ct)
        where T : class
    {
        var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var path = Path.Combine(dir, "GiftContent", fileName);

        if (!File.Exists(path))
            throw new FileNotFoundException($"[GiftShopExtension] Missing content file: {fileName}", path);

        return await jsonUtil.DeserializeFromFileAsync<T>(path, ct)
                ?? throw new InvalidOperationException($"[GiftShopExtension] Failed to deserialize {fileName}");
    }

    private async Task<Dictionary<string, string>> LoadAllContentsAsync(CancellationToken cancellationToken)
    {
        var content = shopTable.Content;

        var menu = await LoadGiftShopContentAsync<List<ShopMenuItem>>("menu.json", cancellationToken);
        var pages = await LoadGiftShopContentAsync<List<ShopPage>>("pages.json", cancellationToken);
        var offers = await LoadGiftShopContentAsync<List<ShopOffer>>("offers.json", cancellationToken);
        var prices = await LoadGiftShopContentAsync<List<ShopPrice>>("prices.json", cancellationToken);
        var locale = await LoadGiftShopContentAsync<Dictionary<string, Dictionary<string, string>>>("locale.json", cancellationToken);

        var giftByOffer = BuildGiftOfferMap(offers);

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

        return giftByOffer;
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

    // Merge Custom Gift to Raw SPT Gift list.
    private async Task MergeCustomGiftsAsync(CancellationToken cancellationToken)
    {
        // Custom gifts file from SPT 4.x mod Tarkov Gift Shop (Not completed, made by me).
        var file = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "Gifts", "customGifts.jsonc");
        var rawGifts = giftsConfig.Gifts;

        if (!File.Exists(file))
            throw new FileNotFoundException($"[GiftShopExtension] Missing content file: customGifts.jsonc", file);


        var customGifts = await jsonUtil.DeserializeFromFileAsync<Dictionary<string, Gift>>(file, cancellationToken)
            ?? throw new InvalidOperationException($"[GiftShopExtension] Failed to deserialize {file}");

        foreach (var (giftName, gift) in customGifts)
        {
            rawGifts[giftName] = gift;
        }

        logger.Success($"[GiftShopExtension] Merged {customGifts.Count} custom gifts", null);
    }

    // Establish a mapping between offer IDs and gift IDs for quick lookup.
    private Dictionary<string, string> BuildGiftOfferMap(IEnumerable<ShopOffer> offers)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var offer in offers)
        {
            // Check if the offer is a gift offer.
            var giftEntry = offer.Items.FirstOrDefault(i => i.Type == GiftShopOfferItemType.GiftMethod);
            if (giftEntry is null) continue;

            var offerId = offer.Id.ToString();

            // giftName is a required field for gift offers, so we can safely read it.
            var giftName = ReadRequiredString(giftEntry.ExtensionData, "giftName", $"Offer {offerId}");

            // Check data if valid.
            if (offer.Items.Count != 1)
                throw new InvalidOperationException($"Gift offer {offerId} should only have one item.");
            if (offer.Countable)
                throw new InvalidOperationException($"Gift offer {offerId} must use countable=false");
            if (!giftsConfig.Gifts.ContainsKey(giftName))
                throw new InvalidOperationException($"Gift offer {offerId} points to unknown gift '{giftName}'");
            if (!result.TryAdd(offerId, giftName))
                throw new InvalidOperationException($"Duplicated gift offer id {offerId}");

        }

        return result;

    }

    private static string ReadRequiredString(IReadOnlyDictionary<string?, object?>? extensionData, string key, string context)
    {
        if (extensionData?.TryGetValue(key, out var raw) != true)
            throw new InvalidOperationException($"[GiftShopExtension] {context} missing required key '{key}'");

        var text = raw switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => throw new InvalidOperationException($"[GiftShopExtension] {context} key '{key}' is not a string.")
        };

        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException($"[GiftShopExtension] {context} key '{key}' is empty or whitespace.");

        return text;
    }
}