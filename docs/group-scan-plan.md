# Group Scan (Multiple-Values Group Search) — Port Plan

> 把 **UE5CEDumper** 的 *Multiple Values Group Scan*(物件感知的 SDR matcher)移植到
> discrete 的 **Value Search(live)/ Snapshot / SPC Query** 三個 surface。
> 參考來源:`D:\Github\UE5CEDumper\docs\group-value-scan-spec.md` +
> `snapshot-group-match-spec.md`;核心 matcher `dll/src/Orden.h` /
> `ui/.../Services/GroupMatch.cs`。

## 0. 這功能是什麼

給 **2–4 個 slot**(每個是一條 numeric predicate + width scope),找出「不同欄位
**各自**滿足一個 slot」的物件 —— 一個 **System of Distinct Representatives (SDR)**,
用 Kuhn 二分匹配解。N 值同時比對的選擇性是乘法級的:單值上千筆命中,group 後常收斂到
個位數。matcher 寫一次(C++ live + C# DB),三個 surface 共用 leaf 來源不同的同一核心。

## 1. 鎖定決策(2026-06-29,使用者確認)

1. **建置順序** = *Matcher 先,再依序全做*:G1 → G2(IL2CPP live)→ G3(Mono live)
   → G4(C# wire)→ G5(Value Search UI)→ G6(Snapshot)→ G7(SPC)。
2. **v1 leaf 範圍** = *Object-block + value-struct*:物件直接 numeric 欄位 + 內嵌
   value-struct 成員(Vector3.x/y/z 等)。Deep(array/容器元素當 block)留到 **G8**;
   **cross-object 直接 drop**(Unity 無 UE GObject 的 Outer chain,不可直接移植)。
3. **live 含 Mono**(保守處理):Mono 端全程單緒、在單一 outer `ThreadAttachGuard` 內、
   worker 永不呼叫 `mono_class_vtable`(plan.md build #1152 凍機真凶)。IL2CPP 無此限制。

採用的預設(無需再問):共用 `GroupResultsControl` UserControl、inline 在 MainWindow
各 tab、wire 命名 camelCase(`beginGroupScan`,對齊既有 `beginValueScan`)、SPC group
維持 `ExperimentalEnabled` gating。

## 2. The seam — discrete 三個 leaf 來源(皆已存在)

leaf = `{ownerObject, fieldOffset, width, bytes/value}`。matcher 是純 SDR;每個 surface
自己產 leaf 並建 per-slot 命中清單,只共用 SDR 核心。

| Surface | discrete 既有 leaf 來源 | 層 |
|---|---|---|
| **Value Search (live)** | **重用既有 value-scan 管線**:`ReverseLookupIndex` 建**一次**共用,per slot 跑既有 `ScanForValue`→resolve(`WhatIsAt`/`revIndex.Lookup`)→ 把命中 tag 到 `(instance, fieldOffset)` leaf | DLL C++(Mono+IL2CPP) |
| **Snapshot** | `SnapshotEnvelope.FieldIndex`(已含 array/struct 展開)group by `(Assembly,ClassFqn,InstanceAddress)` | 純 C# |
| **SPC** | `SpcQueryEngine` 的 per-key `NumericValues[]` 序列 group by 物件 | 純 C# |

> **live 的「掃一次比對多值」(使用者 2026-06-29 指引)**:不照抄 UE5 的 per-object 全
> leaf walk。改成「既有單值掃描跑 N 次,共用同一個 `ReverseLookupIndex`」:slot 的
> *membership*(哪個欄位 == 哪個值)由既有 scanner 決定,matcher 只做 SDR。index build
> 是唯一碰 `mono_*` 的步驟,**建一次給 N 個 slot 共用 → `mono_*` 呼叫不增反減**,所以
> Mono BDWGC 風險不會擴大(同一條已 live-verified 的 scan+resolve 路徑)。

關鍵差異(vs UE5):discrete snapshot 數值是 **display-string 解析**(`ValueDisplay`→
`double`,規則同 `SpcQueryEngine.TryParseNumeric`),**非 hex/byte 解碼**;且 **無
`FloatRoundMode`** —— 比較一律 plain `double`。`FieldType` 是 fully-qualified CLR 名
(`System.Int32` / `System.Single`…),width-scope gate 依此(對齊
`ClassInterestEngine.NumericTypes`)。

## 3. Matcher 雙生(已實作 — G1)

- **C++** `dll/src/shared/GroupMatch.h`(`namespace Discrete::GroupMatch`)—— live 用,**精簡
  純 SDR 核心**(只 `SlotMatches` + `HasDistinctAssignment` + `ResolveAssignment`,純 int
  組合、零 Memory/Mono 依賴)。membership 由既有 `ScanForValue` 決定、refine 由既有
  `CompareCandidateForRefine` 決定,所以 matcher 不需 per-width/predicate 機制(那些在
  C# 雙生 + DB 側才需要)。**G2 wiring 後隨 DLL build 驗證編譯。**
- **C#** `ui/UnityDumpUI/ViewModels/GroupMatch.cs`(`UnityDumpUI.ViewModels`)—— DB 用,
  double-based。純 SDR 核心(`HasDistinctAssignment`/`Assignment`)+ Snapshot 風格
  predicate 評估(absolute Exact/Bigger/Smaller/Between + relative
  Changed/Unchanged/Increased/Decreased,over 每 leaf 的 value 序列)。SPC group 只重用
  SDR 核心、用自己的 `SpcPredicateKind` chain 建命中清單。
- **測試** `ui/UnityDumpUI.Tests/GroupMatchTests.cs`(xUnit)—— SDR distinctness /
  duplicate-value / augmenting-path / empty-slot reject、scope gate、width-fit guard
  (70000 不命中 Int16)、各 predicate、end-to-end Run。**本環境可直接 `build.cmd test`
  驗證(無需遊戲)。**

## 4. 分階段(每階段可獨立 build + 測,粗體 = 動 DLL)

| Phase | 範圍 | 驗證 | 狀態 |
|---|---|---|---|
| **G1** | C++ `GroupMatch.h` + C# `GroupMatch.cs` + xUnit | UI build + `build.cmd test` 綠 | ✅ 實作完成,待 build/test |
| **G2 (DLL)** | `GroupScanSession.h/.cpp` + 4 handlers(`beginGroupScan` IL2CPP + 共用 `refine/query/end`)+ `MagicStrings` + **IL2CPP** group scan = 既有 `ScanForValue` per slot 共用一個 `ReverseLookupIndex` → tag 到 leaf → SDR。value-struct leaf 由既有 reverse-lookup `field.kind` 免費取得 | ✅ **build-verified**(`build.cmd dll` 兩 backend clean、0 warning);IL2CPP 遊戲 live-smoke 待 G5 UI | ✅ 實作+編譯完成 |
| **G3 (DLL)** | **Mono** `beginGroupScan` 同 G2 路徑(既有 Mono value-scan 管線已 live-safe;index 單緒 by design、outer attach guard;raw scan 純 SafeReadBytes —— 不新增 `mono_*` 暴露) | ✅ **build-verified**(兩 backend clean、0 warning);**活俠傳 32-bit Mono live-smoke 待 G5 UI** | ✅ 實作+編譯完成 |
| G4 | C# models(`GroupSlotRequest/GroupSlotMatch/GroupCandidate` + 4 results)+ `ProtocolJson` AOT 註冊 + `BackendAdapter` 4 method + `BulkCommands` | ✅ **build + AOT publish 0-warning** | ✅ 完成 |
| G5 | Value Search Single/Group `ToggleSwitch` + `ValueSearchViewModel.Group.cs`(2-4 slot 輸入、per-slot refine predicate、master-detail 結果 + 🔒 locked-offset 表)+ MainWindow.axaml + 4 codebehind handler | ✅ **785 tests green、AOT publish SUCCESS、integrity hash 注入**;**兩 backend live-smoke 待實機** | ✅ 完成,待 live-smoke |
| G6 | `SnapshotGroupEngine` + 11 xUnit + **Snapshot Diff/Group `ToggleSwitch`**(`SnapshotViewModel.Group.cs`:A/B picker、2-4 slot scope/predicate/target 輸入、master-detail 結果、system filter) | ✅ 803 tests green、AOT publish 0-warning | ✅ done |
| G7 | `SpcGroupEngine` + 7 xUnit + **SPC Single/Group `ToggleSwitch`**(`SpcQueryViewModel.Group.cs`:重用 SelectionRows snapshot picker、per-slot compact chain = absolute first + relative rest、master-detail 結果) | ✅ 803 tests green、AOT publish 0-warning | ✅ done |

> **整個 group-scan 功能(G1-G7)完成 + AOT-verified。** live Value Search 已實機 live-smoke PASS(兩 backend)+ 三項回報修正;Snapshot/SPC group 為純 C#(本機驗證),UI read-only master-detail(無 handoff,結構發現即價值;位址跨 session 可能 stale)。
| G8(可選) | Deep:array-element / 容器 leaf(IL2CPP 再 Mono) | live-smoke | ⬜ deferred |

## 5. 風險 / delta(UE GObjects vs Unity Mono/IL2CPP)

1. **物件識別**:UE 有 GObject index + Outer chain;discrete 用
   `(Assembly,ClassFqn,InstanceAddress)`、無 Outer → cross-object/Pivot **drop**。
2. **field-offset / object header**:沿用既有 value-scan handler 的 offset 慣例
   (`inst + field.offset`),**勿自創**;Mono header = `MonoVTable*`→deref→`MonoClass*`,
   IL2CPP qword0 = `Il2CppClass*`;`ManagedLayout::kPointerSize` 4/8 影響 32/64-bit。
   這是最易錯的 byte-offset 點 —— G2/G3 先對既有 `scanFieldValue` class-scoped 路徑驗證。
3. **BDWGC 單緒(Mono)**:整個 Mono group scan 在單一 outer-attached thread;worker 不
   碰 `mono_class_vtable`/`mono_*`;沿用 `ReverseLookupIndex` 的 `Sleep(1)` pacing。
   IL2CPP 不受限(`FindAllInstances` 平行)。**這是 THE 設計約束。**
4. **AOT-safety**:新 result/candidate 型別全進 `ProtocolJsonContext`(無反射);model 用
   `[ObservableProperty]`;無 `[JsonPropertyName]`(snake_case 在 `BackendAdapter` 套)。
   matcher 維持 pure/static。
5. **數值路徑**:snapshot/SPC 無 hex→byte decoder → C# `Leaf.Num` 由
   `ValueDisplay` 解析(非 byte);live DLL 路徑用 bytes(它擁有記憶體)。

## 6. 檔案地圖

**新增**:`dll/src/shared/GroupMatch.h`(✅)、`GroupScanSession.h/.cpp`;
`ui/UnityDumpUI/ViewModels/GroupMatch.cs`(✅)、`SnapshotGroupEngine.cs`+models、
`SpcGroupEngine.cs`+models、`ValueSearchViewModel.Group.cs`、
`Views/GroupResultsControl.axaml(.cs)`;`ui/UnityDumpUI.Tests/GroupMatchTests.cs`(✅)。
**編輯**:`dll/src/shared/ExportAPI.cpp`(IL2CPP/Mono value-scan 區塊 + `RegisterCoreHandlers`)、
`MagicStrings.h`;`ui/UnityDumpUI.Core/Services/ProtocolModels.cs`+`ProtocolJson.cs`+
`BackendAdapter.cs`;`ui/UnityDumpUI/Views/MainWindow.axaml`(+`.axaml.cs`)、
`MainWindowViewModel.cs`。

## 7. 備註

discrete **無 C++ 測試框架**(不像 UE5 有 `dll_helpers_test`)。SDR 演算法正確性由 C# 雙生
`GroupMatchTests` 覆蓋(同演算法),C++ matcher 由 DLL build + live-smoke 驗證 —— 對齊
discrete 既有「xUnit + live-smoke」慣例。
