using SPTarkov.Common.Models.Logging;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Game;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services.Commerce;
using System.Reflection;
using HarmonyLib;

namespace GiftShopExtension;

// Save the reflection between Gift Shop and Raw Gift mailing methods.
// After purchase, the Gift Shop will call the Raw Gift method to send the gift to the recipient.
internal sealed class GiftShopMiddleware(
    IReadOnlyDictionary<string, string> giftByOffer,
    GiftService giftService,
    ProfileHelper profileHelper,
    SaveServer saveServer,
    ISptLogger<GiftShopExtensionMain> logger
)
{
    public static GiftShopMiddleware? Current { get; private set; }
    public static void Configure(
        IReadOnlyDictionary<string, string> giftByOffer,
        GiftService giftService,
        ProfileHelper profileHelper,
        SaveServer saveServer,
        ISptLogger<GiftShopExtensionMain> logger
    )
    {
        Current = new GiftShopMiddleware(
            giftByOffer,
            giftService,
            profileHelper,
            saveServer,
            logger
        );
    }

    // Judge whether the offer is a gift offer, and if so, whether the player has already received the gift (Only for SPT raw).
    public bool IsGiftOffer(string offerId) => giftByOffer.ContainsKey(offerId);
    public bool HasAlreadyReceivedGift(
        MongoId sessionId,
        string offerId
    )
    {
        if (!giftByOffer.TryGetValue(offerId, out var giftId)) return false;


        var gift = giftService.GetGiftById(giftId);
        if (gift is null) return true;

        return profileHelper.PlayerHasReceivedMaxNumberOfGift(sessionId, giftId, gift.MaxToSendPlayer ?? 1);
    }

    // Only after the shop purchase is completed, the gift will be sent to the recipient.
    public async Task<ShopPurchaseReceipt> DeliverGiftAfterPurchaseAsync(
        Task<ShopPurchaseReceipt> originalPurchase,
        MongoId sessionId,
        string offerId
    )
    {
        var receipt = await originalPurchase;

        if (receipt.Outcome != ShopPurchaseOutcome.Success || !giftByOffer.TryGetValue(offerId, out var giftId))
            return receipt;


        var sendGiftResult = await giftService.SendGiftToPlayerAsync(sessionId, giftId);
        if (sendGiftResult != GiftSentResult.SUCCESS)
        {
            logger.Warning($"[GiftShopExtension] Purchase {offerId} succeeded, " + $"but gift '{giftId}' could not be sent: {sendGiftResult}", null);
            return receipt;
        }

        // Giftserver will modify mail contents and receivedGifts.
        // Save the profile immediately to keep the gift record in sync with the mail contents.
        await saveServer.SaveProfileAsync(sessionId);
        logger.Success($"[GiftShopExtension] Delivered gift '{giftId}' " + $"for offer {offerId}", null);
        return receipt;
    }
}

/// <summary>
/// Raw shop purchase are not allowed 0 (free) purchase.
/// This patch only allows free purchase, but doesn't allow minus tarcoins purchase.
/// </summary>
public sealed class AllowFreePurchasePatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod() => AccessTools.Method(typeof(TarcoinStoreService), nameof(TarcoinStoreService.TrySpend));

    [PatchPrefix]
    private static bool Prefix(int cost, ref bool __result)
    {
        if (cost != 0) return true;

        __result = true;
        return false;
    }
}

/// <summary>
/// This patch deals with the raw gift system and shop purchase system, allowing shop to send gifts after purchase.
/// </summary>
public sealed class GiftPurchasePatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod() => AccessTools.Method(typeof(TarcoinStoreService), nameof(TarcoinStoreService.TryPurchaseAsync));

    [PatchPrefix]
    private static bool Prefix(string offerId, MongoId sessionId, ref Task<ShopPurchaseReceipt> __result)
    {
        var middleware = GiftShopMiddleware.Current;
        if (middleware is null || !middleware.IsGiftOffer(offerId) || !middleware.HasAlreadyReceivedGift(sessionId, offerId))
            return true;


        __result = Task.FromResult(new ShopPurchaseReceipt(ShopPurchaseOutcome.PurchaseLimitExceeded));

        return false;
    }

    [PatchPostfix]
    private static void Postfix(MongoId sessionId, string offerId, ref Task<ShopPurchaseReceipt> __result)
    {
        var middleware = GiftShopMiddleware.Current;
        if (middleware is null || !middleware.IsGiftOffer(offerId)) return;

        __result = middleware.DeliverGiftAfterPurchaseAsync(__result, sessionId, offerId);
    }

}

public sealed class GiftOfferDeliverablePatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod() => AccessTools.Method(typeof(TarcoinStoreService), nameof(TarcoinStoreService.IsDeliverable));

    [PatchPostfix]
    private static void Postfix(ShopOffer offer, ref bool __result)
    {
        if (__result) return;
        if (offer.PurchaseMethod != "INTERNAL_CURRENCY") return;
        var middleware = GiftShopMiddleware.Current;

        if (middleware?.IsGiftOffer(offer.Id.ToString()) == true) __result = true;
    }

}

//Removed warning.
public sealed class GiftOfferCollectDeliverablePatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod() => AccessTools.Method(typeof(TarcoinStoreService), nameof(TarcoinStoreService.CollectDeliverables));

    [PatchPostfix]
    private static void Postfix(ShopOffer offer, ref List<ShopOfferItem> __result)
    {
        var middleware = GiftShopMiddleware.Current;

        // Only deal with the offers through BuildGiftOfferMap.
        if (middleware?.IsGiftOffer(offer.Id.ToString()) != true) return;

        __result.RemoveAll(item => item.Type == GiftShopOfferItemType.GiftMethod);
    }


}