using UnityEngine;

namespace GeosBeltTweaks
{
    internal static class BeltTerrainAim
    {
        // Moves a raised terrain hit to where the mouse ray meets the belt's build height, then snaps it.
        internal static Vector3 Snap(PlanetAuxData aux, Vector3 pos, bool onTerrain, BuildTool_Path tool)
        {
            Vector3 snapped = HalfGridBeltSnapPatch.Snap(aux, pos, onTerrain);
            float radius = snapped.magnitude;
            if (!onTerrain || !Plugin.AimBeltsAtBuildHeight.Value || pos.magnitude - radius <= 0.05f)
            {
                return snapped;
            }

            Ray ray = tool.mouseRay;
            float closestDistance = -Vector3.Dot(ray.origin, ray.direction);
            float discriminant = radius * radius - ray.GetPoint(closestDistance).sqrMagnitude;
            if (!(discriminant > 0f))
            {
                return snapped;
            }

            float distance = closestDistance - Mathf.Sqrt(discriminant);
            return distance > 0f && distance <= 400f
                ? HalfGridBeltSnapPatch.Snap(aux, ray.GetPoint(distance), onTerrain: true)
                : snapped;
        }
    }
}
