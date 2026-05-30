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
| **Tests** | 603 UI tests green;兩個 DLL(x64+x86)build clean |
| **Mid-flight** | 無(nothing in flight) |

**本批次新增(2026-05-30,均已 merged)**:
- **P2 Mono port**(#823)— reverse-lookup interval-tree 移植到 `Mono::WhatIsAt`(value/string scan)。
- **候選數 gate**(#824)— 修掉 index 無條件建立的退步:<5000 候選走每候選 `WhatIsAt`(活俠傳 `10160` 從 51s→~1-2s)。
- **P2 SIMD 內層**(#839)— AVX2 `ScanForString`(anchor)+ `ScanForPointer`(4-qword),runtime-gate + scalar fallback。
- **Inspector「→ Browser」按鈕** — 把當前 instance 的 assembly/class 帶到左側 Assemblies/Classes 過濾欄(保留 `Outer/Inner` slash 形式)。

**⚠ 待 live-smoke(需 inject 實機;原生 scan 無自動測試覆蓋)**:
- 候選數 gate:活俠傳 `10160` 計時 << 51s;廣域 >5000 候選 scan `revIndex=1` 且更快。
- P2 SIMD:Utawarerumono(x64)+ 活俠傳(x86)string scan / find instances — **計時更快 + 結果筆數與舊版完全一致**。

➡ **下一步 / open backlog**:見 [plan.md → Open Backlog / TODO](plan.md#open-backlog--todo)。