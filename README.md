# Motion Take Studio

[![Unity Tests](https://github.com/ChanyaVRC/MotionTakeStudio/actions/workflows/unity-tests.yml/badge.svg?branch=main)](https://github.com/ChanyaVRC/MotionTakeStudio/actions/workflows/unity-tests.yml)

SteamVR/OpenVRのトラッキングから、Play ModeのHumanoidアバターを収録・レビューし、全身IK補正付きAnimation Clipを生成するUnity Editor拡張です。NDMFは任意で、未導入でも通常のHumanoid Cloneを収録できます。

パッケージID: `com.buildsoft.motion-take-studio`

## VCC / VPMからインストール

1. VCCの `Settings > Packages` で次のRepository URLを追加します。

   `https://chanyavrc.github.io/MotionTakeStudio/index.json`

2. 対象プロジェクトの `Manage Project` を開きます。
3. `Motion Take Studio` を追加します。

現在、VCCからインストールできます。

## 開発

パッケージ本体は [`Packages/com.buildsoft.motion-take-studio`](Packages/com.buildsoft.motion-take-studio) にあります。

- [パッケージREADME](Packages/com.buildsoft.motion-take-studio/README.md)
- [導入・記録・レビューガイド](Packages/com.buildsoft.motion-take-studio/Documentation~/GettingStarted.md)
- [アーキテクチャ](Packages/com.buildsoft.motion-take-studio/Documentation~/Architecture.md)
- [APIリファレンス](Packages/com.buildsoft.motion-take-studio/Documentation~/APIReference.md)

Pull RequestではSecret不要のCI契約テストを実行します。`main`とReleaseでは、main限定の
`unity-ci` EnvironmentからUnityライセンスを受け取り、VRChat SDK／NDMFを含まない一時Unity Projectで
Editor 95件とRuntime PlayMode 2件を自動実行します。PR merge後のmain検証が失敗した場合は修正PRを作成し、
ReleaseはUnityテストに成功しない限り公開されません。

## ライセンス

[BSD-3-Clause](Packages/com.buildsoft.motion-take-studio/LICENSE.md)
