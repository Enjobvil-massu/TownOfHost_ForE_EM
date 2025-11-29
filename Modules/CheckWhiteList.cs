using System;
using System.Collections.Generic;

namespace TownOfHostForE.Modules
{
    /// <summary>
    /// ForhiteListEngine.dll が無い環境でもビルドできるようにした簡易ホワイトリスト
    /// ※ WhiteListFriendCodes に friendCode を入れた場合のみホワイトリストが有効化されます
    /// </summary>
    internal static class CheckWhiteList
    {
        // ✅ ここに許可したい friendCode を入れる（空なら無効＝全員許可）
        // 例: "ABCDEF"
        private static readonly HashSet<string> WhiteListFriendCodes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                // "AAAAAA",
                // "BBBBBB",
            };

        public static bool CheckWhiteListData()
        {
            try
            {
                var code = EOSManager.Instance?.friendCode;

                // friendCode が取れない場合も “落とさない” で許可（fail-open）
                if (string.IsNullOrWhiteSpace(code))
                {
                    Main.Logger?.LogInfo("[WhiteList] friendCode が取得できないためホワイトリストチェックをスキップします");
                    return true;
                }

                // リスト未設定なら whitelist 無効＝全員許可
                if (WhiteListFriendCodes.Count == 0)
                {
                    Main.Logger?.LogInfo("[WhiteList] ホワイトリスト未設定のためチェックをスキップします（全員許可）");
                    return true;
                }

                bool result = WhiteListFriendCodes.Contains(code);
                Main.Logger?.LogInfo($"[WhiteList] チェック結果：{result} / code={code}");
                return result;
            }
            catch (Exception e)
            {
                Main.Logger?.LogError($"[WhiteList] チェック中に例外: {e}");
                return true; // 例外時も許可して落とさない
            }
        }
    }
}
