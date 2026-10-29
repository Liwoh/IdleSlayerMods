using System;
using System.Collections.Generic;
using AutoAdventurer.Diagnostics;
using Il2Cpp;
using UnityEngine;

namespace AutoAdventurer.Quests;

internal sealed class SoulFarmingResolver
{
    private const double ScoreEqualityTolerance = 0.000001d;
    private PopupPortals resolvedPopup;
    private bool unavailableLogged;
    private string lastLoggedChoices = string.Empty;
    private bool fallbackMissingLogged;

    // materialQuestActive: an unfinished "Get Materials" quest is active.
    // Those quests accept any material, and materials only drop from the
    // current stage of specific enemies (Enemy.drop), so prefer dimensions
    // where such an enemy spawns instead of the best soul score.
    internal SoulFarmingSelection SelectBestAvailable(bool materialQuestActive = false)
    {
        MapController controller = MapController.instance;
        BaseMap currentMap = controller?.selectedMap;
        PopupPortals popup = ResolvePopup();
        if (controller == null || currentMap == null || popup == null)
            return null;

        // Quest travel reaches many dimensions through GetAvailableMaps(false)
        // while GetAvailableMaps(true) returned only the current one for
        // minutes (observed 2026-10-29), so search both lists.
        List<Map> maps = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (bool includeCurrent in new[] { true, false })
        {
            var source = controller.GetAvailableMaps(includeCurrent);
            if (source == null) continue;
            for (int index = 0; index < source.Count; index++)
            {
                Map candidate = source[index];
                if (candidate != null && seen.Add(candidate.name))
                    maps.Add(candidate);
            }
        }
        if (maps.Count == 0) return null;

        string fallback = (Plugin.Config.FallbackMap.Value ?? string.Empty).Trim();
        string materialMap = (Plugin.Config.MaterialQuestMap.Value ?? string.Empty).Trim();
        bool materialMode = materialQuestActive &&
            !materialMap.Equals("Off", StringComparison.OrdinalIgnoreCase);
        if (materialMode && !materialMap.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            fallback = materialMap;
        if (!materialMode && fallback.Equals("Off", StringComparison.OrdinalIgnoreCase))
            return null;
        bool fixedTarget = fallback.Length > 0 &&
            !fallback.Equals("Auto", StringComparison.OrdinalIgnoreCase) &&
            !fallback.Equals("Off", StringComparison.OrdinalIgnoreCase);
        bool materialAuto = materialMode && !fixedTarget;
        List<string> listed = new();
        Map bestMaterialMap = null;
        double bestMaterialScore = double.MinValue;

        Map bestMap = null;
        double bestScore = double.MinValue;
        double currentScore = double.NaN;
        string currentMapId = currentMap.name ?? string.Empty;

        for (int index = 0; index < maps.Count; index++)
        {
            Map map = maps[index];
            if (map == null || !map.IsAvailable()) continue;

            double souls;
            double coins;
            try
            {
                popup.GetMapScores(map, out souls, out coins);
            }
            catch (Exception exception)
            {
                AdventurerLog.QuestDebug(
                    $"Soul fallback: score read failed; map={map.name}; exception={exception.GetType().Name}.");
                continue;
            }

            if (double.IsNaN(souls) || double.IsInfinity(souls)) continue;
            bool isCurrent = string.Equals(map.name, currentMapId,
                StringComparison.Ordinal);
            if (isCurrent) currentScore = souls;
            if (materialAuto)
            {
                string drops = MaterialDrops(map);
                listed.Add($"{map.name} [{drops}]");
                if (drops.Length > 0 && (bestMaterialMap == null || souls > bestMaterialScore ||
                    (isCurrent && Math.Abs(souls - bestMaterialScore) <= ScoreEqualityTolerance)))
                {
                    bestMaterialMap = map;
                    bestMaterialScore = souls;
                }
            }
            else
                listed.Add(map.name);

            if (fixedTarget)
            {
                if (!map.name.Equals(fallback, StringComparison.OrdinalIgnoreCase))
                    continue;
                bestMap = map;
                bestScore = souls;
                continue;
            }

            bool isBetter = bestMap == null || souls > bestScore;
            bool tiedAndCurrent = bestMap != null && isCurrent &&
                Math.Abs(souls - bestScore) <= ScoreEqualityTolerance;
            if (!isBetter && !tiedAndCurrent) continue;

            bestMap = map;
            bestScore = souls;
        }

        // The Portal list shrinks to the current map while its cooldown runs,
        // so log the choices again whenever they change.
        string choices = string.Join(", ", listed);
        if (choices != lastLoggedChoices)
        {
            lastLoggedChoices = choices;
            AdventurerLog.User(materialAuto
                ? $"Material quest active; dimensions [materials]: {choices}."
                : $"Fallback Map choices: {choices}.");
        }

        if (bestMaterialMap != null)
        {
            bestMap = bestMaterialMap;
            bestScore = bestMaterialScore;
        }
        else if (materialAuto && fallback.Equals("Off", StringComparison.OrdinalIgnoreCase))
            return null;

        if (bestMap == null)
        {
            if (fixedTarget && !fallbackMissingLogged)
            {
                fallbackMissingLogged = true;
                AdventurerLog.Warning(
                    $"Fallback Map '{fallback}' is not an available dimension; staying in the current one.");
            }
            return null;
        }
        unavailableLogged = false;
        return new SoulFarmingSelection
        {
            Map = bestMap,
            SoulScore = bestScore,
            CurrentMapId = currentMapId,
            CurrentSoulScore = currentScore
        };
    }

    // Material names dropped by the current evolution stage of the map's
    // enemies; empty when none of them drops a material.
    private static string MaterialDrops(BaseMap map)
    {
        var enemies = map?.enemies;
        if (enemies == null) return string.Empty;

        HashSet<string> drops = new(StringComparer.Ordinal);
        for (int index = 0; index < enemies.Count; index++)
        {
            Enemy enemy = enemies[index];
            if (enemy == null) continue;
            Enemy stage = CurrentStage(enemy);
            if (stage != null && stage.drop != null)
                drops.Add(stage.drop.name);
        }
        return string.Join(" ", drops);
    }

    private static Enemy CurrentStage(Enemy enemy)
    {
        Enemy first = enemy;
        for (int depth = 0; first.evolutionBackward != null && depth < 32; depth++)
            first = first.evolutionBackward;
        return first.GetCurrentStage() ?? enemy;
    }

    private PopupPortals ResolvePopup()
    {
        PopupPortals popup = PopupPortals.instance ??
                             PortalButton.instance?.popupPortals ??
                             resolvedPopup;
        if (popup != null)
        {
            resolvedPopup = popup;
            unavailableLogged = false;
            return popup;
        }

        foreach (PopupPortals candidate in
                 Resources.FindObjectsOfTypeAll<PopupPortals>())
        {
            if (candidate == null || candidate.gameObject == null) continue;
            var scene = candidate.gameObject.scene;
            if (!scene.IsValid() || !scene.isLoaded) continue;
            resolvedPopup = candidate;
            unavailableLogged = false;
            return candidate;
        }

        if (!unavailableLogged)
        {
            unavailableLogged = true;
            AdventurerLog.QuestDebug(
                "Soul fallback: PopupPortals score provider is unavailable.");
        }
        return null;
    }

    internal void InvalidateSceneObjects()
    {
        resolvedPopup = null;
        unavailableLogged = false;
    }

    internal void Reset()
    {
        InvalidateSceneObjects();
        lastLoggedChoices = string.Empty;
        fallbackMissingLogged = false;
    }
}
