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

Pull RequestではSecret不要のCI契約テストを実行します。保護された`main`へのpushまたはmainからの手動実行だけが、main限定の
`unity-ci` Environmentに置いたサービスアカウントでUnity Build Automation v2を呼び出し、
repository projectはUnity 2022.3.22f1を維持し、cloud CIだけがWindows Micro上の
Unity 2022.3.40f1でEditMode／PlayModeを実行します。ReleaseはUBAを重複実行せず、
同じcommit SHAの正規`main` push run、3つの必須job、未失効artifactを照合してNUnit XMLを再検証します。
Unity login、password、licenseファイルはGitHubへ保存しません。パッケージの対応範囲はUnity 2022.3のままで、
40f1は22f1がUBAの廃止対象になったことに対応するUBA target専用Editorです。PR merge後のmain検証が失敗した場合は
修正PRを作成し、ReleaseはUnityテストに成功しない限り公開されません。

## ライセンス

[BSD-3-Clause](Packages/com.buildsoft.motion-take-studio/LICENSE.md)
