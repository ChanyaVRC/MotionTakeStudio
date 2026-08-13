# トラブルシューティング

## ウィンドウが Capture に接続されない

- Console のコンパイルエラーを先に解消してください。
- パッケージが Package Manager に表示され、`BuildSoft.MotionTakeStudio.Editor` が
  Editor 向けにロードされていることを確認します。
- ウィンドウを閉じ、**Tools > BuildSoft > Motion Take Studio** から開き直します。

## Prepare Play Capture が押せない

- Edit Mode で操作してください。
- Prefab アセットではなく、開いているシーン上の `Animator` を指定してください。
- Avatar が Valid かつ Humanoid であることを Rig Import Settings で確認します。
- セッションが Idle または Error 以外なら、先に Cancel してください。

## Emulator 競合エラーが出る

Lyuma Av3 Emulator または Gesture Manager の有効な Component が見つかると Capture を
停止します。対象 Component または GameObject を無効にしてから、Prepare をやり直してください。
これらのツールが作る Mirror／Shadow Clone と Animator の再生成を誤って記録しないためです。

## Ready にならない

- 一時 Capture Scene のアバターに、有効な Humanoid Animator と必要 Bone が残っているか
  Console の警告を確認します。
- Status が任意 Processor の完了待ちを示す場合は、NDMF の **Apply on Play** と VRChat SDK Hook が
  有効か確認します。処理を開始していない場合は、通常の Humanoid Clone で Ready になります。
- NDMF / VRChat SDK などが Animator を再生成している間は待機します。安定した同じ Animator と
  Bone Map が 2 フレーム続くと Ready になります。
- 外部 Processor が毎フレーム Avatar を作り直す場合は、その処理を停止してください。

## OpenVR が見つからない

- SteamVR Unity Plugin がプロジェクトへ導入されているか確認します。
- SteamVR を起動し、HMD とコントローラーが認識された状態にします。
- `Valve.VR.OpenVR` 型がロードされていないと既定 Provider は利用できません。
- Record は Provider の件数ではなく、Role が割り当てられた有効な Head／LeftHand／RightHand を
  確認します。3 台見えていても Role や Pose が不正なら開始しません。

独自の `ITrackerPoseProvider` を `SetTrackerProvider` で注入する場合、OpenVR は不要です。
ただし有効な Head／LeftHand／RightHand という Record 条件は変わりません。NDMF は任意です。

## Generic Tracker の身体部位が違う

Generic Tracker の自動ロールは装着位置推定ではなくデバイス順です。Ready 後にウィンドウの
**Refresh Tracked Devices** を押し、シリアル番号ごとにロールを選んでください。

標準的な 6 点構成でも自動割り当てを信用しないでください。明示割り当てをしない場合、
Generic Tracker 3 台は Waist／LeftFoot／RightFoot の暫定ロールになります。

## Raw オーバーレイが表示されない

Raw は次の条件をすべて満たすサンプルだけを表示します。

- PoseTarget に対応する TrackerRole がある
- Provider が接続済み・有効なサンプルを返した
- Capture Rig のキャリブレーションが完了した

無効、切断、未割り当ての Tracker は表示されません。

## IK と Auto、または Auto と Manual が重なる

各表示は別ステージです。IK は Tracker IK 直後、Auto は Filtering／Root Jump 補正／Foot Lock 後、
Manual は Recipe 適用後の実際の Solver 結果を描画します。対象フレームで自動補正が働かなければ
IK と Auto、Recipe 差分がなければ Auto と Manual が同じ結果になるため、重なること自体は正常です。

常に同じに見える場合は別フレームへ移動し、Manual では対象 Target に補正キーが評価されているか、
IK と Auto では足固定や大きな Root Jump が発生する区間かを確認してください。

## Stop & Review 後にハンドルが出ない

Scene View を再描画してください。Recipe が存在し、
対象 Bone を持つ Humanoid であることも確認します。ElbowHint と KneeHint は位置ハンドルだけです。

## 肘を動かすと Hand もずれる

Two-Bone IK は到達可能な範囲で End Target を固定します。次を確認してください。

- Hand Target が Limb 長より遠くないか
- Hint が Limb 軸上や Root／Joint と同一点になっていないか
- Scene View またはウィンドウに Clamp / invalid input 警告が出ていないか

補正量を減らし、問題のフレーム周辺を前後にスクラブして Bend の連続性を確認してください。
Avatar に明示された HumanBone 軸レンジは曲げ上限へ反映されますが、複合軸制限までは再現しません。

## Head / Hips 補正が書き出し後に違う

Head は Spine／Neck 連鎖を解決します。Hips は Hips Bone を直接補正し、接地判定なしで両足を
記録済みベース位置へ固定して脚を再解決します。Animator Root 自体を直接補正する処理ではありません。
一方、クリップは HumanPose Muscle と RootT／RootQ のカーブだけで保存します。特に大きな Head 位置補正は
同じ見た目でベイクされない可能性があります。Corrected クリップを対象 Humanoid へ適用して
確認し、必要なら Manual コピーを Animation ウィンドウで調整してください。

## 床貫通・足滑りが Validation に出ない

対象 Humanoid に左右 Foot Bone がない場合は足位置を評価できません。標準 Validation Source は
Capture Rig の接地高さを床として扱うため、
床原点が異なる Scene では結果が不正確です。

Recipe 編集時にも Validation は再実行されます。補正済み全フレームを先頭から再生し、
Root／Muscle、左右 Foot、各フレームの Manual IK 警告、
肘・膝の Bend Direction を検査します。検証後は元のスクラブフレームへ戻ります。

床関連だけが出ない場合は Foot Bone と床高に加え、既定の接地高・貫通量・滑り速度の閾値を
超えているか確認してください。Joint Flip は連続フレームの Bend Direction が既定 120 度以上
変化したときに警告します。

## Save & Exit で書き出せない

- 0 フレーム Take はベイクできません。短くても Record 後にサンプルが増えたことを確認します。
- Console の最初の例外を確認します。
- `Assets/MotionTakeStudio/Takes` と `Assets/MotionTakeStudio/Clips` が作成可能か確認します。
- 失敗時の Pending Export は `Library/MotionTakeStudio/Recovery` に残ります。

書き出し途中の失敗では Recipe／Validation の途中アセットをロールバックし、Pending Export を
再試行可能な状態で残します。

## クラッシュ後に記録を探したい

`Library/MotionTakeStudio/Recovery` をバックアップしてください。`.jsonl` は append-only Journal、
`.review-checkpoint.json` は Capture／Recipe／スクラブ位置を持つ Review 復元点、
`.pending-export.json` は Play Mode 終了後の書き出し待ちデータです。

専用 Recovery UI はありません。Editor API の `MotionTakeRecovery.FindAll()` で Journal を列挙し、
`TryLoad` で `CaptureTake` として読み出せます。復元処理を実装するまでは、元ファイルを移動・削除
しないでください。

## Test Runner に package test が表示されない

- `Packages/com.buildsoft.motion-take-studio` 直下の embedded package なら、Console のコンパイルエラーを
  解消し、package を Reimport してください。
- `Add package from disk...`、Git、registry から導入した非 embedded package では、project の
  `Packages/manifest.json` へ `"testables": ["com.buildsoft.motion-take-studio"]` を追加します。
- EditMode の assembly 名は `BuildSoft.MotionTakeStudio.Editor.Tests`、PlayMode は
  `BuildSoft.MotionTakeStudio.PlayMode.Tests` です。前者だけが見える場合、PlayMode test asmdef が
  import されているか、Test Runner の PlayMode タブを確認してください。
- `testables` の変更が即時反映されない場合は package を再 Import するか Unity を開き直します。

## CLI テストが 0 件または XML エラーで止まる

[テスト runner](../Tools~/Run-MotionTakeStudioTests.ps1)は、Unity が exit code 0 を返しても XML の
run と対象 assembly の `total` が違う、対象 assembly 自身が全件成功していない、または必須
Integration test の fullname が `Passed` でない場合は意図的に失敗します。これは未発見、一部未完走、
別 assembly の混在、必須 E2E の未実行を成功扱いしないためです。次を確認してください。

- 上記の assembly 名と `testables` 設定が正しい
- 同じ `ProjectPath` を別の Unity process や CI job が同時に開いていない
- `-UnityPath` が project の `ProjectVersion.txt` と互換な Unity Editor を指している
- `Library/MotionTakeStudio/TestResults/EditMode.log` と `PlayMode.log` の最初のエラーを確認した
- runner の Unity 引数へ `-quit` や `-runSynchronously` を追加していない
- GPU のない CI では、runner が自動指定する `-nographics` を削除していない
- 既定の 15 分で不足する環境では `-TestTimeoutMinutes` を 1〜120 の範囲で増やした

XML と log が生成されない場合は、明示的な `-UnityPath` と書き込み可能な `-ResultsDirectory` を
指定して再実行してください。

package が Unity project の外にある場合、自動 project 探索はできません。外部 package 内の script を
実行するときに `-ProjectPath "<導入先 Unity project>"` を明示してください。

Unity を起動する前に runner 自体を切り分けるには、次の自己テストだけを実行します。

```powershell
pwsh -File "<package>/Tools~/Run-MotionTakeStudioTests.ps1" -SelfTest
```

`Runner contract self-tests passed.` と表示されれば、XML 欠落・破損、0 件、failed／skip／inconclusive、
対象 assembly と必須 test の不一致を判定する runner 側の契約は動作しています。
Unity 2022.3.22f1 での全体基準は Editor 95 / 95、Runtime PlayMode 2 / 2 です。件数が異なる場合は
XML 内の assembly 名、test fullname、Test Runner のフィルターを確認してください。

### GitHub ActionsでUnity資格情報不足になる

次の3つがrepository Secretではなく、`main`限定の`unity-ci` Environment Secretへ登録されているか
確認します。すべて同じ専用Unity CIアカウントのものにします。

- `UNITY_LICENSE`: [GameCI Activation](https://game.ci/docs/github/activation/)で取得した`.ulf`の全文
- `UNITY_EMAIL`: その専用アカウントのlogin email
- `UNITY_PASSWORD`: その専用アカウントのpassword

`UNITY_SERIAL`をSecretへ追加しないでください。信頼済みhost wrapperが`UNITY_LICENSE`の
`DeveloperData`から導出し、完全な値とUnity表示の末尾`XXXX`形式をlog maskへ登録します。
`DeveloperData did not contain a valid 27-character serial`で失敗する場合は、
`.ulf`のコピー欠落、専用アカウントとlicenseの不一致、license更新後のSecret更新漏れを確認します。
raw ULFは意図的にUnity test containerへ渡しません。containerには導出した`UNITY_SERIAL`、
`UNITY_EMAIL`、`UNITY_PASSWORD`だけがDockerの`--env NAME`で渡ります。

`unity-ci` Environmentのdeployment branchが`main`のみであること、branch protectionが有効であることも
[GitHub公式のEnvironment手順](https://docs.github.com/en/actions/how-tos/deploy/configure-and-manage-deployments/manage-environments)
に沿って確認します。Pull Requestでは意図的にUnityを起動せず、`CI Contract`だけを実行します。

### Unity activationの詳細がlogに出ない

資格情報の誤出力を防ぐため、activation／return launcherのstdout／stderrと一時license logの内容は
意図的に抑止しています。詳細logをworkflowへ出す変更は行わず、次の順に確認します。

1. 専用アカウントのemailとpasswordでUnityへloginできるか。
2. `UNITY_LICENSE`、`UNITY_EMAIL`、`UNITY_PASSWORD`が同じアカウントの値か。
3. license更新やpassword変更後に`unity-ci` Environment Secretも更新したか。
4. 専用アカウントの利用可能なseatが残っているか。

### 強制キャンセル後にUnity seatが残った

workflowは`cancel-in-progress: false`で、通常終了、test失敗、host／containerのhandled signalでlicense returnを
試み、成功logまたはlocal license artifactの削除を確認します。
ただしGitHub上での強制キャンセル、runner喪失、container／processの強制終了ではreturnが完了しない
場合があります。別のrunを開始せず、実行中のUnity workflowがないことを確認してから、
[GameCIのlicense return手順](https://game.ci/docs/github/returning-a-license/)とUnityのlicense管理画面を確認します。
GameCIが案内するmanual returnはProfessional license用です。Professionalでは同手順、それ以外のlicense種別では
Unityのlicense管理画面またはSupportが案内する方法で、専用Unity CIアカウントのseatを手動回復します。

## 自動テストが通るが OpenVR / 任意 NDMF の実機 Capture が動かない

自動 PlayMode テストは Runtime の Player loop を確認するもので、実際の SteamVR／OpenVR デバイス、
ドライバー、USB 接続、tracker role、任意の NDMF Apply on Play の処理結果は対象外です。自動テストとは別に、
SteamVR を起動した対応 PC で本書の Ready／Record 条件を確認してください。NDMF、OpenVR、実アバターを
使う検証が失敗した場合は、このページの **Ready にならない**、**OpenVR が見つからない**、
**Generic Tracker の身体部位が違う**の順に切り分けます。

戻る: [導入・記録・レビューガイド](GettingStarted.md)
