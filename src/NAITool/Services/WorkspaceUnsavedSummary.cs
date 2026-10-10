using System.Collections.Generic;

namespace NAITool.Services;

internal static class WorkspaceUnsavedSummary
{
    internal static string[] Collect(bool i2i, bool upscale, bool effects)
    {
        var names = new List<string>();
        if (i2i) names.Add("mode.i2i");
        if (upscale) names.Add("mode.upscale");
        if (effects) names.Add("mode.post");
        return names.ToArray();
    }
}
