using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AutoCtor;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using EventHandler = FFXIVClientStructs.FFXIV.Client.Game.Event.EventHandler;

namespace ShopItemIcons;

[AutoConstruct]
public unsafe partial class Plugin : IAsyncDalamudPlugin
{
    private const int ShopIconIdOffset = 197;

    private readonly IPluginLog _logger;
    private readonly IDataManager _dataManager;
    private readonly IAddonLifecycle _addonLifecycle;

    public Task LoadAsync(CancellationToken cancellationToken)
    {
        _addonLifecycle.RegisterListener(AddonEvent.PreSetup, "Shop", OnShopPreSetup);
        _addonLifecycle.RegisterListener(AddonEvent.PreRefresh, "Shop", OnShopPreRefresh);
        _addonLifecycle.RegisterListener(AddonEvent.PreRefresh, ["ShopExchangeItem", "ShopExchangeCurrency"], OnShopExchangePreRefresh);
        _addonLifecycle.RegisterListener(AddonEvent.PreRefresh, "GrandCompanyExchange", OnGrandCompanyExchangePreRefresh);
        _addonLifecycle.RegisterListener(AddonEvent.PreRefresh, "InclusionShop", OnInclusionShopPreRefresh);
        _addonLifecycle.RegisterListener(AddonEvent.PreRefresh, "FreeShop", OnFreeShopPreRefresh);
        _addonLifecycle.RegisterListener(AddonEvent.PreRefresh, "SkyIslandExchange2", OnSkyIslandExchange2PreRefresh);

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _addonLifecycle.UnregisterListener(AddonEvent.PreSetup, "Shop", OnShopPreSetup);
        _addonLifecycle.UnregisterListener(AddonEvent.PreRefresh, "Shop", OnShopPreRefresh);
        _addonLifecycle.UnregisterListener(AddonEvent.PreRefresh, ["ShopExchangeItem", "ShopExchangeCurrency"], OnShopExchangePreRefresh);
        _addonLifecycle.UnregisterListener(AddonEvent.PreRefresh, "GrandCompanyExchange", OnGrandCompanyExchangePreRefresh);
        _addonLifecycle.UnregisterListener(AddonEvent.PreRefresh, "InclusionShop", OnInclusionShopPreRefresh);
        _addonLifecycle.UnregisterListener(AddonEvent.PreRefresh, "FreeShop", OnFreeShopPreRefresh);
        _addonLifecycle.UnregisterListener(AddonEvent.PreRefresh, "SkyIslandExchange2", OnSkyIslandExchange2PreRefresh);

        return ValueTask.CompletedTask;
    }

    private void OnShopPreSetup(AddonEvent type, AddonArgs args)
    {
        if (args is AddonSetupArgs setupArgs)
            UpdateShopIcons(GetAtkValues(setupArgs));
    }

    private void OnShopPreRefresh(AddonEvent type, AddonArgs args)
    {
        if (args is AddonRefreshArgs refreshArgs)
            UpdateShopIcons(GetAtkValues(refreshArgs));
    }

    // Shops: Simple merchants that sell items for Gil.
    private void UpdateShopIcons(Span<AtkValue> values)
    {
        if (!EnsureLength(values, 625))
            return;

        if (TryUpdateGilShopIcons(values))
            return;

        if (TryUpdateRetainerBuybackIcons(values))
            return;

        _logger.Debug("[UpdateShopIcons] Could not update icons: unknown Shop");
    }

    private bool TryUpdateGilShopIcons(Span<AtkValue> values)
    {
        var handler = ShopEventHandler.AgentProxy.Instance()->Handler;
        if (handler == null)
            return false;

        if (values[0] is not { Type: AtkValueType.UInt, UInt: var tabIndex })
        {
            _logger.Debug("[UpdateShopIcons:GilShop] Could not read tab index. Aborting.");
            return false;
        }

        switch (tabIndex)
        {
            case 0: // Buy
                for (var i = 0; i < handler->VisibleItemsCount; i++)
                {
                    ref var iconIdValue = ref values[ShopIconIdOffset + i];
                    if (iconIdValue is not { Type: AtkValueType.UInt })
                        continue;

                    var itemIndex = handler->VisibleItems[i];
                    if (itemIndex < 0 || itemIndex > handler->ItemsCount)
                        continue;

                    var itemId = handler->Items[itemIndex].ItemId;
                    if (itemId == 0)
                        continue;

                    iconIdValue.UInt = GetItemIcon(itemId);
                }

                return true;

            case 1: // Buyback
                for (var i = 0; i < handler->BuybackCount; i++)
                {
                    ref var iconIdValue = ref values[ShopIconIdOffset + i];
                    if (iconIdValue is not { Type: AtkValueType.UInt })
                        continue;

                    var itemId = handler->Buyback[i].ItemId;
                    if (itemId == 0)
                        continue;

                    iconIdValue.UInt = GetItemIcon(itemId);
                }

                return true;

            default:
                _logger.Debug("[UpdateShopIcons:GilShop] Invalid TabIndex. Aborting.");
                return false;
        }
    }

    private bool TryUpdateRetainerBuybackIcons(Span<AtkValue> values)
    {
        var handler = EventFramework.Instance()->GetEventHandlerById(0x310001);
        if (handler == null)
            return false;

        var agent = AgentShop.Instance();
        if (agent->ItemRetainerBuyback == null)
            return false;

        const int offset = EventHandler.StructSize + 8;
        if (agent->EventReceiver != (AtkModuleInterface.AtkEventInterface*)((nint)handler + offset))
            return false;

        for (var i = 0; i < agent->ItemRetainerBuybackSpan.Length; i++)
        {
            ref var iconIdValue = ref values[ShopIconIdOffset + i];
            if (iconIdValue is not { Type: AtkValueType.UInt })
                continue;

            var itemId = agent->ItemRetainerBuybackSpan[i].ItemId;
            if (itemId == 0)
                continue;

            iconIdValue.UInt = GetItemIcon(itemId);
        }

        return true;
    }

    // Currency Exchange: Vendors that sell items for currency, such as all the societies vendors, or the Trophy Crystal Exchange.
    // Item Exchange: Vendors that trade items for items, such as the Wolf Collar Exchange or Itinerant Moogles that exchange items for Irregular Tomestones during the Moogle Treasure Trove event.
    private void OnShopExchangePreRefresh(AddonEvent type, AddonArgs args)
    {
        if (args is not AddonRefreshArgs refreshArgs)
            return;

        var values = GetAtkValues(refreshArgs);

        // 48 89 5C 24 ?? 55 56 57 41 54 41 55 41 56 41 57 48 83 EC ?? 0F B6 99
        if (!EnsureLength(values, 3325))
            return;

        if (values[4] is not { Type: AtkValueType.UInt, UInt: var itemCount })
            return; // undefined when addon is opened, don't log

        ReplaceItemIcons(values, itemCount, 1066, 212); // main items (weapons etc.)
        ReplaceItemIcons(values, itemCount, 1088, 234); // sub items (shields)
    }

    // Grand Company Seal Exchange: Quartermasters of Grand Companies.
    private void OnGrandCompanyExchangePreRefresh(AddonEvent type, AddonArgs args)
    {
        if (args is not AddonRefreshArgs refreshArgs)
            return;

        var values = GetAtkValues(refreshArgs);

        // last function called in GCShopEventHandler_vf47
        if (!EnsureLength(values, 556))
            return;

        ReplaceItemIcons(values, 50, 317, 167);
    }

    // Item Exchange (categorized): Splendors Vendors, the Scrip Exchange or the Wolf Mark Exchange.
    private void OnInclusionShopPreRefresh(AddonEvent type, AddonArgs args)
    {
        if (args is not AddonRefreshArgs refreshArgs)
            return;

        var values = GetAtkValues(refreshArgs);

        // second to last function called in AgentInclusionShop_Update
        if (!EnsureLength(values, 2939))
            return;

        if (values[298] is not { Type: AtkValueType.UInt, UInt: var itemCount })
        {
            _logger.Debug("[OnInclusionShopPreRefresh] Could not read item count.");
            return;
        }

        ReplaceItemIcons(values, itemCount, 300, 301, 18);
    }

    // Rewards: Vendors that provide job gear free of charge.
    private void OnFreeShopPreRefresh(AddonEvent type, AddonArgs args)
    {
        if (args is not AddonRefreshArgs refreshArgs)
            return;

        var values = GetAtkValues(refreshArgs);

        // found in AgentFreeShop_Show
        if (!EnsureLength(values, 565))
            return;

        if (values[76] is not { Type: AtkValueType.UInt, UInt: var itemCount })
        {
            _logger.Debug("[OnFreeShopPreRefresh] Could not read item count.");
            return;
        }

        ReplaceItemIcons(values, itemCount, 138, 199);
    }

    // Exchange: Calamity Salvager when exchanging old pigments for all-purpose pigments.
    private void OnSkyIslandExchange2PreRefresh(AddonEvent type, AddonArgs args)
    {
        if (args is not AddonRefreshArgs refreshArgs)
            return;

        var values = GetAtkValues(refreshArgs);

        if (!EnsureLength(values, 461))
            return;

        if (values[0] is not { Type: AtkValueType.UInt, UInt: var itemCount })
        {
            _logger.Debug("[OnSkyIslandExchange2PreRefresh] Could not read item count.");
            return;
        }

        ReplaceItemIcons(values, itemCount, 56, 176);
    }

    private void ReplaceItemIcons(
        Span<AtkValue> values,
        uint itemCount,
        int itemIdsStartIndex,
        int iconIdsStartIndex,
        int stepSize = 1,
        [CallerMemberName] string memberName = "")
    {
        _logger.Debug($"[{memberName}] Replacing icons for {itemCount} item slots");

        for (var i = 0; i < itemCount; i++)
        {
            ref var itemIdValue = ref values[itemIdsStartIndex + i * stepSize];
            if (itemIdValue is not { Type: AtkValueType.UInt, UInt: var itemId } || itemId == 0)
                continue;

            ref var iconIdValue = ref values[iconIdsStartIndex + i * stepSize];
            switch (iconIdValue.Type)
            {
                case AtkValueType.Int:
                    iconIdValue.Int = (int)GetItemIcon(itemIdValue.UInt);
                    break;

                case AtkValueType.UInt:
                    iconIdValue.UInt = GetItemIcon(itemIdValue.UInt);
                    break;
            }
        }
    }

    private uint GetItemIcon(uint itemId)
    {
        return ItemUtil.IsEventItem(itemId)
            ? (_dataManager.Excel.GetSheet<EventItem>().TryGetRow(itemId, out var eventItemRow) ? eventItemRow.Icon : 0u)
            : (_dataManager.Excel.GetSheet<Item>().TryGetRow(ItemUtil.GetBaseId(itemId).ItemId, out var itemRow) ? itemRow.Icon : 0u);
    }

    public static Span<AtkValue> GetAtkValues(AddonRefreshArgs args)
    {
        return new((void*)args.AtkValues, (int)args.AtkValueCount);
    }

    public static Span<AtkValue> GetAtkValues(AddonSetupArgs args)
    {
        return new((void*)args.AtkValues, (int)args.AtkValueCount);
    }

    public bool EnsureLength(Span<AtkValue> values, int expectedCount, [CallerMemberName] string memberName = "")
    {
        if (values.Length == expectedCount)
            return true;

        _logger.Debug($"[{memberName}] Expected {expectedCount} AtkValues, found {values.Length}. Aborting.");
        return false;
    }
}
