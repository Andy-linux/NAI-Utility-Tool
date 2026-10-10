using System;

namespace NAITool.Services;

/// <summary>Single-image pricing for the V3/V4/V5 models supported by this client.</summary>
internal static class NovelAiAnlasCalculator
{
    internal const int MaxBaseCost = 140;

    internal static bool IsV5Model(string model) => model is
        "nai-diffusion-5" or "nai-diffusion-5-full" or "nai-diffusion-5-curated" or
        "nai-diffusion-5-inpainting" or "nai-diffusion-5-full-inpainting" or
        "nai-diffusion-5-curated-inpainting" or "nai-diffusion-5-full-medium" or "nai-diffusion-5-full-medium-inpainting";

    internal static int PaidBaseCost(string model, int width, int height, int steps,
        bool sm = false, double strength = 1, bool smDyn = false)
    {
        if (width <= 0 || height <= 0 || steps <= 0)
            return int.MaxValue;

        long pixels = (long)width * height;
        // Official client: round the pixel/step formula first, then apply model,
        // SMEA and img2img strength factors, and finally round again.
        // https://novelai.net/_next/static/chunks/1601-acc34459bdce34e2.js
        double cost = Math.Ceiling(2.951823174884865e-6 * pixels +
                                  5.753298233447344e-7 * pixels * steps *
                                  (model.Contains("-medium", StringComparison.Ordinal) ? 1 / 1.06521739 : 1));
        bool supportsSm = ModelCapabilities.SupportsSmea(model);
        if (supportsSm) cost *= smDyn ? 1.4 : sm ? 1.2 : 1;
        if (IsV5Model(model)) cost *= 1.5;
        cost *= double.IsFinite(strength) ? Math.Clamp(strength, 0, 1) : 1;
        return (int)Math.Clamp(Math.Ceiling(cost), 2, int.MaxValue);
    }

    internal static int BaseCost(string model, int width, int height, int steps,
        bool activeOpus, bool? v5UsageIsNegative, bool sm = false, double strength = 1, bool smDyn = false)
    {
        int paidCost = PaidBaseCost(model, width, height, steps, sm, strength, smDyn);
        if (paidCost > MaxBaseCost) return paidCost;

        // Unknown V5 usage cannot establish free eligibility. A displayed 0% is
        // not evidence of exhaustion; use the provider's usage.isNegative state.
        bool free = activeOpus && steps <= 28 && (long)width * height <= 1048576 &&
                    (!IsV5Model(model) || v5UsageIsNegative == false);
        return free ? 0 : paidCost;
    }

    internal static int ReferenceCost(int unencodedVibes, int activeVibes, int preciseReferences) =>
        Math.Max(0, unencodedVibes) * 2 + Math.Max(0, activeVibes - 4) * 2 +
        Math.Max(0, preciseReferences) * 5;
}
