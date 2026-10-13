using System;

namespace TownOfHostForE.Modules;

internal static class ReaverOptionMigration
{
    // Schema 3/V3: preserve seconds, rounded to the nearest available whole second.
    internal static int FromPreviousIndex(int id, int index) => id switch
    {
        61010 => Math.Clamp((int)Math.Round(2.5 + index * 0.5, MidpointRounding.AwayFromZero), 1, 180) - 1,
        61012 => Math.Clamp(index * 5, 0, 60),
        _ => index
    };
}
