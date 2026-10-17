using System;

namespace NAITool.Services;

internal static class HighDraftRules
{
    internal static void ApplyProtectedStepLimit(NAIParameters parameters)
    {
        if (!ModelCapabilities.IsMedium(parameters)) parameters.Steps = Math.Min(parameters.Steps, 28);
    }
}
