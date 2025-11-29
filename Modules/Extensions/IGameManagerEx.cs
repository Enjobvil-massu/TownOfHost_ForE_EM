using AmongUs.GameOptions;

namespace TownOfHost_ForE.Modules.Extensions
{
    public static class IGameManagerEx
    {
        // Bool
        public static void Set(this BoolOptionNames name, bool value, NormalGameOptionsV10 opt)
            => opt.SetBool(name, value);

        public static void Set(this BoolOptionNames name, bool value, HideNSeekGameOptionsV10 opt)
            => opt.SetBool(name, value);

        // Int32
        public static void Set(this Int32OptionNames name, int value, NormalGameOptionsV10 opt)
            => opt.SetInt(name, value);

        public static void Set(this Int32OptionNames name, int value, HideNSeekGameOptionsV10 opt)
            => opt.SetInt(name, value);

        // Float
        public static void Set(this FloatOptionNames name, float value, NormalGameOptionsV10 opt)
            => opt.SetFloat(name, value);

        public static void Set(this FloatOptionNames name, float value, HideNSeekGameOptionsV10 opt)
            => opt.SetFloat(name, value);

        // Byte
        public static void Set(this ByteOptionNames name, byte value, NormalGameOptionsV10 opt)
            => opt.SetByte(name, value);

        public static void Set(this ByteOptionNames name, byte value, HideNSeekGameOptionsV10 opt)
            => opt.SetByte(name, value);

        // UInt32
        public static void Set(this UInt32OptionNames name, uint value, NormalGameOptionsV10 opt)
            => opt.SetUInt(name, value);

        public static void Set(this UInt32OptionNames name, uint value, HideNSeekGameOptionsV10 opt)
            => opt.SetUInt(name, value);
    }
}
