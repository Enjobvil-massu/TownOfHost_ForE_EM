# Town Of Host For E EM

**Town Of Host For E EM** は、Among Us 用ホストMOD **Town Of Host / TownOfHost_ForE** を基にした非公式Forkです。

このリポジトリは開発・検証中のソースコードを公開するためのものです。
Innersloth、Town Of Host開発者、TownOfHost_ForE開発者による公式版ではありません。

## Fork / Credits

本MODは以下のプロジェクトを基に開発しています。

- [Town Of Host](https://github.com/tukasa0001/TownOfHost)
- [TownOfHost_ForE](https://github.com/AsumuAkaguma/TownOfHost_ForE)

Town Of Host のFork Guidelineに従って開発・公開します。

- [このリポジトリ内のForkGuideline](./ForkGuideline.md)
- [Town Of Host Fork Guideline](https://github.com/tukasa0001/TownOfHost/blob/main/ForkGuideline.md)
- [Among Us Mod Policy](https://www.innersloth.com/among-us-mod-policy/)

元プロジェクトおよび関連MODの開発者・貢献者の皆様に感謝いたします。

## 対応環境

- Among Us v17.4s
- Windows版ホスト
- BepInEx / IL2CPP環境
- ホストのみMOD導入を基本とする構成
- 完全バニラのPC / Android等の参加者を想定
- 同一MODを導入したPC参加者についても互換性を確認しながら開発

このForkでは公開ルームを許可していません。

## 現在の開発方針

主な目的は、現行Among Usへの対応と、ホストMODとしての互換性・安定性改善です。

- ゲーム開始・役職配布
- MOD / Vanilla混在ロビー
- 会議・投票・追放
- AntiBlackout
- Ghost / Camera遷移
- Task同期
- Pet関連処理
- Vanilla向けtargeted RPC
- 1～15人での動作
- スマートフォン等の完全バニラ参加者との互換性

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

## 主な追加・調整内容

- Among Us v17.4s対応
- `/cmd` コマンド体系
- 会議・追放表示の互換性改善
- Vanilla参加者向け役職同期改善
- Task同期改善
- Pet / Rental Pet関連の互換性改善
- Airship Random Spawn対応
- God / 神
- UltraStar / ウルトラスター
- Scales / 天秤
- 各種役職・勝利条件・表示処理の修正
- 実プレイログを利用した不具合調査基盤

## 開発中機能について

このリポジトリには、実機検証中のA/B変更が含まれる場合があります。

- MODホスト追放時のVanilla Ghost遷移
- 通常Skip時の公式表示互換
- 会議中のMODバージョン表示位置
- MOD / Vanilla混在時の最終ランタイム回帰

そのため、GitHub上の最新ソースが常に安定版であるとは限りません。

## Releaseについて

現在は開発途中のため、**一般配布用の安定版DLL Releaseはまだ公開していません。**

ソースコードの公開と、ビルド済みDLLの一般配布は分けて管理します。

## 注意事項

- 本MODは非公式Forkです。
- 本Fork固有の問題をInnerslothや元MOD開発者へ問い合わせないでください。
- 公開ルームでは使用しないでください。
- Among Usや他プレイヤーに対する不正行為を目的とする機能は追加しません。
- 配布する場合はTown Of Host Fork GuidelineおよびAmong Us Mod Policyに従ってください。

## License

このリポジトリには既存プロジェクトの `LICENSE` を継承しています。

詳細は [LICENSE](./LICENSE) を確認してください。
