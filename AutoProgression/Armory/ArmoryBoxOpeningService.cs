using System;
using AutoProgression.Diagnostics;
using AutoProgression.Runtime;
using Il2Cpp;
using UnityEngine;

namespace AutoProgression.Armory;

internal sealed class ArmoryBoxOpeningService
{
    private TemporaryCraftableItem selectedItem;
    private bool invalidSelectKeyLogged;
    private bool invalidOpenKeyLogged;
    private bool duplicateKeysLogged;

    internal void Tick()
    {
        KeyCode selectKey = ResolveKey(
            Plugin.Config.ArmoryBoxSelectKey.Value,
            KeyCode.B,
            "select",
            ref invalidSelectKeyLogged);
        KeyCode openKey = ResolveKey(
            Plugin.Config.ArmoryBoxOpenKey.Value,
            KeyCode.N,
            "open",
            ref invalidOpenKeyLogged);

        if (selectKey == openKey)
        {
            if (!duplicateKeysLogged)
            {
                ProgressionLog.Warning(
                    $"Armory box select and open keys are both '{selectKey}'. Configure different keys before using this feature.");
                duplicateKeysLogged = true;
            }
            return;
        }

        duplicateKeysLogged = false;
        if (Input.GetKeyDown(selectKey)) SelectHighlightedItem();
        if (Input.GetKeyDown(openKey)) OpenSelectedItems();
    }

    private void SelectHighlightedItem()
    {
        TemporaryCraftableItem selected = null;
        foreach (TemporaryCraftableItemButton button in
                 Resources.FindObjectsOfTypeAll<TemporaryCraftableItemButton>())
        {
            if (button == null || !button.gameObject.scene.IsValid() ||
                !button.gameObject.scene.isLoaded ||
                !button.gameObject.activeInHierarchy ||
                button.selectedOverlay == null ||
                !button.selectedOverlay.gameObject.activeInHierarchy)
                continue;

            selected = button.item;
            break;
        }

        if (!IsSupportedItem(selected))
        {
            Notify("Select an Armory box, Dragon Egg, or Simurgh Egg first.", false);
            return;
        }

        selectedItem = selected;
        string name = GetDisplayName(selected);
        ProgressionLog.User($"Bulk-opening item selected: {name}.");
        Notify($"Selected: {name}", true);
    }

    private void OpenSelectedItems()
    {
        if (!IsSupportedItem(selectedItem))
        {
            selectedItem = null;
            Notify("No Armory box or egg selected.", false);
            return;
        }

        LootBoxManager manager = LootBoxManager.instance;
        if (manager != null && manager.IsBeingOpened())
        {
            // IsBeingOpened() can stay true after an earlier opening while
            // no loot-box window is on screen (seen on 2026-10-30).
            bool visible = manager.animationIcon != null &&
                           manager.animationIcon.gameObject.activeInHierarchy;
            if (visible)
            {
                Notify("Wait for the current loot-box animation to finish.", false);
                return;
            }
            ProgressionLog.ArmoryDebug(
                "Loot-box manager reports an opening but its window is hidden; opening anyway.");
        }

        int requested = Math.Clamp(Plugin.Config.ArmoryBoxesPerPress.Value, 1, 100);
        int opened = 0;
        string name = GetDisplayName(selectedItem);

        for (int index = 0; index < requested; index++)
        {
            if (!CanOpen(selectedItem)) break;
            LootBox lootBox = selectedItem.lootBoxOpen;
            if (lootBox == null) break;

            try
            {
                selectedItem.lootBoxOpen = null;
                selectedItem.Craft();
                lootBox.Open();
                opened++;
            }
            catch (Exception exception)
            {
                ProgressionLog.Exception(
                    $"Armory box opening after {opened} item(s)",
                    exception);
                break;
            }
            finally
            {
                selectedItem.lootBoxOpen = lootBox;
            }
        }

        if (opened <= 0)
        {
            Notify("Nothing opened: check materials and available Armory space.", false);
            return;
        }

        ProgressionLog.User($"Opened {opened} {name} item(s) in the background.");
        Notify($"Opened {opened} {name}", true);
    }

    private static bool CanOpen(TemporaryCraftableItem item)
    {
        try
        {
            return IsSupportedItem(item) && item.TabVisible() &&
                   item.ExtraCondition() && item.HowManyCanCraft() > 0d &&
                   (!IsArmoryBox(item) || (WeaponsManager.instance?.hasFreeSlot ?? false));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsArmoryBox(TemporaryCraftableItem item) =>
        item != null && item.requiresFreeArmorySpace && item.lootBoxOpen != null;

    private static bool IsSupportedItem(TemporaryCraftableItem item) =>
        IsArmoryBox(item) || IsSupportedEgg(item);

    private static bool IsSupportedEgg(TemporaryCraftableItem item)
    {
        try
        {
            return IsSupportedEggCore(item);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSupportedEggCore(TemporaryCraftableItem item)
    {
        if (item == null || item.requiresFreeArmorySpace || item.lootBoxOpen == null)
            return false;

        Drop dragonEgg = Drops.list?.DragonEgg;
        Drop simurghEgg = Drops.list?.SimurghEgg;
        if (dragonEgg == null || simurghEgg == null) return false;

        var requirements = item.GetRequirements();
        if (requirements == null) return false;

        MaterialRequirement matched = null;
        int requirementCount = 0;
        foreach (MaterialRequirement requirement in requirements)
        {
            if (requirement?.material == null) continue;
            requirementCount++;
            matched = requirement;
        }

        if (requirementCount != 1 || matched == null || matched.amount != 1d)
            return false;

        return MatchesDrop(matched.material, dragonEgg) ||
               MatchesDrop(matched.material, simurghEgg);
    }

    private static bool MatchesDrop(Drop candidate, Drop expected) =>
        candidate == expected || string.Equals(
            candidate?.name,
            expected?.name,
            StringComparison.OrdinalIgnoreCase);

    private static KeyCode ResolveKey(
        string configuredValue,
        KeyCode fallback,
        string purpose,
        ref bool invalidLogged)
    {
        string configured = configuredValue?.Trim();
        if (ConfiguredKey.TryResolve(configured, out KeyCode parsed))
        {
            invalidLogged = false;
            return parsed;
        }

        if (!invalidLogged)
        {
            ProgressionLog.Warning(
                $"Invalid Armory box {purpose} key '{configured}'. Falling back to {fallback}.");
            invalidLogged = true;
        }
        return fallback;
    }

    private static string GetDisplayName(TemporaryCraftableItem item) =>
        string.IsNullOrWhiteSpace(item?.localizedName)
            ? item?.name ?? "unknown"
            : item.localizedName;

    private static void Notify(string message, bool positive)
    {
        ProgressionLog.Debug(message);
        Plugin.ModHelperInstance?.ShowNotification(message, positive);
    }

    internal void Reset()
    {
        selectedItem = null;
        invalidSelectKeyLogged = false;
        invalidOpenKeyLogged = false;
        duplicateKeysLogged = false;
    }
}
