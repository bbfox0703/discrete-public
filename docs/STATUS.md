# UnityCEDumper — Status (指標檔 / pointer)

> 這個檔已**瘦身成指標檔**。正典進度帳與**集中待辦清單 (Open Backlog / TODO)**
> 都在 [plan.md](plan.md);完整的舊 session hand-off 詳細紀錄在
> [archive/STATUS-archive-2026-05-29.md](archive/STATUS-archive-2026-05-29.md)
> 與 [archive/status-history.md](archive/status-history.md)。

| 項目 | 值 |
|---|---|
| **Current build** | #928 |
| **Current phase** | ✅ **Phase 31 inline value-type struct(Vec3/4)** 31-1/31-3/31-4 SHIPPED 於 `dev`(31-2/31-5 deferred);先前 Phase 29i 4 波亦在 `dev` |
| **Branches** | `dev` 領先 `main`(Phase 29i + Phase 31 partial,多 commit);`main` 仍在 `315bcc4` |
| **Tests** | 684 UI tests green;UI 0/0 warnings。**注意:Phase 31-1 改了 DLL(`ExportAPI.cpp`)尚未 build/驗證** |
| **Mid-flight** | Phase 31-2(DLL Phase B 泛型 struct,卡 offset 問題)+ 31-5(value-search 命名)**deferred 待 live build**;Phase 31-1 DLL 待實機 build + live-smoke;Phase 29i 待 live-smoke |

**Phase 31 live-smoke 發現(2026-06-01,Core Keeper/Pug IL2CPP x64;log `…\Discrete_Dumper\Logs\44332`)** — 兩個**既有 live Inspector value-struct 功能**的真實 bug,且決定性解開 Phase B 的 offset 疑問:
1. **value-struct 展開多加 0x10(header double-count)** — `PlayerController.srOffSetPos` (Vector3@0xA34) 經 hex 驗證真值是 **(0,0,0)**,但 Inspector 顯示 (0.6875,-0.0585,0) — 它從 `structBase + 0x10` 開始讀(`il2cpp_field_get_offset(Vector3.x)` 回 header-inclusive 的 0x10,inline struct 無 header 卻沒扣),讀到隔壁 `defaultSROffSetPos` 的 y/z。**定論:inline value-type 成員正確讀法 = `fieldAddr + member.offset − 2*ptrSize`。** → 這正是 §3.2 Phase B 卡住的問題,現在已確定要扣 header。**已 ship 的 31-1 Phase A 不受影響**(寫死 struct-relative 0/4/8 offset,反而比現有 live drill 正確)。**待修**:既有 live Inspector ValueStruct drill 的 +0x10 bug(`WalkedFieldKind.ValueStruct` → walkInstance-by-name 路徑)。
2. **巢狀型別 value-struct 解析失敗** — `CharacterData.NonSerialized`(nested type)展開報 `class not found: CharacterData.NonSerialized`;by-name class 解析不認 `.` 分隔的巢狀名(需 `/`/`+`)。CE 可展開。同時影響 live drill 巢狀展開 + Phase B。

**最新批次(2026-06-01,Phase 31 — inline value-type struct,部分 SHIPPED)** — spec [value-type-structs.md](value-type-structs.md):
- **根因**:`decodeField`([ExportAPI.cpp](../dll/src/shared/ExportAPI.cpp))對 value-type struct(Vector3=3 floats 等)只讀一個 pointer-word → snapshot/SPC/diff/pivot/value-search 全顯示 `(no value)`、無法處理 Vec3/4。
- **31-1 SHIPPED**(DLL Phase A + UI)— `decodeField` catch-all 加 named Unity math struct 表(Vector2/3/4、Quaternion、Color(32)、Vector2/3Int、Rect:依固定 offset 讀 float/byte/int32 emit `fields` 成員;instance 欄位 + array 元素共用,純 offset 讀取零 CTD 風險)。protocol `SnapshotFieldEntry.Fields`(隨 30a dedup `V` + `payload_gzip`,舊 .db 相容);`FormatFieldValue` 渲染 `(x,y,z)`。**DLL 未 build/驗證 — 待實機。**
- **31-3 SHIPPED**(UI)— `BuildFieldIndex` 加 `ExpandStructMembers`:struct 成員 fan-out 成 pseudo-row(`_position.x/.y/.z`,絕對 field offset、遞迴 nested)+ 保留 summary tuple row → diff/SPC 可逐軸定位。`CountFieldValues` 同步計數。
- **31-4 SHIPPED**(UI)— `SnapshotEnvelope.EnumerateFlatFields`/`ResolveDottedFieldValue`;PivotEngine/KeyDiscovery 支援 dotted component path(`_position.x`),欄位 picker 列出 component。struct value 欄位自動顯示 `(x,y,z)` 軌跡。
- **31-2 / 31-5 DEFERRED** — 31-2(泛型 value-type 遞迴)卡在 inline struct member offset 是否扣 header(`member.offset` vs `-2*ptrSize`,§3.2,只能實機對 CE 驗證);31-5(value-search reverse-lookup 命名 component)是 DLL address-finder 改動、riding on 31-2(單 component float scan 今天已可用)。兩者待 live build session。

**最新批次(2026-06-01,Phase 29i — Pivot usability & value-driven discovery,UI/engine-only)** — spec 見 [class-pivot.md → Phase 29i](class-pivot.md#phase-29i--pivot-usability--value-driven-discovery):
- **29i-1 class 結構瀏覽** — `ClassShortlistEngine` 改為**保留** field 名稱/型別(原本掃完只留 count);shortlist grid 加 `Fields (preview)` 欄(前 10 + `+N more`)+ 下方 detail panel(完整 field 清單,double-tap→Key / 右鍵→Value)。
- **29i-2 array inner-field 評分 picker** — `KeyDiscoveryEngine` 加 `ArrayField` element 模式(把 Layer B 元素 `Fields[]` 當 pseudo-instance 餵同一套評分,`FieldSetsOf` 共用來源);Layer B expander 加「Score element fields」grid(同 Stage 1 形狀)。
- **29i-4 ★ 值驅動定位** — 新 `SnapshotValueLocatorEngine`:exact(欄位帶某值,依命中快照數 + match rarity 排序)/ delta(欄位值域跨兩快照位移 Δ,用 set-membership 免逐 instance 對齊)反查 → ranked `(class, field)`,含 Layer A/B array element;跨 session 可用(讀 blob 非 live mem)。UI「Find by value」expander + double-tap handoff + 「Suggest key & run」一鍵收尾。
- **29i-3 無監督排序 + preview** — 新 `ClassInterestEngine`:Pass A Stage-0 top-N(成本閘)→ Pass B per-class KeyDiscovery + numeric 欄位 volatility(同 key 跨快照變化比例,constant-gated);`interestingness = bestKeyScore × maxVolatility`。「Likely game-state」expander + 懶建 3 筆 trajectory preview。**最 heuristic**(noise 未完全濾掉,靠 preview 辨識;精確版用 29i-4)。
- 序:29i-1 → 29i-2 → 29i-4 → 29i-3(1A 最後)。每波各自 commit/push 到 `dev`、build clean、tests green。

**最新批次(2026-06-01,CT 分發 + pipe 並發 + walk 硬化)** — live-verified(Utawarerumono IL2CPP):
- **CT bridge 改 action-driven** — 砍掉 feature-picker 對話框與「預挑 lua」模型。`Inject Discrete Bridge` 只塞 core(`utils`+`unity_dump` = inject DLL + pipe)+ `Load Discrete Bridge` AA。其餘 lua helper 由各「Send to CE Table / Create AA」動作**按需自帶**(`InjectTableFile` + 生成的 AA 用 `findTableFile` 自我載入,見新 `AaLuaLoader`)。`unity_dissect`/`unity_scene`/`unity_addrfind` 從 bridge 移除(純 UI,走 pipe;檔案留作 Mode-A 獨立腳本)。
- **CT 分發** — DLL 不內嵌、與 .CT 同資料夾(`Discrete-Mono.dll` / `Discrete-IL2CPP.dll` / `Discrete-Mono-x86.dll`);`utils.lua` `M.dllPath` 改 table-folder 優先多候選搜尋 + `is32bitProcess()` 挑 x86。新 `tools/Package-CTDist.ps1` 一鍵打包(自動改名 x86)。
- **DLL 卸載** — Tier-2 FreeLibrary **移除**(對每幀 hook runtime_invoke 的 DLL 無法安全 unmap → CTD);只留 dormant teardown(`[DISABLE]` → `discreteShutdown()`:停 pipe + 卸 hook、DLL 留映射、可 re-enable)。
- **Pipe 並發** — `BackendAdapter` 開**兩條**連線(interactive + bulk),依 cmd 路由;snapshot/value-scan/find/export 走 bulk,瀏覽走 interactive → snapshot 時 Inspector/Classes 不再卡。`BulkOperationGate` 跨 VM 互斥重操作(按鈕變灰 + busy 提示)。AOBMaker 不在時所有插入動作都訊息 + 取消;Inspector HEX 導覽也補訊息。UI 用 2 條連線(`kMaxPipeInstances=4` 留 ≥2 給 CLI)。
- **build** — Release/Publish 的 dist 不再留 `*.pdb`(只 Debug 留 + 結尾 prune)。
- **walk drill-down CTD 全修(IL2CPP)** — (1)繼承走訪 `class_get_parent` 全走 SEH(`SehGetParent`);(2)`ReadClassInfo`/`DescribeClass` 的逐項 accessor(`field_get_*`/`method_get_*`/`class_get_name/namespace`)全包進泛型 `SehCall`;(3)繼承鏈 runaway(衝到 16 = 垃圾 class)直接 bail,不 ReadClassInfo;(4)String 欄位指向非字串物件時偵測嵌入 NUL / 高控制字元比例 → `<string_error: not text>` 而非整片亂碼。`walkInstance` 加逐步 walk log(每行 flush,可定位崩潰步驟)。

**先前批次(2026-05-31 後段,Inspector nested-class drilldown + Copy CE XML x86)**:
- **Mono nested-class drilldown 修正** — Inspector 展開只經 field-type reference 抵達的 ref-type(如 `StatConvertData` 經 `StatConvertCollectionData._list`)原本只顯示繼承的 `m_CachedPtr`:Mono `walkInstance` 用 `mono_class_from_name` 解到的 metadata class field table 是空的。修法:Mono 端比照 IL2CPP 接受 `klass_from_header_addr`,用 `mono_object_get_class`(`Mono::ClassOfObject`)從物件 vtable 取**活的 runtime class**(必 field-set-up)。Exporter 也加 name-based fallback(`EmitChildFieldsByHeaderAsync`)。**Live-verified(活俠傳:`_list[0]` 完整展開 `_curve`/`_book`/`_devNote`/`_id`…)**。
- **Copy CE XML 32-bit pointer-size 修正** — (a)IntPtr/reference/List-handle 葉節點輸出 `8 Bytes`(x86 應 4)→ overlap 下一欄;(b)String header offset + `List._items` deref 用了 stale `GamePointerSize`(export 時 0→8)→ x64 offsets + `_list <empty>`。修法:DLL `walkInstance`(Mono+IL2CPP)回應加權威 `pointer_size`;`WalkInstanceResult.PointerSize` + exporter 採用之覆蓋 caller 值;新 `PointerVartype(options)` 取代 9 處寫死 `8 Bytes`,IntPtr scalar leaf 同步。**Live-verified(活俠傳:m_CachedPtr=4 Bytes、String length @+8、`_list` 15 元素)**。
- **SS5** — class-list「Inspect (find instances)…」補上切到 Inspector tab。
- **Export-options drilldown depth 上限 5→6**。

**先前批次(2026-05-31 前段,Phase 28b instance + Snapshot UI/UX)**:
- **Phase 28b — IL2CPP instance-field xref** — 不再回 v1 error;Zydis 掃宣告類自己的 methods 找 `[gpReg+fieldOffset]`(base≠rip/rsp/rbp),`confidence="heuristic"`,read/write 由 operand action 分類;method 範圍 = 下個 method entry,clamp 8KB + section end + `int3` 停;dedup by hitAddress。**Live-verified(活俠傳 IL2CPP x64:`CharacterData.life_` @0x40 → 126 matches)**。Phase 28b-v3.2(`<>c` lambda-cache xref)正式 **WON'T DO**(命名易識別、非 gameplay state、無 C/P 值)。
- **DataGrid 欄寬 rule(寫進 CLAUDE.md + ui-spec.md)** — result grid 禁用 `Width="*"`(逼 fit-to-window → 拉一欄縮別欄);改 `Width="Auto"+MinWidth` → 可橫向捲動。修了 5 個 result grid + Inspector Name/Type/Value MinWidth(220/160/320→120/110/140)。例外:MethodCall 參數格 / SPC picker。
- **Snapshot UI/UX(SS1-4)** —(SS1)scope `+add assembly` 改 **AutoCompleteBox** 可打字過濾;`Include system assemblies` 在 Scope 有指定 assemblies 時 disable(僅全抓適用)。(SS2)`Game: 32-bit Mono…` banner 併入 Decode budget 列(MaxLines=2);budget 說明改 hover tooltip。(SS3)`FormatMemory` MB/GB 一律 2 位小數(`0.04 MB` 不再顯示 `0 MB`)。(SS4)**FIX 真 x86 bug**:`List<T>._items` 從寫死 +0x10 改 `2*ptrSize`(x86=+0x8)+ `ParseLittleEndianHexQword` 依長度 byte-swap → 32-bit Mono nested List 不再 "header unreadable"。**Live-verified(活俠傳:`StatConvertData._list` 15 元素位址與 CE 逐 byte 一致)**。
- **SS6/SS7 查證為正常(非 bug)** — `_name <string_error: implausible length>` 是 string 守衛正確拒絕 heap-walk false-positive instance 的 garbage pointer;`_statAdditionData` drilldown 位址與 CE 逐 byte 一致(List 折疊成元素是刻意表示法)。

**先前 UI 批次(2026-05-30,Inspector + Value Search)**:
- **List<T> 可展開** — `InstanceFieldClassifier`/`UnityTypeMap`/`CeXmlExporter` 同時接受 IL-form `List`1<>` 與 live demangled `List<>`;`enchantList_`/`hasSkill_` 等現有展開箭頭 + Copy CE XML 正確 per-slot。
- **Inspector instance 級 Copy CE XML 按鈕** — 跨 tab(Value Search / Address Lookup / Scene / Snapshot)inspect 後可用,不需選欄位/不依賴 Browser。**(已 live-verified)**
- **展開不再跳頂端** — `InsertChildrenIntoFields`/`CollapseSubtree` 無 filter 時直接 splice `FilteredFields`,保留捲動位置。
- **String drilldown 修正** — List<String> slot DLL 回傳裸 pointer;UI 端 fallback 用 `readMemory` 解 managed String,header offset 隨 bitness(x64 +0x10/+0x14、x86 +0x8/+0xC,`StringFieldOffsets`)。**Mono 32-bit live-verified(道德 / karma01 正確)**。
- **Inspector Limit 欄位加大**(`MinWidth=150 Width=160`)。
- **Value Search**:Limit 預設 10000;新增 `numeric (no 1-byte)` / `numeric (all widths)` 聚合型別(fan-out 多寬度去重,one-shot);`Ignore system assemblies` 勾選 + results filter 欄位(client-side,不重掃)。
- **Copy CE XML live-export 重寫** — 改用 `walkInstance`/`walkInstanceFromHeader`(與 Inspector 同路徑)取代舊的 static name-lookup + UnityTypeMap decode。修正:nested class(`charaInfo`)會展開、`enchantList_` List 元素逐 slot drill、enum(`charaKind_`)→ signed int leaf(不再是 `0x65` 假指標 group)、String → CE `String` 指向 inline UTF-16 buffer。
- **Copy CE XML 改 relative addressing(UE5CEDumper model)** — 整張表只有 root 一個絕對位址;其餘全用 `+offset`:scalar=`+offset`、pointer(class/string/list)=兩層 deref group(`+offset` + `Offsets=[0]`,children 再 `+childOffset`)、inline struct=`+offset`(無 Offsets)、String=`+offset` Offsets=[0] → `_stringLength`(+2*ptr)/`_firstChar`(+2*ptr+4, ZeroTerminate)、List<T>= 外層 deref + 內層 `_items` deref + 每 slot `+(4*ptr + i*elemSize)`。→ 把 root 換成 CE symbol 即可套用到該 class 的任一 instance。
- **Copy CE XML drilldown depth 修正** — Inspector 的 instance/node「Copy CE XML」原本 hardcode `DrilldownDepth:1`,忽略 Export-options 滑桿;改由 `Inspector.ExportDepthProvider`(MainWindowViewModel 接到 `Browser.DrilldownDepth`/`ArrayLimit`)帶入,depth 4 現在會 drill `statusWork_→base_→hp_/mp_` 整條鏈。`ExportOptions.PointerSize` 由 `Inspector.GamePointerSize` 帶入。`ILiveExportSource` 加 walkInstance 兩個方法;新 emit 在 `CeXmlExporter.Live.cs`。新增 `CeXmlExporterLiveTests`(fake live source:relative-addr enum/string/nested/List<String> + x86 offset)。**64-bit live-verified(受讚頌者)**。
- **Copy CE XML 32-bit 修正(build #855)** —(1)**DLL walkArray stride**:ref-type array fallback `elemSize` 從寫死 8 改 `ManagedLayout::kPointerSize`(x86=4),修掉 Inspector grid + CE table 上 `_levelText[i]` slot 位址每個都偏 4 bytes(活俠傳 x86)。(2)**array data-offset robust**:exporter 改由 `walk.DataAddress − arrayAbs` 推 SZARRAY vector offset(精確,免賴 PointerSize)。(3)**String 子節點改中性 label**(`length (int32)` / `chars (UTF-16)`)不再用 `_stringLength`/`_firstChar` 假 symbol(各遊戲名稱不同:m_length / m_firstChar …,且未實際讀 metadata)。String header offset 仍隨 ptr(x64 +0x10/+0x14、x86 +0x8/+0xC)。**Mono 32-bit live-verified(活俠傳,build #855:array slot 位址 stride 4、String header +8/+C 正確)**。
- **Inspector context-menu 精簡 + Copy CE field 重做** — 移除「Copy CE XML rooted here」(與 Copy CE field 重複);「Copy CE field」改成每個選取 node 各自一棵 standalone 子樹(root 帶該欄位絕對位址、children relative),depth-aware + multi-select,與 in-place export 同 emitter(`CeXmlExporter.RenderFieldNodesLiveAsync` + `EmitWalkedFieldLiveAsync` 的 `addrOverride`)。修掉舊 `WrapLeavesXml` 把 String/array slot 輸出成單一 flat `Pointer` leaf 的 bug(`[0]` 現在正確展開 length/chars)。`WalkedFieldNode` 保留來源 `WalkedField`(`ToWalkedField()`)以忠實重現 enum/string/struct 分類。新增 2 個 `RenderFieldNodes` 測試。644 tests green、UI build clean(UI-only,無需 DLL rebuild)。**64-bit + Mono 32-bit live-verified(受讚頌者 / 活俠傳)**。
- **聚合 scan ReverseLookupIndex 重建修正** — 聚合 fan-out 原本每個寬度都重建 ~48s reverse-lookup index(32-bit Mono 8 寬度 → 6+ 分鐘 hang)。新增 process-wide TTL 重用 cache:`ReverseLookupIndex::Build(reuseWindowMs)`(Mono+IL2CPP),`beginValueScan` 收 `reuseIndexMs` arg;聚合首寬度建一次,其餘 7 個複製 sorted bases(ms vs 48s)。單次 / refine scan 不帶 arg → 永遠 fresh(避 GC 失效)。**Mono 32-bit live-verified(log 59260:1 次 build `instances=1268731` + 5× `reused cached index`,float/double <5000 走 WhatIsAt fallback;整批 8 寬度 ~2.6s,前為 6+ 分鐘)**。
- **Dictionary 展開** — 暫不處理(`buffAttrRes_`/`buffSickRes_`),需 live `_entries`/`Entry<K,V>` layout 調查。

**先前批次(2026-05-30,scan,均已 merged)**:
- **P2 Mono port**(#823)/ **候選數 gate**(#824)/ **P2 SIMD 內層**(#839)/ **Inspector「→ Browser」按鈕**。

**⚠ 待 live-smoke(需 inject 實機)**:
- Value Search 聚合去重結果(原生 scan 無自動測試覆蓋;聚合 index 重用本身已 live-verified,log 59260)。Copy CE XML / Copy CE field 內容已 live-verified(受讚頌者 x64 + 活俠傳 Mono x86)。
- 候選數 gate / P2 SIMD:Utawarerumono(x64)+ 活俠傳(x86)計時 + 結果筆數與舊版一致。
- SS1/SS2 picker/layout 觀感(純 UI,build 已驗);28b-instance 已 live-verified(活俠傳),x64 game(受讚頌者)instance xref 可選 re-smoke。

➡ **下一步 / open backlog**:見 [plan.md → Open Backlog / TODO](plan.md#open-backlog--todo)。