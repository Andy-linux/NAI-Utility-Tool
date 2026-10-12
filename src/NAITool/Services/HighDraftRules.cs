using System;
using System.Collections.Generic;
using System.Linq;

namespace NAITool.Services;

internal static class HighDraftRules
{
    internal static string TransformNegative(NAIParameters parameters, string negative, Func<string, string> transform) =>
        ModelCapabilities.IsMedium(parameters) ? negative : transform(negative);

    internal static string[] CaptureActiveNegatives(IEnumerable<(bool Disabled, string Negative)> characters) =>
        characters.Where(character => !character.Disabled).Select(character => character.Negative).ToArray();

    internal static void ApplyProtectedStepLimit(NAIParameters parameters)
    {
        if (!ModelCapabilities.IsMedium(parameters)) parameters.Steps = Math.Min(parameters.Steps, 28);
    }
}
