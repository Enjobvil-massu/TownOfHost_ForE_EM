# Town Of Host For E EM

**Town Of Host For E EM（TOH4E_EM）** は、Among Us 用ホストMOD **Town Of Host / TownOfHost_ForE** を基に、現行Among Usへの対応、ホストOnly環境での安定性、役職・機能追加を行っている非公式Forkです。

Innersloth、Town Of Host、TownOfHost_ForE、その他クレジット記載先の各開発者による公式版ではありません。  
本Fork固有の不具合や仕様について、元MODやInnerslothへ問い合わせないでください。

## 現在のソース

- TOH4E_EM source/tag: **v6190.421**
- GitHub branch: **main**
- Among Us: **2026.9.29 / v19**
- Platform: **Windows x64**
- Runtime: **BepInEx / IL2CPP**
- 基本運用: **ホストのみMODを導入**
- 参加者: PC / Android等の**完全バニラ参加者を想定**
- 公開ルーム: **使用しません**

> [!NOTE]
> GitHubの `main` / tag はソースコードの公開状態を示します。  
> ビルド済みDLLの配布・Updater公開状態とは別管理です。

## このForkの方針

TOH4E_EMでは、役職や機能を追加するだけでなく、ホストOnly MODとしてバニラ参加者と安定して遊べることを重視しています。

主な対象:

- ゲーム開始・役職配布
- MOD / Vanilla混在ロビー
- 会議・投票・追放
- AntiBlackout
- Ghost / Camera遷移
- Task同期
- Pet / Rental Pet関連
- Vanilla向けtargeted RPC
- 公式RPCとの互換性
- 1～15人での動作
- スマートフォン等の完全バニラ参加者との互換性
- Among Us更新時の互換性対応
- 実プレイログを利用した不具合調査

## v6190.421 時点の主な追加役職

### 配管工 / Plumber
清掃系の実タスクを1件ずつ進めるクルーメイト役職です。  
必要数を完了すると能力が解禁され、人外がベントから出た直後の視界妨害や、設定によって使用ベント位置の確認ができます。

Airshipでは有効な `VentCleaning` / `CleanToilet` から毎回独立してランダム選択します。

### リーヴァー / Reaver
ペット操作でエネルギービームをチャージし、左右移動で発射方向を決めるインポスター役職です。  
ビームは陣営を問わず命中対象になります。

### ジャッカルリーヴァー / JackalReaver
リーヴァー系のビーム能力を持つジャッカル陣営役職です。  
通常キルも使用しながらジャッカル陣営の勝利を目指します。

### イージス / Aegis
ペット操作でバリアを展開し、攻撃者が存在する攻撃を反射するクルーメイト役職です。  
ビーム反射時は反射後のビームを停止させる専用処理も持ちます。

### イビル真っ赤 / EvilDyer
キル成功時、倒された本人を除く生存者を一定時間、**赤色・帽子なし・スキンなし・バイザーなし・ペットなし**の姿にします。  
時間経過、通報、会議開始で元の姿へ戻ります。

TOH_Yの実装を参考・移植しつつ、TOH4E_EMではホストOnly / バニラ参加者向けに、標準の外見RPCを使用する方式へ再構成しています。

## その他の主な追加・調整

- Among Us v19対応
- `/cmd` コマンド体系
- 会議・追放表示の互換性改善
- Vanilla参加者向け役職同期改善
- Task同期改善
- Pet / Rental Pet関連の互換性改善
- Airship Random Spawn
- God / 神
- UltraStar / ウルトラスター
- Scales / 天秤
- HamburgerShop / ハンバーガー屋
- Renegade / 義賊
- v19公式Ghost Role互換対応
- Shared Beam系処理
- 各種役職・勝利条件・表示処理の修正
- 通信量やRPC競合を考慮した安定化
- 実プレイログを利用した診断基盤

## チャットコマンド

外部から使用するチャットコマンドは原則として `/cmd` を入口に統一しています。

```text
/cmd h
/cmd m
/cmd n
/cmd bt
```

Guess系コマンドは `/cmd bt` を使用します。  
`/h`、`/m`、`/n`、`/bt` 等を直接入力する旧形式は、外部入力用コマンドとしては使用しません。

## Fork / 移植・参考クレジット

TOH4E_EMは複数のAmong Us MODの成果を参考・移植して開発しています。  
元の実装をTOH4E_EM向けに変更・安定化している場合でも、由来のクレジットは維持します。

### ベース

- [Town Of Host](https://github.com/tukasa0001/TownOfHost)  
  本系統のFork元です。

- [TownOfHost_ForE](https://github.com/AsumuAkaguma/TownOfHost_ForE)  
  TOH4E_EMの直接のベースです。既存役職・各種機能・TOH4E固有処理を継承しています。

### Town Of Host_Y / TOH_Y

- [Town Of Host_Y](https://github.com/Yumenopai/TownOfHost_Y)
  - **神 / God**  
    TOH_Y v519.27.1の実装をTOH4Eへ移植・適応しています。  
    TOH_Y側のクレジットおよび移植監査では、神の元となる実装として **SuperNewRoles** が記録されています。
  - **イビル真っ赤 / EvilDyer**  
    TOH_YのEvilDyerを参考・移植し、TOH4E_EMのホストOnly構成、バニラRPC、連続発動・外見復元・通信負荷対策に合わせて再構成しています。
  - その他、TOH_Y由来の設定・役職・処理を参考にしている箇所があります。

### Town Of Host-K / TOH-K

- [Town Of Host-K](https://github.com/KYMario/TownOfHost-K)
  - **UltraStar / ウルトラスター** をクルーメイト役職として移植・適応しています。
  - **Scales / 天秤** をTOH4E方式へ限定移植し、Vanilla表示や使用回数などTOH4E_EM向けの処理を追加しています。
  - その他、ホストMODとしての表示・挙動を調査する際の参考実装として確認しています。

### SuperNewRoles / SNR

- [SuperNewRoles](https://github.com/SuperNewRoles/SuperNewRoles)
  - **HamburgerShop / ハンバーガー屋**  
    SNRの実装を直接参照し、TOH4E_EMのホストOnly構成へ局所移植しています。移植時の参照Commit: [e7bdfee5943c7b7fc2b298c80519aee5b595e445](https://github.com/SuperNewRoles/SuperNewRoles/commit/e7bdfee5943c7b7fc2b298c80519aee5b595e445)
  - **God / 神**  
    TOH4E_EMではTOH_Y実装を経由して移植していますが、TOH_Y側のクレジット上、元となる神の実装はSuperNewRoles由来です。
  - TownOfHost_ForE側から継承している機能・役職にも、SuperNewRolesを参考・移植したものがあります。

### その他の継承クレジット

TownOfHost_ForEおよび各参照MODがクレジットしているプロジェクトの成果も含まれています。

- [TownOfHostEdited](https://github.com/KARPED1EM/TownOfHostEdited)
- [TheOtherRoles](https://github.com/TheOtherRolesAU/TheOtherRoles)
- [ExtremeRoles](https://github.com/yukieiji/ExtremeRoles)
- [Nebula on the Ship](https://github.com/Dolly1016/Nebula)
- [DOVA-SYNDROME](https://dova-s.jp/)

すべての元プロジェクト、役職考案者、実装者、貢献者の皆様に感謝いたします。

## Fork Guideline / Mod Policy

Town Of Host のFork GuidelineおよびAmong Us Mod Policyに従って開発・公開します。

- [このリポジトリ内のForkGuideline](./ForkGuideline.md)
- [Town Of Host Fork Guideline](https://github.com/tukasa0001/TownOfHost/blob/main/ForkGuideline.md)
- [Among Us Mod Policy](https://www.innersloth.com/among-us-mod-policy/)

## Release / 配布について

ソースコードの公開と、ビルド済みDLLの配布は分けて管理しています。

- GitHub `main`: 現在のソース
- Git tag: 検証済みソースの節目
- DLL / Updater: 別の配布工程で管理
- GitHub Releases: 現在はバイナリ配布先として使用していません

そのため、GitHub上の最新Commitが、そのまま一般配布中DLLと同一バージョンであるとは限りません。

## 開発・検証について

このリポジトリでは安定性を優先し、変更内容によって以下を分離して確認しています。

- Source静的監査
- Build
- Warning差分
- DLL SHA-256
- Dev runtime配置
- Vanilla参加者との実機確認
- 会議 / Task / Pet / Ghost / RPC回帰

静的に成立していても、画面表示・音・ネットワーク挙動など実機確認が必要な項目は、実機確認前に「動作保証済み」とは扱いません。

## 注意事項

- 本MODは非公式Forkです。
- 本Fork固有の問題をInnerslothや元MOD開発者へ問い合わせないでください。
- 公開ルームでは使用しないでください。
- 公開ルームから非公開MOD部屋への勧誘など、バニラ利用者が意図せずMOD環境へ参加する可能性がある運用は行わないでください。
- Among Usや他プレイヤーに対する不正行為を目的とする機能は追加しません。
- 再配布・Forkを行う場合は、ライセンス、Fork Guideline、Among Us Mod Policy、および各移植元のクレジットを確認してください。

## License

このリポジトリは既存プロジェクトから継承した **GNU General Public License v3.0 (GPL-3.0)** の下で公開しています。

詳細は [LICENSE](./LICENSE) を確認してください。
