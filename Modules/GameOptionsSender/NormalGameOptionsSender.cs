using AmongUs.GameOptions;

namespace TownOfHostForE.Modules
{
    public class NormalGameOptionsSender : GameOptionsSender
    {
        public override IGameOptions BasedGameOptions =>
            GameOptionsManager.Instance.CurrentGameOptions;
        public override bool IsDirty
        {
            get
            {
                var logicComponents = GameManager.Instance?.LogicComponents;
                if (logicComponents == null)
                {
                    return false;
                }

                if (_logicOptions == null || !logicComponents.Contains(_logicOptions))
                {
                    _logicOptions = null;

                    foreach (var glc in logicComponents)
                    {
                        if (glc.TryCast<LogicOptions>(out var lo))
                        {
                            _logicOptions = lo;
                            break;
                        }
                    }
                }

                return _logicOptions != null && _logicOptions.IsDirty;
            }
            protected set
            {
                if (_logicOptions != null)
                    _logicOptions.ClearDirtyFlag();
            }
        }
        private LogicOptions _logicOptions;

        public override IGameOptions BuildGameOptions()
            => BasedGameOptions;
    }
}