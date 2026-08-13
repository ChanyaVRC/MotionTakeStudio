# API・データモデル

Motion Take Studio は Runtime の耐久データと、UnityEditor 専用の Capture／Authoring／Bake API を
分離しています。Runtime 型の namespace は `BuildSoft.MotionTakeStudio`、Editor 型は
`BuildSoft.MotionTakeStudio.Editor` です。

## Runtime データモデル

### MotionTakeAsset

Import 済み `.mttake` のメインアセットです。

- `TakeDisplayName`, `SessionId`, `FrameRate`, `HumanScale`
- `SourceAvatarGlobalObjectId`
- `Frames`, `FrameCount`, `DurationSeconds`
- `Initialize`, `ClearFrames`, `AddOrReplaceFrame`, `TryGetFrame`

`TryGetFrame` の引数は List index ではなく `FrameIndex` です。連番追加は末尾へ O(1) で追加し、
置換・非連番挿入は二分探索で昇順を維持します。

### MotionTakeFrame

1 フレーム分の値オブジェクトです。

- `FrameIndex`, `TimestampSeconds`
- `TrackerPoses`: `MotionTrackerPoseSample` のリスト
- `ResolvedHumanPose`: `MotionHumanPoseSample`
- `TrackingWasInterpolated`
- `SetTrackerPose`, `TryGetTrackerPose`, `SetResolvedHumanPose`

`MotionTrackerPoseSample` は Role、DeviceId、TrackingState、位置、回転、速度、角速度を持ちます。
`Valid` と `Interpolated` が `IsUsable == true` です。

`MotionHumanPoseSample.Muscles` はシリアライズ済み Take を外部から変更できないよう、アクセスごとに
防御コピーを返します。複数回走査する場合は戻り値をローカル変数へ保持してください。
`ToHumanPose()` も独立した Muscle 配列を返します。

### PoseTarget と補正 Recipe

`PoseTarget` は Head、Hips、左右 Hand、左右 Foot、左右 ElbowHint、左右 KneeHint の 10 種類です。
Hint 以外は位置と回転、Hint は位置だけを扱います。

`MotionPoseTargetOffset` は次を保存します。

- `PositionOffsetNormalized`: Avatar Root ローカルの位置差分
- `RotationOffsetLocal`: ベース回転に右から掛けるローカル差分 Quaternion
- `HasPosition`, `HasRotation`

通常ターゲットの位置は `humanScale`、Hint は `limbLength` で正規化されます。
`ResolvePositionOffset(humanScale, limbLength)` で元のローカル差分へ戻せます。

`MotionPoseKey` は Frame、InfluenceFrames、複数 TargetOffset を持ちます。InfluenceFrames は
1〜60、既定値は 12 です。`MotionPoseCorrectionTrack.TryEvaluate` はキーの前後を Smoothstep で
ゼロへ Fade し、影響範囲が重なる隣接キーを補間します。

`MotionEditRecipe` は SourceTake、CorrectionTrack、RecipeVersion、DisplayName を持つ
`ScriptableObject` です。

### Validation Report

`MotionValidationReport` は SourceTake と、フレーム範囲付きの `MotionValidationMarker` を保持します。
カテゴリは TrackingGap、FootSliding、FloorPenetration、RootDiscontinuity、NonFinitePose、
IkUnreachable、JointFlip です。

標準 Capture は補正後の左右 Foot 位置と四肢の Bend Direction を Validation Source へ渡します。
JointFlip は隣接 Sample の Bend Direction が `MotionTakeValidationSettings.JointFlipAngle`
（既定 120 度）以上変化したときに生成されます。Solver 自体も前フレームの Bend Direction を使って
反転を抑制します。

## Editor セッション

`MotionCaptureCoordinator.Instance` は標準 Editor セッションです。主な API は次のとおりです。

- 状態: `Phase`, `StatusMessage`, `FrameCount`, `CurrentFrame`
- データ: `ActiveCapture`, `ActiveMotionTake`, `ActiveRecipe`, `ValidationIssues`
- 操作: `PrepareCapture`, `BeginRecording`, `StopAndReview`, `ScrubToFrame`, `SaveAndExit`, `Cancel`
- Tracker: `TrackerProvider`, `TrackedDevices`, `RefreshTrackedDevices`, `AssignTrackerRole`
- Overlay: `OverlayPoseSource`, `TryGetSolvedTargetPose`
- 拡張: `SetTrackerProvider`, `Revalidate`

これらは Unity Editor の Main Thread から呼びます。状態遷移を飛ばすと
`InvalidOperationException` になります。

標準 `PrepareCapture` は一時 Clone を作り、NDMF Apply on Play と互換の完了通知を安全に利用できる
場合だけ `AvatarActivator` を追加します。この場合は完了通知後の Root を安定化して Ready にします。
NDMF がない、無効、確認不能、または安全に開始できない場合は、通常の Humanoid Clone を安定化して
Ready にします。NDMF はコンパイル時・パッケージ依存ではありません。

`IMotionTakeStudioSession` と `MotionTakeStudioSessionBridge.Register` を使うと、独自の Coordinator を
ウィンドウへ接続できます。登録は `IDisposable` を返すため、所有者の破棄時に Dispose してください。

## トラッカープロバイダー

`ITrackerPoseProvider` は次を提供します。

- `DisplayName`, `IsAvailable`, `Diagnostic`, `Devices`
- `TryGetFrame`
- `AssignRole(deviceId, role)`

`BeginRecording` は具体的な Provider 種別には依存しませんが、`IsAvailable == true` で、最初の Frame に
有効な Head／LeftHand／RightHand が必要です。単に Pose が 3 件あるだけでは開始条件を満たしません。

標準実装 `ValveOpenVrTrackerProvider` は `Valve.VR.OpenVR` を Reflection で読みます。
Generic Tracker は暫定ロールになるため、Ready 中に標準ウィンドウの
**OpenVR Tracker Roles** からシリアル番号ごとに明示割り当てしてください。

次は Ready 後に実行する Editor スクリプトの例です。

```csharp
using BuildSoft.MotionTakeStudio.Editor;
using UnityEditor;

internal static class MotionTakeTrackerSetup
{
    [MenuItem("Tools/BuildSoft/Assign Motion Trackers")]
    private static void Assign()
    {
        var provider = MotionCaptureCoordinator.Instance.TrackerProvider;
        provider.AssignRole("LHR-WAIST-SERIAL", TrackerRole.Waist);
        provider.AssignRole("LHR-LEFT-FOOT-SERIAL", TrackerRole.LeftFoot);
        provider.AssignRole("LHR-RIGHT-FOOT-SERIAL", TrackerRole.RightFoot);
    }
}
```

通常の Domain Reload を使う場合は Play Mode 移行で Provider が作り直されるため、
Prepare 前ではなく **Ready 後** に呼びます。この割り当ては永続設定アセットには保存されません。

独自セッションをウィンドウへ接続する場合、`IMotionTakeTrackerRoleSession` を実装すると同じ
Provider 名、診断、デバイス一覧、Refresh、ロール選択 UI を利用できます。
`IMotionTakeValidationSession.Revalidate` は Recipe 変更後の再検証 Hook です。

独自 Provider は `ITrackerPoseProvider` を実装し、Recording 中でないときに
`MotionCaptureCoordinator.Instance.SetTrackerProvider(provider)` で設定できます。

## Authoring API

`MotionTakeCorrectionAuthoring` は Recipe 変更を Unity Undo と Dirty 状態へ接続します。

- `TryGetEvaluatedTargetPose`
- `SetPosition`, `SetRotation`
- `AddPoseKey`, `ResetTarget`, `DeleteKey`, `HasKeyAtFrame`
- `SupportsRotation`, `IsLimbHint`

`MotionTakePreviewDriver` は HumanPose を先に適用し、Recipe の Head／Hips と四肢の補正を後から
適用します。Hand／Foot と Elbow／Knee Hint には `TwoBoneIkSolver` を使います。
`TryGetSolvedTargetPose` は要求 Target ではなく、到達範囲や関節制限で Clamp された後の実 Pose を返します。

`IMotionTakeOverlayPoseSource.TryGetSolvedTargetPose(stage, target, frame, out pose)` は
`Ik`、`Automatic`、`Manual` のいずれか 1 ステージを受け取ります。標準 Coordinator は Tracker IK 直後を
`HumanoidCaptureFrame.ikBodyPosition`／`ikBodyRotation`／`ikMuscles` に Auto とは別に保持し、
Auto は Filtering・Root Jump 補正・Foot Lock 後、Manual は Recipe 適用後の Solver 実 Pose を返します。
`IMotionTakeStudioSession.OverlayPoseSource` を実装する独自 Session も、この3ステージを混同せず返してください。

`IAnimatorPreviewStateGuard` を差し替えると、Animator の Playback 状態を別 Coordinator と
協調して所有できます。既定 Guard は `enabled`、`speed`、`runtimeAnimatorController` を退避し、
Preview 中は Animator を停止して Controller を外し、Dispose 時に戻します。

## Clip Baker

`IMotionTakeClipSource` は SampleCount、FrameRate、`TryGetSample` を提供します。
`MotionTakeAssetClipSource` は `MotionTakeAsset` をそのまま Auto Source として使う Adapter です。

```csharp
using System;
using BuildSoft.MotionTakeStudio;
using BuildSoft.MotionTakeStudio.Editor;
using UnityEditor;

var take = Selection.activeObject as MotionTakeAsset;
if (take == null)
{
    throw new InvalidOperationException("Project View で MotionTakeAsset を選択してください。");
}

var source = new MotionTakeAssetClipSource(take);
var result = MotionTakeClipBaker.CreateVersionedClips(
    "Assets/MotionTakeStudio/Clips",
    take.TakeDisplayName,
    source,
    source);
```

上の例では Corrected Source も同じなので Auto と Corrected の内容は同一です。補正済みクリップを
作るには、対象 Humanoid 上で Recipe を評価し、その HumanPose を返す別
`IMotionTakeClipSource` を渡します。

`BuildClip` は全 `HumanTrait.MuscleName` と RootT／RootQ を空パスの `Animator` カーブとして
作成し、NaN、Infinity、Muscle 数の不一致を拒否します。サンプル間の Muscle と Root 成分には
Linear Tangent を設定するため、各収録サンプルで速度がゼロになる補間にはなりません。
`CreateVersionedClips` は Auto、Corrected、Manual の 3 アセットを未使用の `_vNN` パスへ作ります。

## Validation Engine

`MotionTakeValidationEngine.Validate(IMotionTakeValidationSource, settings)` は、非有限値、Root 急変、
床貫通、足滑り、トラッキング欠落、Joint Flip を検査します。各 `MotionTakeValidationSample.IkWarnings` は、
その Sample の Frame に紐づく `IkUnreachable` Issue として追加されます。

`MotionTakeAssetValidationSource` は Root、Muscle、Tracker State を取り出します。床関連を有効にする
には、コンストラクターへ左右足位置と Floor Height を返す Delegate を渡すか、独自
`IMotionTakeValidationSource` を実装してください。HumanPose の `bodyPosition` は Avatar の
`humanScale` でメートルへ変換してから Root 急変を評価するため、`RootDiscontinuityDistance` の単位は
Avatar の体格によらずメートルです。

標準 `MotionCaptureCoordinator.Revalidate` は現在の Recipe を Manual の全フレームへ順番に適用し、
補正後 Root／Muscle／Foot、フレーム別 IK Warning、四肢 Bend Direction を Buffer へ収集して検証します。
検証終了後は呼び出し前のスクラブフレームを再適用します。

## Recovery API

`MotionTakeRecovery.RecoveryDirectory` はプロジェクトの
`Library/MotionTakeStudio/Recovery` を返します。

- `FindAll()`: `.jsonl` Journal を列挙
- `TryLoad(path, out CaptureTake, out RecoveryEntry)`: 読み込み。途中で切れた最終行は無視
- `Archive(path, out archivedPath, out error)`: Recovery 内の Journal を `Archived` へ移動

Recovery API は Editor 専用です。`CaptureTake` はシリアライズ可能な中間形式であり、
`MotionTakeAsset` そのものではありません。現行版には `CaptureTake` を UI から `.mttake` へ
再保存する操作はありません。

戻る: [導入・記録・レビューガイド](GettingStarted.md) / [アーキテクチャ](Architecture.md)
