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

### GitHub ActionsでUBA認証または設定エラーになる

GitHubの`Settings > Environments > unity-ci`に次の5項目があるか確認します。repository Secret／Variableでは
なく、deployment branchを`main`へ限定したEnvironmentへ置きます。

- Secret `UNITY_UBA_KEY_ID`
- Secret `UNITY_UBA_SECRET_KEY`
- Variable `UNITY_UBA_ORG_ID`
- Variable `UNITY_UBA_PROJECT_ID`
- Variable `UNITY_UBA_BUILD_TARGET_ID`

Key IDとSecret keyは同じUnity Cloudサービスアカウントkey pairでなければなりません。Unity accountの
email、password、Editor license、ULF、serialは使用しません。旧hosted Unity CI用Secretが残っていてもbridgeは
参照しないため、UBA成功を確認した後に別の安全な作業として削除してください。

status codeごとの切り分けは次のとおりです。

- `401`: Key ID／Secret keyの組み合わせ、key失効、前後の空白を確認する
- `403`: サービスアカウントに対象projectのtarget設定読取、build開始、status／artifact読取、timeout時cancelの
  最小roleがあるか、現在のDashboard表示に従って確認する
- `404`: 3つのIDが同じorganization／project／`main` targetを指しているか確認する
- Environment待機: deployment branch、required reviewer、`main` branch protectionを確認する

認証header、Secret key、API responseの認証情報をworkflow logへ追加しないでください。keyをrotateするときは
新しいpairの2つのSecretを同時に更新し、`main`の成功後に古いkeyを無効化します。

### UBAが違うUnityを使う、Player buildで止まる、または二重起動する

Dashboardの`UNITY_UBA_BUILD_TARGET_ID`に対応するConfigurationを開き、次を確認します。

- Branchは`main`、Project subfolderは空欄
- Unity versionは`2022.3.40f1`を明示し、Auto DetectはOff
- Windows Desktop 64-bit、Machine typeは`Micro`
- Unit tests、EditMode、PlayMode、test失敗時build失敗がすべてOn
- Auto-buildとScheduleはOff
- Player exportはOff（`settings.advanced.unity.playerExporter.export=false`）

このtargetはunit-test-only／Content-onlyです。Playerやscene buildの失敗が出る場合、player exportが再びOnに
なっていないか確認します。pushごとにUBA buildが2件作られる場合は、UBAのAuto-buildまたはScheduleが
有効です。GitHub exact-SHA bridgeだけをtriggerにします。

packageの対応範囲はUnity 2022.3で、既存のローカル基準結果は2022.3.22f1です。standalone repositoryの
`ProjectVersion.txt`とUBA targetを40f1へ揃えたのは、Unityの2026年dependency更新で22f1が削除対象になった
ためです。Auto Detectに依存せず、targetにも40f1を明示してpreflightで不一致を検出します。

### UBAはSuccessだが`Unity CI Gate`が失敗する

`.github/scripts/Invoke-UnityBuildAutomation.ps1`は、要求した完全な`GITHUB_SHA`とUBA buildのSCM commitが
一致しない場合に失敗します。branch名が`main`でも、push直後の別commitや過去の成功buildは受け入れません。
Dashboardのbuild詳細でcommit SHAを確認し、対象workflow runのSHAと完全一致しなければ再利用せず、正しい
SHAで新しいbuildを開始します。

SHAが一致しても、次の2ファイルが欠落、破損、またはstrict validationに不合格ならgateは失敗します。

- `artifacts/editmode-results.xml`
- `artifacts/playmode-results.xml`

artifactをdownloadした作業ディレクトリで、workflowと同じ検証を再現できます。

```powershell
pwsh -File "Packages/com.buildsoft.motion-take-studio/Tools~/Run-MotionTakeStudioTests.ps1" `
  -ValidateResultsOnly -ValidationMode EditMode `
  -ResultPath "artifacts/editmode-results.xml"

pwsh -File "Packages/com.buildsoft.motion-take-studio/Tools~/Run-MotionTakeStudioTests.ps1" `
  -ValidateResultsOnly -ValidationMode PlayMode `
  -ResultPath "artifacts/playmode-results.xml"
```

validatorはwell-formed XML、対象assembly、run／assembly件数一致、EditMode 95件以上、PlayMode 2件以上、全件Passed、
failed／skipped／inconclusive 0、必須E2E fullnameを確認します。UBAのbuild statusだけを成功条件に
しないため、0件run、途中XML、別assemblyのartifactも意図的に失敗します。2022.3.22f1での確認済み基準は
Editor 95 / 95、Runtime PlayMode 2 / 2です。40f1の件数を新しい基準として記載するのは、実際のUBA XMLを
検証した後にしてください。

### Releaseでexact-SHA Unity CI evidenceが見つからない

ReleaseはUBAを再実行せず、正規`unity-tests.yml`の`push/main` runを完全な`GITHUB_SHA`で検索します。
最新attemptの`CI Contract`、`Unity EditMode + PlayMode`、`Unity CI Gate`がすべて成功し、
`unity-tests-<SHA>` artifactが唯一・未失効・非emptyでなければfail closedです。PRの同名Gate、手動dispatch、
別branch、別SHA、失効artifactは受け入れません。

Artifactの保持期間は14日です。対象SHAが現在の`main`で、artifactだけが失効している場合は、そのSHAの元の
`push` workflow runをGitHub Actionsから**Re-run all jobs**し、新しいattemptが成功してからReleaseを再dispatchします。
Release jobは取得後にEditMode 95件以上／PlayMode 2件以上のstrict XML validationも再実行します。

### UBAの無料枠が想定より早く減る

2026年の無料枠はorganizationごとに月200 Windows Micro分です。clean buildと手動Replayは消費に含まれますが、
Releaseは既存のexact-SHA artifactを再検証するため新しいUBA分を消費しません。Dashboardの
**Build Automation > Settings > Build Consumption**で次を確認します。

- Build minutes monthly capが有効で、追加支出が`$0`または許容額に制限されている
- Monthly cap reminderが支出上限より低い値に設定されている
- Concurrency capが`1`
- Auto-buildとScheduleがOff
- 不要なartifactを保持し続けないretention ruleになっている

料金と無料枠は変更されるため、Dashboardの当月usageを正とします。支出cap到達時は設定を緩める前に、
二重trigger、不要なclean build、長すぎるartifact保持を先に修正してください。

## 自動テストが通るが OpenVR / 任意 NDMF の実機 Capture が動かない

自動 PlayMode テストは Runtime の Player loop を確認するもので、実際の SteamVR／OpenVR デバイス、
ドライバー、USB 接続、tracker role、任意の NDMF Apply on Play の処理結果は対象外です。自動テストとは別に、
SteamVR を起動した対応 PC で本書の Ready／Record 条件を確認してください。NDMF、OpenVR、実アバターを
使う検証が失敗した場合は、このページの **Ready にならない**、**OpenVR が見つからない**、
**Generic Tracker の身体部位が違う**の順に切り分けます。

戻る: [導入・記録・レビューガイド](GettingStarted.md)
