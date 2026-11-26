// QuickMaintenance - one click sharpens / cleans an item as far as it will go.
// Game time, tool wear and skill rolls are multiplied to match the clicks it replaces.
using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(QuickMaintenance.Mod), "QuickMaintenance", "1.2.5", "ShreddyJim", "https://github.com/ShreddyJimMods/tld-quick-maintenance/releases")]
[assembly: MelonGame("Hinterland", "TheLongDark")]

namespace QuickMaintenance
{
    public class Mod : MelonMod
    {
        internal static MelonLogger.Instance Log;
        internal static MelonPreferences_Entry<bool> Enabled, ScaleTime, ScaleToolWear, Verbose;
        internal static MelonPreferences_Entry<float> ProgressBarSeconds;

        public override void OnInitializeMelon()
        {
            Log = LoggerInstance;
            var cat = MelonPreferences.CreateCategory("QuickMaintenance");
            Enabled = cat.CreateEntry("Enabled", true, description: "One click sharpens/cleans as far as possible");
            ScaleTime = cat.CreateEntry("ScaleTime", true, description: "Game time passes for every step that was combined (false = time of a single step)");
            ScaleToolWear = cat.CreateEntry("ScaleToolWear", true, description: "Whetstone / cleaning kit wears for every step that was combined (false = wear of a single step)");
            ProgressBarSeconds = cat.CreateEntry("ProgressBarSeconds", 3f, description: "Real seconds the progress bar takes (0 = leave the game's own value)");
            Verbose = cat.CreateEntry("Verbose", false, description: "Write what the mod did to the MelonLoader log");
            Log.Msg("loaded");
        }
    }

    internal enum Kind { Sharpen, Clean }

    internal static class Core
    {
        private struct Orig { public float IncMin, IncMax; public int DurMin, DurMax; }
        private static readonly Dictionary<int, Orig> Originals = new Dictionary<int, Orig>();
        private static string lastLogged;

        // Steps the next click will combine, per kind, and the item they were worked out for.
        private static readonly int[] Planned = { 1, 1 };
        private static readonly int[] PlannedItem = { 0, 0 };
        private static bool inPrepare;
        private static int successFrame = -1;
        internal static int ActiveSteps = 1;    // steps combined by the action in progress
        internal static int SkillSteps = 1;
        internal static bool InToolDegrade, ToolHookFired, InSkillRepeat;

        private static void V(string msg)
        {
            if (Mod.Verbose.Value && msg != lastLogged) { lastLogged = msg; Mod.Log.Msg(msg); }
        }

        private static void Query(Panel_Inventory_Examine p, Kind k, out float inc, out int mins)
        {
            inc = 0f; mins = 0;
            if (k == Kind.Sharpen) p.GetSharpenConditionIncreaseAndDuration(out inc, out mins);
            else p.GetCleanConditionIncreaseAndDuration(out inc, out mins);
        }

        // How many more uses the best matching tool in the inventory has left (int.MaxValue = unknown / no limit).
        private static int ToolUsesLeft(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<GearItem> required)
        {
            if (!Mod.ScaleToolWear.Value || required == null || required.Length == 0) return int.MaxValue;
            var inv = GameManager.GetInventoryComponent();
            if (inv == null) return int.MaxValue;
            int best = -1;
            for (int i = 0; i < required.Length; i++)
            {
                var prefab = required[i];
                if (prefab == null) continue;
                var tool = inv.GetBestGearItemWithName(prefab.name);
                if (tool == null || tool.m_DegradeOnUse == null) continue;
                float per = tool.m_DegradeOnUse.m_DegradeHP;
                if (per <= 0f) return int.MaxValue;
                best = Math.Max(best, (int)Math.Floor(tool.m_CurrentHP / per + 0.001f));
            }
            return best < 0 ? int.MaxValue : Math.Max(1, best);
        }

        // Rewrites the item's per-use numbers so that a single use takes it as far as possible.
        // Called from every place the game asks for those numbers; it asks for them itself, hence the guard.
        internal static void Prepare(Panel_Inventory_Examine p, Kind k)
        {
            if (inPrepare || p == null) return;
            inPrepare = true;
            try { PrepareCore(p, k); }
            finally { inPrepare = false; }
        }

        private static void PrepareCore(Panel_Inventory_Examine p, Kind k)
        {
            Planned[(int)k] = 1;
            var gi = p.m_GearItem;
            PlannedItem[(int)k] = gi == null ? 0 : gi.GetInstanceID();
            if (gi == null) return;
            float secs = Mod.ProgressBarSeconds.Value;
            if (Mod.Enabled.Value && secs > 0f)
            {
                if (k == Kind.Sharpen) p.m_SharpenTimeSeconds = secs; else p.m_CleanTimeSeconds = secs;
            }
            var s = k == Kind.Sharpen ? gi.m_Sharpenable : null;
            var c = k == Kind.Clean ? gi.m_Cleanable : null;
            if (s == null && c == null) return;

            int id = s != null ? s.GetInstanceID() : c.GetInstanceID();
            if (!Originals.TryGetValue(id, out var o))
            {
                o = s != null
                    ? new Orig { IncMin = s.m_ConditionIncreaseMin, IncMax = s.m_ConditionIncreaseMax, DurMin = s.m_DurationMinutesMin, DurMax = s.m_DurationMinutesMax }
                    : new Orig { IncMin = c.m_ConditionIncreaseMin, IncMax = c.m_ConditionIncreaseMax, DurMin = c.m_DurationMinutesMin, DurMax = c.m_DurationMinutesMax };
                Originals[id] = o;
            }
            Set(s, c, o, 1, 1);
            if (!Mod.Enabled.Value) return;

            Query(p, k, out float inc, out int mins);
            float scale = o.IncMax <= 1f ? 1f : 100f;               // fields are either fractions or percent
            float needed = (1f - gi.GetNormalizedCondition()) * scale;
            if (inc <= 0f || needed <= inc * 1.001f) return;        // one normal step already finishes it

            int n = (int)Math.Ceiling(needed / inc - 0.001f);
            n = Math.Min(n, ToolUsesLeft(s != null ? s.m_RequiredGear : c.m_RequiredGear));
            if (n <= 1) return;

            Set(s, c, o, n, Mod.ScaleTime.Value ? n : 1);
            Query(p, k, out float inc2, out int mins2);
            // The game caps the gain it reports at what the item still needs (as a whole number), so a small
            // remainder can look like "no change". A longer duration then shows the combined values were used.
            bool capped = inc2 < inc * 1.5f && mins2 > mins && inc2 >= needed - 1.01f;
            if (inc2 < inc * 1.5f && !capped)
            {
                // The game did not follow the changed numbers; put them back and behave like vanilla.
                Set(s, c, o, 1, 1);
                V($"{k} {gi.name}: nothing to combine here (one step gives {inc:0.##} in {mins} min and the game reports the same for {n}) - this click works as in the unmodded game");
                return;
            }
            int steps = Planned[(int)k] = capped ? n : Math.Max(1, (int)Math.Round(inc2 / inc));
            if (Mod.ScaleTime.Value && mins > 0 && mins2 > 0)
            {
                // The game's own time formula is not linear; steer it to exactly what the separate clicks would cost.
                int target = capped ? mins * n : (int)Math.Round(mins * inc2 / inc);
                if (mins2 != target)
                {
                    Set(s, c, o, n, n * (float)target / mins2);
                    Query(p, k, out inc2, out mins2);
                }
            }
            V($"{k} {gi.name}: {steps} steps in one - +{inc2:0.##} condition, {mins2} min (single step +{inc:0.##}, {mins} min; fields {o.IncMin}-{o.IncMax}, {o.DurMin}-{o.DurMax} min)");
        }

        private static void Set(Sharpenable s, Cleanable c, Orig o, int incMul, float durMul)
        {
            if (s != null)
            {
                s.m_ConditionIncreaseMin = o.IncMin * incMul; s.m_ConditionIncreaseMax = o.IncMax * incMul;
                s.m_DurationMinutesMin = (int)Math.Round(o.DurMin * durMul); s.m_DurationMinutesMax = (int)Math.Round(o.DurMax * durMul);
            }
            else
            {
                c.m_ConditionIncreaseMin = o.IncMin * incMul; c.m_ConditionIncreaseMax = o.IncMax * incMul;
                c.m_DurationMinutesMin = (int)Math.Round(o.DurMin * durMul); c.m_DurationMinutesMax = (int)Math.Round(o.DurMax * durMul);
            }
        }

        internal static void Guard(string where, Action a)
        {
            try { a(); }
            catch (Exception e) { Mod.Log.Error($"{where}: {e}"); }
        }

        internal static bool InSuccess;

        internal static void Trace(string msg) { if (Mod.Verbose.Value) Mod.Log.Msg(msg); }

        internal static void Begin(Panel_Inventory_Examine p, Kind k)
        {
            Prepare(p, k);
            Trace($"{k} clicked: {Planned[(int)k]} step(s)");
        }

        // The action finished: this is the one moment the game always goes through, so the step count
        // for tool wear and skill is taken here rather than from the click (which the game may not report).
        internal static void Succeed(Panel_Inventory_Examine p, Kind k)
        {
            var gi = p == null ? null : p.m_GearItem;
            int steps = gi != null && gi.GetInstanceID() == PlannedItem[(int)k] ? Planned[(int)k] : 1;
            ActiveSteps = SkillSteps = Math.Max(1, steps);
            InSuccess = true; ToolHookFired = false;
            successFrame = Time.frameCount;
            Trace($"{k} succeeded: {ActiveSteps} step(s) | before: {Describe(gi, k)}");
        }

        // What the item and its clean/sharpen numbers look like right now (verbose logging only).
        internal static string Describe(GearItem gi, Kind k)
        {
            if (!Mod.Verbose.Value || gi == null) return "";
            try
            {
                string s = $"{gi.name} HP {gi.m_CurrentHP:0.##} condition {gi.GetNormalizedCondition() * 100f:0.##}%";
                if (k == Kind.Clean && gi.m_Cleanable != null) s += $" clean gain {gi.m_Cleanable.m_ConditionIncreaseMin:0.###}-{gi.m_Cleanable.m_ConditionIncreaseMax:0.###}";
                if (k == Kind.Sharpen && gi.m_Sharpenable != null) s += $" sharpen gain {gi.m_Sharpenable.m_ConditionIncreaseMin:0.###}-{gi.m_Sharpenable.m_ConditionIncreaseMax:0.###}";
                return s;
            }
            catch (Exception e) { return "(could not read item: " + e.Message + ")"; }
        }

        internal static void After(Panel_Inventory_Examine p, Kind k)
        {
            InSuccess = false;
            if (Mod.Verbose.Value && p != null) Trace($"{k} after: {Describe(p.m_GearItem, k)}");
        }

        // Tool wear that does not belong to a sharpen/clean that just finished must never be multiplied.
        internal static void ToolWearBegins()
        {
            if (Time.frameCount != successFrame) ActiveSteps = SkillSteps = 1;
            InToolDegrade = true;
            if (ActiveSteps > 1) Trace($"tool wear requested: {ActiveSteps} step(s)");
        }

        internal static void ToolWearEnds()
        {
            InToolDegrade = false;
            if (ActiveSteps > 1 && Mod.ScaleToolWear.Value && !ToolHookFired)
                Mod.Log.Warning($"tool wear for {ActiveSteps} combined steps could not be applied (the game wore the tool once)");
            ActiveSteps = 1;
        }
    }

    // ---- keep the numbers (and the labels that show them) up to date ----
    [HarmonyPatch(typeof(Panel_Inventory_Examine), nameof(Panel_Inventory_Examine.RefreshSharpenPanel))]
    internal static class P_RefreshSharpen { static void Prefix(Panel_Inventory_Examine __instance) => Core.Guard("RefreshSharpenPanel", () => Core.Prepare(__instance, Kind.Sharpen)); }

    [HarmonyPatch(typeof(Panel_Inventory_Examine), nameof(Panel_Inventory_Examine.RefreshCleanPanel))]
    internal static class P_RefreshClean { static void Prefix(Panel_Inventory_Examine __instance) => Core.Guard("RefreshCleanPanel", () => Core.Prepare(__instance, Kind.Clean)); }

    // RefreshCleanPanel / OnClean are not reliably reached (the game inlines them or takes another route,
    // e.g. keyboard or the tool-select step), so also hook the functions the game does call for its numbers.
    // No hook on GetSharpenConditionIncreaseAndDuration either, for the same reason as the clean one below.
    // No hook on GetCleanConditionIncreaseAndDuration: hooking it made every call from the game fail
    // (a NullReferenceException inside the hook's entry stub), which left cleaning with a gain of zero.
    // UpdateCleanLabels and OnSelectCleanTool below already cover the clean path.

    [HarmonyPatch(typeof(Panel_Inventory_Examine), nameof(Panel_Inventory_Examine.UpdateSharpenLabels))]
    internal static class P_SharpenLabels { static void Prefix(Panel_Inventory_Examine __instance) => Core.Guard("UpdateSharpenLabels", () => Core.Prepare(__instance, Kind.Sharpen)); }

    [HarmonyPatch(typeof(Panel_Inventory_Examine), nameof(Panel_Inventory_Examine.UpdateCleanLabels))]
    internal static class P_CleanLabels { static void Prefix(Panel_Inventory_Examine __instance) => Core.Guard("UpdateCleanLabels", () => Core.Prepare(__instance, Kind.Clean)); }

    [HarmonyPatch(typeof(Panel_Inventory_Examine), nameof(Panel_Inventory_Examine.OnSelectSharpenTool))]
    internal static class P_SelectSharpenTool { static void Prefix(Panel_Inventory_Examine __instance) => Core.Guard("OnSelectSharpenTool", () => Core.Begin(__instance, Kind.Sharpen)); }

    [HarmonyPatch(typeof(Panel_Inventory_Examine), nameof(Panel_Inventory_Examine.OnSelectCleanTool))]
    internal static class P_SelectCleanTool { static void Prefix(Panel_Inventory_Examine __instance) => Core.Guard("OnSelectCleanTool", () => Core.Begin(__instance, Kind.Clean)); }

    // ---- the click ----
    [HarmonyPatch(typeof(Panel_Inventory_Examine), nameof(Panel_Inventory_Examine.OnSharpen))]
    internal static class P_OnSharpen { static void Prefix(Panel_Inventory_Examine __instance) => Core.Guard("OnSharpen", () => Core.Begin(__instance, Kind.Sharpen)); }

    [HarmonyPatch(typeof(Panel_Inventory_Examine), nameof(Panel_Inventory_Examine.OnClean))]
    internal static class P_OnClean { static void Prefix(Panel_Inventory_Examine __instance) => Core.Guard("OnClean", () => Core.Begin(__instance, Kind.Clean)); }

    // Completion: the step count for tool wear and skill is fixed here.
    [HarmonyPatch(typeof(Panel_Inventory_Examine), nameof(Panel_Inventory_Examine.SharpenSuccessful))]
    internal static class P_SharpenSuccessful
    {
        static void Prefix(Panel_Inventory_Examine __instance) => Core.Guard("SharpenSuccessful", () => Core.Succeed(__instance, Kind.Sharpen));
        static void Postfix(Panel_Inventory_Examine __instance) { Core.InSuccess = false; Core.Guard("SharpenAfter", () => Core.After(__instance, Kind.Sharpen)); }
    }

    [HarmonyPatch(typeof(Panel_Inventory_Examine), nameof(Panel_Inventory_Examine.CleanSuccessful))]
    internal static class P_CleanSuccessful
    {
        static void Prefix(Panel_Inventory_Examine __instance) => Core.Guard("CleanSuccessful", () => Core.Succeed(__instance, Kind.Clean));
        static void Postfix(Panel_Inventory_Examine __instance) { Core.InSuccess = false; Core.Guard("CleanAfter", () => Core.After(__instance, Kind.Clean)); }
    }

    // ---- tool wear: the game wears the tool once; make that one wear count for every combined step ----
    [HarmonyPatch(typeof(Panel_Inventory_Examine), nameof(Panel_Inventory_Examine.DegradeToolUsedForAction))]
    internal static class P_DegradeTool
    {
        static void Prefix() => Core.ToolWearBegins();
        static void Postfix() => Core.ToolWearEnds();
    }

    [HarmonyPatch(typeof(GearItem), nameof(GearItem.DegradeOnUse))]
    internal static class P_GearDegradeOnUse
    {
        static void Prefix(GearItem __instance, ref float __state)
        {
            __state = -1f;
            if (!(Core.InToolDegrade || Core.InSuccess) || Core.ToolHookFired || Core.ActiveSteps <= 1 || !Mod.ScaleToolWear.Value) return;
            var d = __instance.m_DegradeOnUse;
            if (d == null) return;
            __state = d.m_DegradeHP;
            d.m_DegradeHP = __state * Core.ActiveSteps;
            Core.ToolHookFired = true;
            if (Mod.Verbose.Value) Mod.Log.Msg($"tool {__instance.name}: wear x{Core.ActiveSteps} ({__state:0.##} -> {d.m_DegradeHP:0.##} HP), HP before {__instance.m_CurrentHP:0.##}");
        }
        static void Postfix(GearItem __instance, float __state)
        {
            if (__state < 0f) return;
            var d = __instance.m_DegradeOnUse;
            if (d != null) d.m_DegradeHP = __state;
        }
    }

    // ---- skill: one roll per combined step, as the separate clicks would have given ----
    [HarmonyPatch(typeof(Panel_Inventory_Examine), nameof(Panel_Inventory_Examine.UpdateSkillAfterSharpen))]
    internal static class P_SkillSharpen
    {
        static void Postfix(Panel_Inventory_Examine __instance, bool success)
        {
            if (Core.InSkillRepeat) return;
            int extra = Math.Min(Core.SkillSteps - 1, 40); Core.SkillSteps = 1;
            Core.InSkillRepeat = true;
            Core.Guard("UpdateSkillAfterSharpen", () => { for (int i = 0; i < extra; i++) __instance.UpdateSkillAfterSharpen(success); });
            Core.InSkillRepeat = false;
        }
    }

    [HarmonyPatch(typeof(Panel_Inventory_Examine), nameof(Panel_Inventory_Examine.UpdateSkillAfterClean))]
    internal static class P_SkillClean
    {
        static void Postfix(Panel_Inventory_Examine __instance, bool success)
        {
            if (Core.InSkillRepeat) return;
            int extra = Math.Min(Core.SkillSteps - 1, 40); Core.SkillSteps = 1;
            Core.InSkillRepeat = true;
            Core.Guard("UpdateSkillAfterClean", () => { for (int i = 0; i < extra; i++) __instance.UpdateSkillAfterClean(success); });
            Core.InSkillRepeat = false;
        }
    }
}
