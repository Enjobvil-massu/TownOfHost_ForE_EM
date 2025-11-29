using System.Collections.Generic;

namespace TownOfHostForE.Modules.Extensions
{
    internal static class Il2CppListEx
    {
        // Il2CppSystem.Collections.Generic.List<T> を IEnumerable<T> に変換
        public static IEnumerable<T> AsEnumerable<T>(this Il2CppSystem.Collections.Generic.List<T> list)
        {
            if (list == null) yield break;

            for (int i = 0; i < list.Count; i++)
                yield return list[i];
        }
    }
}
