using System;
using TownOfHostForE.Roles.Core;

namespace TownOfHostForE
{
    public class FloatOptionItem : OptionItem
    {
        // 必須情報
        public FloatValueRule Rule;
        public int UiStepMultiplier { get; private set; } = 1;
        public int LegacySavedIndexMultiplier { get; private set; } = 1;

        public FloatOptionItem SetUiStepMultiplier(int value)
        {
            UiStepMultiplier = value < 1 ? 1 : value;
            return this;
        }
        public FloatOptionItem SetLegacySavedIndexMultiplier(int value)
        {
            LegacySavedIndexMultiplier = value < 1 ? 1 : value;
            return this;
        }

        // コンストラクタ
        public FloatOptionItem(int id, string name, float defaultValue, TabGroup tab, bool isSingleValue, FloatValueRule rule)
        : base(id, name, rule.GetNearestIndex(defaultValue), tab, isSingleValue)
        {
            Rule = rule;
        }
        public static FloatOptionItem Create(
            int id, string name, FloatValueRule rule, float defaultValue, TabGroup tab, bool isSingleValue
        )
        {
            return new FloatOptionItem(
                id, name, defaultValue, tab, isSingleValue, rule
            );
        }
        public static FloatOptionItem Create(
            int id, Enum name, FloatValueRule rule, float defaultValue, TabGroup tab, bool isSingleValue
        )
        {
            return new FloatOptionItem(
                id, name.ToString(), defaultValue, tab, isSingleValue, rule
            );
        }
        public static FloatOptionItem Create(
            SimpleRoleInfo roleInfo, int idOffset, Enum name, FloatValueRule rule, float defaultValue, bool isSingleValue, OptionItem parent = null
        )
        {
            var opt = new FloatOptionItem(
                roleInfo.ConfigId + idOffset, name.ToString(), defaultValue, roleInfo.Tab, isSingleValue, rule
            );
            opt.SetParent(parent ?? roleInfo.RoleOption);
            return opt;
        }

        // Getter
        public override int GetInt() => (int)Rule.GetValueByIndex(CurrentValue);
        public override float GetFloat() => Rule.GetValueByIndex(CurrentValue);
        public override string GetString()
        {
            var value = Rule.GetValueByIndex(CurrentValue);

            // MADMATE_VENT_MAX_INFINITY_DISPLAY_V1:
            // DEVICE_TIME_LIMIT_PORT_V21:
            // Keep zero as the stored value. For these options only,
            // zero means unlimited and is rendered as infinity.
            if ((Id == 91529 ||
                 Id == 100092 ||
                 Id == 100093 ||
                 Id == 100094 || Id == 41816) &&
                value <= 0f)
            {
                return "\u221E";
            }

            return ApplyFormat(value.ToString());
        }
        public override int GetValue()
            => Rule.RepeatIndex(base.GetValue());

        // Setter
        public override void SetValue(int value, bool doSync = true)
        {
            base.SetValue(Rule.RepeatIndex(value), doSync);
        }
    }
}