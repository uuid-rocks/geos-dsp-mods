using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using DspNative.Validation;
using HarmonyLib;
using UnityEngine;

namespace GeosBeltTweaks.Scenarios
{
    // Clicks belts from flat ground onto the highest terrain near the player, then aims at each placed end again.
    public sealed class BeltBuildHeight : ProbeScenario
    {
        private const int Belt = 2001;

        public override IEnumerator Run(ScenarioContext context)
        {
            Player player = GameMain.mainPlayer;
            PlanetData planet = GameMain.localPlanet ?? throw new InvalidOperationException("The save must load on a planet.");
            PlanetFactory factory = planet.factory;
            PlayerAction_Build build = player.controller.actionBuild;
            BuildTool_Path tool = build.pathTool;
            var aim = (ConfigEntry<bool>)AccessTools.Field(AccessTools.TypeByName("GeosBeltTweaks.Plugin"), "AimBeltsAtBuildHeight").GetValue(null);
            Type halfGrid = AccessTools.TypeByName("GeosBeltTweaks.HalfGridBeltSnapPatch");

            float radius = planet.realRadius + 0.2f;
            Vector3 up = player.position.normalized;
            Vector3 east = Vector3.Cross(up, Vector3.up).normalized;
            Vector3 north = Vector3.Cross(east, up).normalized;
            var ground = new List<(Vector3 point, float height)>();
            for (int x = -52; x <= 52; x += 2)
            for (int y = -52; y <= 52; y += 2)
            {
                Vector3 direction = planet.aux.Snap(up * radius + x * east + y * north, true).normalized;
                if (Physics.Raycast(new Ray(direction * (radius + 10), -direction), out RaycastHit hit, 20, 1 << (int)Layer.Terrain, QueryTriggerInteraction.Collide))
                    ground.Add((hit.point, hit.point.magnitude - radius));
            }
            var targets = ground.Where(sample => sample.height > 0.1f).OrderByDescending(sample => sample.height).Take(12).ToList();
            if (targets.Count == 0) throw new InvalidOperationException("The save needs terrain above the belt height near the player.");

            player.package.AddItemStacked(Belt, 1000, 0, out _);
            player.SetHandItems(Belt, 0);
            for (int frame = 0; frame < 10; frame++) yield return null;
            if (player.controller.cmd.type != ECommand.Build || build.activeTool != tool) throw new InvalidOperationException("Holding belts did not open the belt tool.");

            var rows = new List<string> { "halfGrid\taim\ttarget\theight\tslope\tringOffsetPx\tcursorOffsetPx\tcell\tplaced\tplacedEndDelta\tsnapped" };
            var cursorOffsets = new Dictionary<(bool half, int target, float slope, bool aim), float>();
            int placed = 0, snapped = 0;
            float worstPlacement = 0;
            Vector2 center = new Vector2(Screen.width / 2f, Screen.height / 2f);
            try
            {
                foreach (bool half in new[] { false, true })
                {
                    AccessTools.Field(halfGrid, "Enabled").SetValue(null, half);
                    AccessTools.Field(halfGrid, "HalfStep").SetValue(null, half);
                    for (int index = 0; index < targets.Count; index++)
                    foreach (float slope in new[] { 0f, 1f, 2f })
                    foreach (bool aimOn in new[] { false, true })
                    {
                        aim.Value = aimOn;
                        (Vector3 target, float height) = targets[index];
                        Vector3 radial = target.normalized;
                        Vector3 camera = target + 18 * (radial + slope * Vector3.Cross(radial, Vector3.up).normalized);
                        context.HoldCamera(camera, Quaternion.LookRotation(target - camera, radial + north * 0.05f), 45);
                        Vector2? start = ground
                            .Where(sample => Mathf.Abs(sample.height) < 0.08f && Vector3.Distance(sample.point, target) > 5)
                            .OrderBy(sample => (sample.point - target).sqrMagnitude)
                            .Select(sample => GameCamera.main.WorldToScreenPoint(sample.point))
                            .Where(pixel => pixel.z > 0 && pixel.x > 200 && pixel.x < Screen.width - 200 && pixel.y > 200 && pixel.y < Screen.height - 150)
                            .Select(pixel => (Vector2?)pixel)
                            .FirstOrDefault();
                        if (start == null) throw new InvalidOperationException("No flat ground is on screen near target " + index + ".");

                        context.Pointer = start;
                        for (int frame = 0; frame < 3; frame++) yield return null;
                        yield return context.Click();
                        if (player.controller.cmd.stage != 1) throw new InvalidOperationException("Clicking flat ground did not start a belt.");
                        context.Pointer = center;
                        for (int frame = 0; frame < 8; frame++) yield return null;
                        if (!tool.castGround || tool.castObject)
                        {
                            rows.Add(string.Join("\t", half, aimOn, index, height.ToString("F4", CultureInfo.InvariantCulture), slope, "", "", "pointer over object " + tool.castObjectId, false, "", false));
                            yield return context.Click(KeyCode.Mouse1);
                            continue;
                        }
                        Vector3 cell = tool.cursorTarget;
                        List<ConnGizmoObj> ends = Traverse.Create(build.model.connRenderer).Field<List<ConnGizmoObj>>("objs_0").Value;
                        if (ends.Count == 0) throw new InvalidOperationException("The belt end ring is missing.");
                        float ring = Vector2.Distance(GameCamera.main.WorldToScreenPoint(ends[ends.Count - 1].pos), center);
                        float cursor = Vector2.Distance(GameCamera.main.WorldToScreenPoint(cell), center);
                        cursorOffsets[(half, index, slope, aimOn)] = cursor;
                        if (!half && index == 0) yield return context.Capture("aim-" + (aimOn ? "on" : "off") + "-slope-" + slope);

                        var existing = new HashSet<int>(Prebuilds(factory));
                        yield return context.Click();
                        int[] created = Prebuilds(factory).Where(id => !existing.Contains(id)).ToArray();
                        if (player.controller.cmd.stage != 0) yield return context.Click(KeyCode.Mouse1);
                        int end = created.OrderBy(id => (factory.prebuildPool[id].pos - cell).sqrMagnitude).FirstOrDefault();
                        float placement = end == 0 ? -1 : Vector3.Distance(factory.prebuildPool[end].pos, cell);
                        context.Pointer = start;
                        for (int frame = 0; frame < 3; frame++) yield return null;
                        context.Pointer = center;
                        for (int frame = 0; frame < 4; frame++) yield return null;
                        bool snap = end != 0 && tool.castObject && tool.castObjectId == -end;
                        foreach (int id in created) factory.RemovePrebuildWithComponents(id);
                        if (aimOn && end != 0)
                        {
                            placed++;
                            if (snap) snapped++;
                            worstPlacement = Mathf.Max(worstPlacement, placement);
                        }
                        rows.Add(string.Join("\t", half, aimOn, index, height.ToString("F4", CultureInfo.InvariantCulture), slope, ring.ToString("F2", CultureInfo.InvariantCulture), cursor.ToString("F2", CultureInfo.InvariantCulture), cell.ToString("F4"), end != 0, placement.ToString("F5", CultureInfo.InvariantCulture), snap));
                    }
                }
            }
            finally
            {
                aim.Value = true;
                AccessTools.Field(halfGrid, "Enabled").SetValue(null, false);
                AccessTools.Field(halfGrid, "HalfStep").SetValue(null, false);
                player.SetHandItems(0, 0);
                context.Write("belt-build-height.tsv", rows);
            }

            var pairs = cursorOffsets.Keys.Where(key => !key.aim && cursorOffsets.ContainsKey((key.half, key.target, key.slope, true))).Select(key => (off: cursorOffsets[key], on: cursorOffsets[(key.half, key.target, key.slope, true)])).ToList();
            int worse = pairs.Count(pair => pair.on > pair.off + 1);
            int closer = pairs.Count(pair => pair.on < pair.off - 5);
            if (worse > 0 || closer == 0) throw new InvalidOperationException("With aim on, " + worse + " belt end cells moved away from the pointer and " + closer + " moved closer.");
            if (placed == 0 || snapped != placed || worstPlacement > 0.001f) throw new InvalidOperationException("With aim on, " + snapped + " of " + placed + " placed belt ends snapped when aimed at again; the farthest end was " + worstPlacement + " m from its cursor cell.");
        }

        private static IEnumerable<int> Prebuilds(PlanetFactory factory)
        {
            for (int id = 1; id < factory.prebuildCursor; id++)
                if (factory.prebuildPool[id].id == id) yield return id;
        }
    }
}
