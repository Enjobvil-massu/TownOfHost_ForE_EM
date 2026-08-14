//using System;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Epic.OnlineServices.UI;
using TownOfHostForE;
using TownOfHostForE.Roles.Core;
using System.Data.Common;
using UnityEngine.UIElements;

namespace TownOfHostForE.Modules
{
    //参考:TOHY：MeetingDisplayText
    internal class MeetingDisplay
    {
        public static string reportInfo = "";

        public static void SetDisplayForClient(int targetNum,PlayerVoteArea pva)
        {

            if (targetNum == 0)
            {
                // MEETING_VERSION_FIXED_CARRIER_AB_V1:
                var versionVoteArea = Object.Instantiate(pva.NameText);
                versionVoteArea.transform.DestroyChildren();
                versionVoteArea.transform.SetParent(pva.NameText.transform);
                versionVoteArea.text =
                    $"<align=left><nobr><size=75%>{Main.ModNameRichText} <color=#ffffff>v{Main.PleviewPluginVersion}</color></size></nobr></align>";
                versionVoteArea.transform.localPosition = pva.NameText.transform.localPosition;
                versionVoteArea.transform.localPosition += new Vector3(-0.3f, 1.05f);
                versionVoteArea.gameObject.name = "MeetingVersionFixedCarrierABV1";

                if (!Options.ShowReportReason.GetBool()) return;
                var plusVoteArea = Object.Instantiate(pva.NameText);
                plusVoteArea.transform.DestroyChildren();
                plusVoteArea.transform.SetParent(pva.NameText.transform);
                (string text, bool kaigyou) = ButtonInfo();
                plusVoteArea.text = text;
                plusVoteArea.transform.localPosition = pva.NameText.transform.localPosition;
                float setY = kaigyou ? 0.5f : 0.6f;
                plusVoteArea.transform.localPosition += new Vector3(-0.3f, setY);

            }
            else if (targetNum == 1)
            {
                var plusVoteArea = Object.Instantiate(pva.NameText);
                plusVoteArea.transform.DestroyChildren();
                plusVoteArea.transform.SetParent(pva.NameText.transform);
                (string text, bool kaigyou) = SetMyRoleInfo(PlayerControl.LocalPlayer);
                plusVoteArea.text = text;
                plusVoteArea.transform.localPosition = pva.NameText.transform.localPosition;
                float setY = kaigyou ? 0.3f : 0.4f;
                plusVoteArea.transform.localPosition += new Vector3(-0.3f, setY);

            }
        }

        public static string SetDisplayForVanilla(PlayerControl seer,PlayerControl seen, string name, string suffix, string roleText, bool isMeeting)
        {
            if (!isMeeting) return name;

            // 表示するテキスト
            string addText = "";
            // 高さ調節用
            //int column = 0;
            float height = 2.0f;

            bool kaigyou = false;

            if (seen == Main.AllAlivePlayerControls.ElementAtOrDefault(0))
            {
                if (Options.ShowReportReason.GetBool())
                {
                    (addText, kaigyou) = ButtonInfo();
                }
                else
                {
                    // VANILLA_MEETING_VERSION_SLOT_AB_V1:
                    addText = "　\n";
                    kaigyou = true;
                }
            }
            else if (seen == Main.AllAlivePlayerControls.ElementAtOrDefault(1))
            {
                //(addText, column, height) = SetMyRoleInfo();
                (addText, kaigyou) = SetMyRoleInfo(seer);
            }

            // 既存MeetingDisplayの表示を、従来と同じ上下対称の補正で構築する。
            string result = name;
            if (addText != "")
            {
                float existingHeight = height;

                // 名前が数行になる時に一段ぶん少なくする
                if (roleText != "") existingHeight -= 0.5f;
                if (suffix != "") existingHeight -= 0.5f;

                string plusDisplay = $"<align={"left"}>{addText}</align>" +
                    $"<line-height={existingHeight}em>\n</line-height>";

                string adjust = $"<line-height={existingHeight}em>\n</line-height>";
                //for (int i = 0; i < column; i++) adjust += '\n';
                if (kaigyou) adjust += "\nㅤ";
                else adjust += "　";

                result = plusDisplay + name + adjust;
            }

            // VANILLA_HOST_MOD_VERSION_MEETING_DISPLAY_PERMANENT:
            // 完全バニラ端末が会議中にMODホストを見る場合だけ表示する。
            // 既存TargetNameへ直接voffset/posを混ぜず、MeetingDisplay自身が元から使っている
            // 「上側表示 + 同じ高さの下側補正」の対称構造で元の名前位置を保持する。
            // VANILLA_MEETING_VERSION_FIRST_ALIVE_AB_V1:
            if (seen == Main.AllAlivePlayerControls.ElementAtOrDefault(0) &&
                seer != PlayerControl.LocalPlayer &&
                !Main.playerVersion.ContainsKey(seer.PlayerId))
            {
                float modHeight = 3.0f;
                if (roleText != "") modHeight -= 0.5f;
                if (suffix != "") modHeight -= 0.5f;

                string modInfo =
                    $"<align=center><nobr><size=75%>{Main.ModNameRichText} <color=#ffffff>v{Main.PleviewPluginVersion}</color></size></nobr></align>";

                string modDisplay =
                    modInfo + $"<line-height={modHeight}em>\n</line-height>";

                string modAdjust =
                    $"<line-height={modHeight}em>\n</line-height>　";

                result = modDisplay + result + modAdjust;
            }

            return result;
        }


        public static void CreateButtonInfo(PlayerControl reporter, NetworkedPlayerInfo target)
        {
            var reporterInfo = Utils.GetPlayerInfoById(reporter.PlayerId);
            var reporterColor = reporterInfo.Color;

            // FIRSTTURN_VANILLA_REASON_CAUSEFIX_PERMANENT:
            // 初手強制会議ではIntroPatchがreporter.Data自身をsentinel targetとして渡す。
            // Vanilla向け表示はHost側reportInfoを使うため、表示時overrideだけではなく
            // reportInfo生成元でも初手強制会議を確定させる。
            if (Options.FirstTurnMeeting?.GetBool() == true &&
                Options.CurrentGameMode == CustomGameMode.Standard &&
                MeetingStates.FirstMeeting &&
                target != null &&
                target.PlayerId == reporter.PlayerId)
            {
                reportInfo = Utils.ColorString(Color.red, "初手強制会議");
                return;
            }

            //ボタンレポート
            if (target == null)
            {
                reportInfo = Utils.ColorString(reporterColor,"ボタン");
                return;
            }

            //死体がある場合
            string temp = Utils.ColorString(Color.white,"通報先：");

            temp += Utils.ColorString(target.Color,target.ColorName);

            reportInfo = temp;
        }

        private static (string,bool) ButtonInfo()
        {
            bool enterKaigYOU = false;

            // FIRSTTURN_MEETING_LABEL_PERMANENT:
            // FirstTurnMeetingInProgressはHost専用stateのため、MOD参加者との表示統一には使用しない。
            bool isFirstTurnForcedMeeting =
                Options.FirstTurnMeeting?.GetBool() == true &&
                Options.CurrentGameMode == CustomGameMode.Standard &&
                MeetingStates.FirstMeeting &&
                GameStates.IsMeeting;

            string displayInfo = isFirstTurnForcedMeeting
                ? Utils.ColorString(Color.red, "初手強制会議")
                : reportInfo;

            string returnString = $"<align={"left"}><size=80%>" + Utils.ColorString(Color.red, "『") + displayInfo + Utils.ColorString(Color.red, "』") + "</size></align>" + "\n";

            if (returnString.Length >= 12) enterKaigYOU = true;
            return (returnString,enterKaigYOU);
        }

        /// <summary>
        /// 自身のロール情報を画面に映す
        /// </summary>
        /// <param name="seer"></param>
        /// <returns>情報入り名称/改行ありか</returns>
        private static (string,bool) SetMyRoleInfo(PlayerControl seer)
        {
            string returnString = "";
            var cRole = seer.GetCustomRole();
            bool enterKaigYOU = false;

            if (!cRole.IsNotAssignRoles())
            {
                returnString = seer.GetRoleClass().MeetingInfo();
                if (returnString.Length >= 12) enterKaigYOU = true;
            }

            if (returnString != "")
            {
                returnString = $"<size=80%><align={"left"}>{returnString}</align></size>";
            }

            return (returnString,enterKaigYOU);
        }
    }
}
