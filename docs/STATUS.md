# UnityCEDumper — Status (指標檔 / pointer)

> 這個檔已**瘦身成指標檔**。正典進度帳與**集中待辦清單 (Open Backlog / TODO)**
> 都在 [plan.md](plan.md);完整的舊 session hand-off 詳細紀錄在
> [archive/STATUS-archive-2026-05-29.md](archive/STATUS-archive-2026-05-29.md)
> 與 [archive/status-history.md](archive/status-history.md)。

| 項目 | 值 |
|---|---|
| **Current build** | #864 |
| **Current phase** | ✅ Phase 28(Field→Methods)全收尾;IL2CPP instance-field xref + Snapshot UI/UX 批次 SHIPPED & merged |
| **Branches** | `dev == main == origin` @ `6e0fea5` |
| **Tests** | 644 UI tests green;兩個 DLL(x64+x86)build clean、UI 0/0 |
| **Mid-flight** | 無(nothing in flight)|

**最新批次(2026-05-31,Phase 28b instance + Snapshot UI/UX)**:
- **Phase 28b — IL2CPP instance-field xref** — 不再回 v1 error;Zydis 掃宣告類自己的 methods 找 `[gpReg+fieldOffset]`(base≠rip/rsp/rbp),`confidence="heuristic"`,read/write 由 operand action 分類;method 範圍 = 下個 method entry,clamp 8KB + section end + `int3` 停;dedup by hitAddress。**Live-verified(活俠傳 IL2CPP x64:`CharacterData.life_` @0x40 → 126 matches)**。Phase 28b-v3.2(`<>c` lambda-cache xref)正式 **WON'T DO**(命名易識別、非 gameplay state、無 C/P 值)。
- **DataGrid 欄寬 rule(寫進 CLAUDE.md + ui-spec.md)** — result grid 禁用 `Width="*"`(逼 fit-to-window → 拉一欄縮別欄);改 `Width="Auto"+MinWidth` → 可橫向捲動。修了 5 個 result grid + Inspector Name/Type/Value MinWidth(220/160/320→120/110/140)。例外:MethodCall 參數格 / SPC picker。
- **Snapshot UI/UX(SS1-4)** —(SS1)scope `+add assembly` 改 **AutoCompleteBox** 可打字過濾;`Include system assemblies` 在 Scope 有指定 assemblies 時 disable(僅全抓適用)。(SS2)`Game: 32-bit Mono…` banner 併入 Decode budget 列(MaxLines=2);budget 說明改 hover tooltip。(SS3)`FormatMemory` MB/GB 一律 2 位小數(`0.04 MB` 不再顯示 `0 MB`)。(SS4)**FIX 真 x86 bug**:`List<T>._items` 從寫死 +0x10 改 `2*ptrSize`(x86=+0x8)+ `ParseLittleEndianHexQword` 依長度 byte-swap → 32-bit Mono nested List 不再 "header unreadable"。**Live-verified(活俠傳:`StatConvertData._list` 15 元素位址與 CE 逐 byte 一致)**。
- **SS5** — class-list「Inspect (find instances)…」補上切到 Inspector tab(原本只 set target + find,沒切 tab)。
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