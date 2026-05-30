# UnityCEDumper — Status (指標檔 / pointer)

> 這個檔已**瘦身成指標檔**。正典進度帳與**集中待辦清單 (Open Backlog / TODO)**
> 都在 [plan.md](plan.md);完整的舊 session hand-off 詳細紀錄在
> [archive/STATUS-archive-2026-05-29.md](archive/STATUS-archive-2026-05-29.md)
> 與 [archive/status-history.md](archive/status-history.md)。

| 項目 | 值 |
|---|---|
| **Current build** | #839 |
| **Current phase** | ✅ Phase 30 + scan P1/P2 全 SHIPPED & merged to `main` |
| **Branches** | `dev == main == origin` |
| **Tests** | 640 UI tests green;兩個 DLL(x64+x86)build clean |
| **Mid-flight** | 無(nothing in flight) |

**UI 修正批次(2026-05-30,Inspector + Value Search)**:
- **List<T> 可展開** — `InstanceFieldClassifier`/`UnityTypeMap`/`CeXmlExporter` 同時接受 IL-form `List`1<>` 與 live demangled `List<>`;`enchantList_`/`hasSkill_` 等現有展開箭頭 + Copy CE XML 正確 per-slot。
- **Inspector instance 級 Copy CE XML 按鈕** — 跨 tab(Value Search / Address Lookup / Scene / Snapshot)inspect 後可用,不需選欄位/不依賴 Browser。**(已 live-verified)**
- **展開不再跳頂端** — `InsertChildrenIntoFields`/`CollapseSubtree` 無 filter 時直接 splice `FilteredFields`,保留捲動位置。
- **String drilldown 修正** — List<String> slot DLL 回傳裸 pointer;UI 端 fallback 用 `readMemory` 解 managed String,header offset 隨 bitness(x64 +0x10/+0x14、x86 +0x8/+0xC,`StringFieldOffsets`)。**Mono 32-bit live-verified(道德 / karma01 正確)**。
- **Inspector Limit 欄位加大**(`MinWidth=150 Width=160`)。
- **Value Search**:Limit 預設 10000;新增 `numeric (no 1-byte)` / `numeric (all widths)` 聚合型別(fan-out 多寬度去重,one-shot);`Ignore system assemblies` 勾選 + results filter 欄位(client-side,不重掃)。
- **聚合 scan ReverseLookupIndex 重建修正** — 聚合 fan-out 原本每個寬度都重建 ~48s reverse-lookup index(32-bit Mono 8 寬度 → 6+ 分鐘 hang)。新增 process-wide TTL 重用 cache:`ReverseLookupIndex::Build(reuseWindowMs)`(Mono+IL2CPP),`beginValueScan` 收 `reuseIndexMs` arg;聚合首寬度建一次,其餘 7 個複製 sorted bases(ms vs 48s)。單次 / refine scan 不帶 arg → 永遠 fresh(避 GC 失效)。
- **Dictionary 展開** — 暫不處理(`buffAttrRes_`/`buffSickRes_`),需 live `_entries`/`Entry<K,V>` layout 調查。

**先前批次(2026-05-30,scan,均已 merged)**:
- **P2 Mono port**(#823)/ **候選數 gate**(#824)/ **P2 SIMD 內層**(#839)/ **Inspector「→ Browser」按鈕**。

**⚠ 待 live-smoke(需 inject 實機)**:
- **聚合 scan index 重用** — 32-bit Mono `numeric (no 1-byte)`:scan 日誌應出現 1 次 `ReverseLookupIndex(Mono): instances=…` 接著多次 `reused cached index`,總時間 ≈ 單次 build(~1 分內)而非 6+ 分鐘;結果筆數與逐寬度個別 scan 一致。
- List<T> 展開 + Copy CE XML 內容、Value Search 聚合去重結果(原生 scan / walkArray 無自動測試覆蓋)。
- 候選數 gate / P2 SIMD:Utawarerumono(x64)+ 活俠傳(x86)計時 + 結果筆數與舊版一致。

➡ **下一步 / open backlog**:見 [plan.md → Open Backlog / TODO](plan.md#open-backlog--todo)。