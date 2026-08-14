# Changelog

## [0.1.1] - 2026-08-13

- Added SHA-pinned GitHub Actions automation. Pull requests run secret-free CI contract tests; protected `main`
  invokes Unity Build Automation v2 and archives its test results. Releases reuse the exact-SHA successful `main`
  run and revalidate its NUnit XML instead of spending another Windows Micro build.
- Replaced hosted Unity license activation with a least-privilege Unity Cloud service account. Protected jobs read
  `UNITY_UBA_KEY_ID` and `UNITY_UBA_SECRET_KEY` only from the `unity-ci` Environment, while organization, project,
  and build-target IDs are non-secret Environment variables.
- Added repository support for an API-controlled, unit-test-only Windows Micro target on Unity 2022.3.40f1. The
  documented target contract keeps Auto-build and player export disabled, enables both EditMode and PlayMode, and
  fails the build on any test failure. No Player or scene build is required. The bridge accepts only the requested
  GitHub commit SHA and revalidates both NUnit XML files with the package runner before the release gate can pass.
- Kept the package compatible with Unity 2022.3 projects. The standalone CI target uses 2022.3.40f1 because
  Unity Build Automation marks 2022.3.22f1 for removal; the verified 22f1 baseline remains Editor 95 / 95 and
  PlayMode 2 / 2.
- Made NDMF Apply on Play an optional enhancement with no package dependency.
- Capture now uses the ordinary stabilized Humanoid clone when optional processing cannot be armed.
- An armed avatar processor must still report completion before the capture can become Ready.
- Added PlayMode coverage for the NDMF-free Capture／Review／Correction／Bake path and the optional
  processor completion gate. Unity 2022.3.22f1 passes Editor 95 / 95 and Runtime PlayMode 2 / 2.

## [0.1.0] - 2026-08-12

- Added Play Mode Humanoid capture and recovery workflow.
- Added NDMF Apply on Play gating for the processed additive-scene capture clone.
- Added review scrubbing and distinct Raw／IK／Automatic／Manual solved-pose overlays.
- Added full-body Scene View targets, including elbow and knee hints.
- Added non-destructive pose correction keys and versioned Humanoid clip baking.
- Added full-frame corrected validation for non-finite poses, physical-meter Root jumps, floor penetration,
  foot sliding, per-frame IK warnings, tracking gaps, and joint flips.
- Added motion-preserving Linear Tangents to baked Muscle and Root curves.
- Added defensive-copy Muscle access, atomic Review checkpoints, and durable Pending Export recovery payloads.
- Added reflection-based OpenVR capture, replaceable tracker providers, and compile-time-optional NDMF／
  VRChat SDK integrations. The standard capture workflow still requires NDMF Apply on Play at runtime.
- EditMode／PlayMode の自動テストを分離し、Unity 2022.3.22f1 で Editor 87 / 87、Runtime PlayMode
  2 / 2 の成功を確認しました。Editor suite には、Play Mode へ遷移して 6 点 Capture、Review、
  左肘 Hint 補正、Validation、Clip 再生を通す E2E テスト 1 件を含みます。
- PowerShell runner に XML 契約の自己テスト、対象 assembly 検証、0 件・skip・inconclusive・一部成功の
  false green 防止、対象 assembly 単位の集計、run／assembly 件数一致、必須 Integration test fullname
  検証、既定 15 分の process timeout、`-nographics`、外部 local package 向けの明示 `ProjectPath`
  対応を追加しました。
- Test Runner／CLI、非 embedded package の `testables` 設定、OpenVR／NDMF 実機 Integration を
  自動テストと分けて検証する手順を文書化しました。
