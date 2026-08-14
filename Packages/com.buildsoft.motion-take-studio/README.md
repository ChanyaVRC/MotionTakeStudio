# Motion Take Studio

Motion Take Studio は、Unity の Play Mode で Humanoid アバターを記録し、
Scene View でポーズ補正を確認して、バージョン付きの Humanoid
`AnimationClip` として保存する Unity Editor パッケージです。

> 現在のバージョンは **0.1.1** です。実運用前に、下記の「現在の制限」と
> [トラブルシューティング](Documentation~/Troubleshooting.md)を確認してください。

## 動作条件

- Unity 2022.3 以降
- 有効な Humanoid Avatar を持つ、シーン上の `Animator`
- Unity Animation Rigging 1.2.1（パッケージ依存関係として宣言済み）
- NDMF は任意で、自動導入されません。NDMF と **Apply on Play** を安全に利用できる場合は
  処理後の Avatar を収録し、利用できない場合は通常の Humanoid Clone を収録します。
- 既定のトラッキングには、起動中の SteamVR と SteamVR Unity Plugin（OpenVR）が必要。
  `ITrackerPoseProvider` を差し替える場合、OpenVR は不要です。
- VRChat SDK、Modular Avatar、VRCFury、NDMF は任意です。NDMF と OpenVR は基本アセンブリから
  Reflection で利用するため、コンパイル時・パッケージ依存はありません。

## インストールと起動

通常は VCC の **Settings > Packages** へ次の VPM Repository URL を追加し、
対象プロジェクトの **Manage Project** から **Motion Take Studio** を追加します。

`https://chanyavrc.github.io/MotionTakeStudio/index.json`

開発時は Unity の Package Manager で **Add package from disk...** を選び、このフォルダーの
`package.json` を指定できます。埋め込みパッケージとして使う場合は、プロジェクトの
`Packages/com.buildsoft.motion-take-studio` に配置してください。

インストール後、**Tools > BuildSoft > Motion Take Studio** を開きます。

## 最短の記録手順

1. Edit Mode で、シーン上の Humanoid `Animator` を選択します。
2. **Prepare Play Capture** を押し、状態が **Ready** になるまで待ちます。
3. **Record** を押して動きを記録します。
4. 少なくとも 1 フレーム記録してから **Stop & Review** を押します。
5. Review 中にフレームをスクラブし、Scene View のハンドルで補正します。
6. **Save & Exit** を押します。Edit Mode に戻ると、Take、Recipe、検証レポート、
   Auto／Corrected／Manual のクリップが生成されます。

詳しい手順は [導入・記録・レビューガイド](Documentation~/GettingStarted.md)を参照してください。

## 生成されるデータ

- `Assets/MotionTakeStudio/Takes/*.mttake`: トラッカー、IK ステージ、Auto 解決後 HumanPose のサンプル
- `Assets/MotionTakeStudio/Takes/*_recipe_vNN.asset`: 非破壊の補正キー
- `Assets/MotionTakeStudio/Takes/*_validation_vNN.asset`: タイムライン検証結果
- `Assets/MotionTakeStudio/Clips/*_auto_vNN.anim`: 記録済み自動処理結果
- `Assets/MotionTakeStudio/Clips/*_corrected_vNN.anim`: 手動補正を反映した再評価結果
- `Assets/MotionTakeStudio/Clips/*_manual_vNN.anim`: Corrected の編集用コピー

既存のクリップは上書きされません。Manual クリップを選択し、
**Assets > BuildSoft > Open Clip in Animation Window** から Unity の Animation
ウィンドウで編集できます。Manual 側の変更は、後の再ベイクへ逆流しません。

## ドキュメント

- [導入・記録・レビューガイド](Documentation~/GettingStarted.md)
- [トラブルシューティング](Documentation~/Troubleshooting.md)
- [API・データモデル](Documentation~/APIReference.md)
- [アーキテクチャ](Documentation~/Architecture.md)

## 自動テスト（開発者向け）

自動テストは、Editor API、書き出し、補正ロジックを確認する
`BuildSoft.MotionTakeStudio.Editor.Tests` と、実際に Editor の Play Mode へ入って Runtime の
フレーム進行を確認する `BuildSoft.MotionTakeStudio.PlayMode.Tests` の 2 層です。
**Window > General > Test Runner** で EditMode と PlayMode の両タブを個別に実行してください。

Unity 2022.3.22f1 で、Editor suite は **95 / 95**、Runtime PlayMode suite は
**2 / 2** の成功を確認しています。Editor suite には、生成 Humanoid を使って実際に
Play Mode へ入り、NDMF なしの 6 点 Capture、Review、左肘 Hint の 10 cm 補正、Validation、Clip の
再生確認までを通す E2E と、任意 Processor の完了通知ゲートを確認する E2E を含みます。

パッケージの対応範囲はUnity 2022.3のままで、standalone repositoryの`ProjectVersion.txt`は
Unity 2022.3.22f1を維持します。
Unity Build Automationで22f1が廃止対象になったため、cloud UBA targetだけは2022.3.40f1を明示使用します。
これは利用側projectの最低／推奨Editorを40f1へ変更するものではなく、上記95件／2件は22f1で確認した基準値です。

埋め込みパッケージを含むプロジェクトルートでは、次の runner で両 assembly を逐次実行できます。

```powershell
pwsh -File "Packages/com.buildsoft.motion-take-studio/Tools~/Run-MotionTakeStudioTests.ps1"
```

[テスト runner](Tools~/Run-MotionTakeStudioTests.ps1)は Unity を起動する前に自身の XML 契約を
自己テストし、EditMode と PlayMode を `-nographics` で逐次実行します。各 Unity process の timeout は
既定 15 分です。Unity の exit code に加え、各 NUnit XML に対象 assembly と必須 Integration test の
fullname があり、EditModeは95件以上、PlayModeは2件以上で、対象 assembly 自身が`passed == total`、
`failed == skipped == inconclusive == 0`、`result == Passed`、かつ run と assembly の `total` が
一致することを検査します。そのため、0 件 run、一部 skip、別 assembly の混在、必須 E2E の未実行は
成功扱いになりません。
`Add package from disk...`、Git、registry から導入した非 embedded package のテストを実行する場合は、
導入先プロジェクトの `Packages/manifest.json` に
`"testables": ["com.buildsoft.motion-take-studio"]` も追加し、runner へ `-ProjectPath` を明示してください。
詳しい GUI／CLI 手順は[導入・記録・レビューガイド](Documentation~/GettingStarted.md)にあります。

GitHub ActionsではPull Requestに対してSecret不要のrunner／workflow契約だけを検証し、保護された`main`への
pushまたはmainからの手動実行だけがUnity Build Automation v2のWindows Micro targetで両Unity suiteを実行します。targetはAuto-buildと
player exportを無効にしたunit-test-only／Content-only構成です。ReleaseはUBAを再実行せず、正規workflowの
`push/main`、完全一致commit SHA、最新attemptの3必須job、唯一の未失効artifactをread-only APIで確認します。
取得したEditMode／PlayMode XMLは上記runnerの`-ValidateResultsOnly`で再検証され、0件、skip、inconclusive、assembly不一致、
必須E2E欠落、要求SHAとbuild SHAの不一致があればReleaseも失敗します。GitHubへUnity accountのlogin、
password、licenseファイルは保存しません。詳細は
[導入・記録・レビューガイド](Documentation~/GettingStarted.md#github-actions-と-unity-build-automation-v2)を参照してください。

この自動テストは、実機の SteamVR／OpenVR デバイス列挙や、任意の NDMF Apply on Play の処理結果までは
保証しません。リリース前には、対応機器と実アバターを使った標準 Capture フローも別に確認してください。

## 現在の制限

- **Ready** で表示される Tracker Roles からシリアル番号と身体ロールを確認・変更できます。
  HMD と左右コントローラー以外は暫定割り当てなので、録画前に明示設定してください。
- Record 開始には、使用中の Provider が有効な Head／LeftHand／RightHand を返す必要があります。
  追加 Tracker は 6 点・11 点を含め任意ですが、Generic Tracker の装着位置は自動判定しません。
- Recipe 編集後の検証は補正済みの全フレームを再評価し、非有限値、Root 急変、床貫通、
  足滑り、IK 到達不能、関節の単一フレーム反転を表示します。これは閾値ベースの診断であり、
  生成 Clip が全アバターで破綻しないことを保証するものではありません。
- Head／Hips の大きな補正は全身へ連鎖するため、生成した Corrected Clip を必ず実アバターで
  再確認してください。
- 指、表情、PhysBone、音楽同期、実行中の VRChat クライアント内での同時記録、
  カスタムカーブエディターは対象外です。
- Recovery Journal を一覧・復元する専用 UI はありません。復元 API は Editor 向けに
  公開されています。

## ライセンス

BSD-3-Clause. Copyright (c) 2026 BuildSoft.
