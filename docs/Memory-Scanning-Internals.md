# Cheat Engine Memory Scanning Internals

> 本文件基於 CE 開源碼 (Delphi/Object Pascal) 的實際分析，記錄記憶體掃描引擎的核心實作細節。
> 原始重點為 AOB pattern matching；**2026-05-28 擴充** 加入 CE *數值/value scan* 路徑的執行緒模型與對齊策略 (§14)、AOBMaker SIMD 掃描器細節 (§15)，以及 **discrete (in-process 注入式 DLL) 掃描器的現況、瓶頸分析與加速 roadmap (§16)**。
> Sources:
> - CE: `D:\Github\cheat-engine\Cheat Engine\` — `memscan.pas` (~8964 lines), `simpleaobscanner.pas`, `foundlisthelper.pas`, `NewKernelHandler.pas`
> - AOBMaker: `D:\Github\AOBMaker\src\AOBMaker.Platform.Windows\Services\` — `WindowsMemoryScanner.cs`, `WindowsSequenceScanner.cs`
> - discrete: `D:\Github\discrete\dll\src\shared\Memory.cpp`, `il2cpp\IL2CPPAddressFinder.cpp`, `shared\ExportAPI.cpp`
>
> 備份副本同步保存於 `D:\Github\discrete\docs\Memory-Scanning-Internals.md`。

---

## 1. 架構概覽

CE 的記憶體掃描由三層元件構成：

```
TMemScan (高階控制器)
  └── TScanController (掃描協調器)
        └── TScanner[] (工作執行緒 × N)
```

| 元件 | 檔案 | 職責 |
|------|------|------|
| **TMemScan** | `memscan.pas` | 使用者介面層：管理掃描選項、掃描結果檔、進度回報 |
| **TScanController** | `memscan.pas` | 執行緒管理：記憶體區段列舉、工作分配、TScanner 生命週期 |
| **TScanner** | `memscan.pas` | 工作執行緒：讀取記憶體 buffer、執行 pattern matching、寫出結果 |
| **TSimpleAOBScanner** | `simpleaobscanner.pas` | 輕量 AOB 掃描：AutoAssembler 中 `aobscan` / `aobscanmodule` 使用 |

---

## 2. 記憶體區段列舉

### VirtualQueryEx 掃描

```pascal
// TScanController 在 FirstScan 前列舉所有可掃描區段
while VirtualQueryEx(processHandle, address, mbi, sizeof(mbi)) <> 0 do
begin
  if (mbi.State = MEM_COMMIT) and IsValidRegion(mbi) then
    AddRegion(mbi.BaseAddress, mbi.RegionSize);
  address := mbi.BaseAddress + mbi.RegionSize;
end;
```

### 區段過濾

| 條件 | 說明 |
|------|------|
| `MEM_COMMIT` | 僅掃描已提交的頁面 |
| Protection | 排除 `PAGE_NOACCESS`、`PAGE_GUARD` |
| Type | 依使用者設定過濾 `MEM_IMAGE` / `MEM_PRIVATE` / `MEM_MAPPED` |
| Executable | `aobscanmodule` 預設只掃 executable 區段 |
| Writable | 依掃描類型決定是否需要可寫 |

### Module 範圍限定

`aobscanmodule` 使用模組的 base address + size 限定掃描範圍：

```pascal
// simpleaobscanner.pas
moduleBase := getModuleBase(moduleName);
moduleSize := getModuleSize(moduleName);
// 只在 [moduleBase, moduleBase+moduleSize) 範圍內掃描
```

---

## 3. Pattern 表示法

### 資料結構

```pascal
type
  TBytes = array of SmallInt;  // SmallInt (-32768..32767)
```

| 值 | 意義 |
|----|------|
| `0..255` | 字面值 byte，需完全匹配 |
| `-1` (0xFFFF) | 完整 wildcard (`??`)，匹配任何 byte |
| 其他負值 | Nibble wildcard (`?X` 或 `X?`)：`(mask << 8) \| value` 編碼 |

### Pattern 解析

```pascal
// 解析 "48 8B ?? 08" → TBytes
for each token in pattern.Split(' '):
  if token = '??' then
    result[i] := -1          // Full wildcard
  else if token contains '?' then
    result[i] := EncodeNibble(token)  // Nibble wildcard
  else
    result[i] := StrToInt('$' + token)  // Literal byte
```

---

## 4. Pattern Matching 演算法

### 核心：逐 byte 線性比對 + 首位不匹配提前退出

```pascal
function TScanner.ArrayOfByteExact(newvalue, oldvalue: pointer): boolean;
var i: integer;
begin
  for i := 0 to abs_arraylength - 1 do
    if (abs_arraytofind[i] <> -1) and           // 非 wildcard
       (pbytearray(newvalue)[i] <> abs_arraytofind[i]) then
    begin
      result := false;
      exit;  // ← 首位不匹配立即退出
    end;
  result := true;
end;
```

### Nibble Wildcard 變體

```pascal
function TScanner.ArrayOfByteExact_NibbleWildcardSupport(newvalue, oldvalue: pointer): boolean;
var i: integer;
    nibblevalue: word;
    mask, expected: byte;
begin
  for i := 0 to abs_arraylength - 1 do
  begin
    if abs_arraytofind[i] = -1 then continue;  // Full wildcard

    if abs_arraytofind[i] < 0 then  // Nibble wildcard
    begin
      nibblevalue := word(abs_arraytofind[i]);
      mask := (nibblevalue shr 8) and $FF;
      expected := nibblevalue and $FF;
      if (pbytearray(newvalue)[i] and mask) <> expected then
      begin
        result := false;
        exit;
      end;
    end
    else  // Literal byte
    begin
      if pbytearray(newvalue)[i] <> abs_arraytofind[i] then
      begin
        result := false;
        exit;
      end;
    end;
  end;
  result := true;
end;
```

### 重要：**沒有使用 Boyer-Moore 或 KMP**

CE 的 AOB 掃描使用最基本的 **滑動視窗逐 byte 前移** (O(n×m))，依賴首位不匹配提前退出來加速。對於典型遊戲行程（幾百 MB 記憶體、幾十 byte 的 pattern），這已足夠快。

---

## 5. 緩衝區管理

### 讀取策略

```pascal
const
  buffersize = 4 * 1024 * 1024;  // 4MB per chunk
```

每個 TScanner 執行緒：

1. 從分配的記憶體區段起始位置開始
2. 以 4MB 為單位呼叫 `ReadProcessMemory`
3. 每次讀取 `buffersize + (pattern_length - 1)` bytes（處理跨 chunk 邊界的 pattern）
4. 在 buffer 內做線性掃描
5. 前進 `buffersize` bytes，重複直到區段結束

每次讀取多讀 `variablesize - 1` bytes（AOB 掃描時 `variablesize` = pattern 長度），確保不遺漏跨 chunk 邊界的匹配：

```pascal
// memscan.pas line ~5720
if size < toread then  // 後面還有資料
  ReadProcessMemory(phandle, pointer(currentbase),
    memorybuffer, size + variablesize - 1, actualread)
else  // 最後一個 chunk
  ReadProcessMemory(phandle, pointer(currentbase),
    memorybuffer, size, actualread);
```

CE 偵測相鄰區段是否記憶體連續（`canOverlap`），連續時可跨區段掃描：

```pascal
// memscan.pas line ~5820
if ((i+1) < memRegionPos - 1) and
   (currentbase + toread = memregion[i+1].BaseAddress) then
  canOverlap := (scanOption <> soUnknownValue);
```

### Buffer 配置

使用 `VirtualAlloc`（非一般堆積）取得頁面對齊的記憶體：

```pascal
memorybuffer := VirtualAlloc(nil,
  maxregionsize + variablesize + 16,
  MEM_COMMIT or MEM_RESERVE or MEM_TOP_DOWN,
  PAGE_READWRITE);
```

### 結果緩衝

- `CurrentAddressBuffer`：預配置的位址陣列
- `CurrentFoundBuffer`：預配置的匹配值陣列
- 滿了就 flush 到磁碟（`ADDRESSES.<name>` + `MEMORY.<name>` 檔案）
- `Tscanfilewriter` 使用**雙緩衝**寫入：掃描繼續同時 I/O 非同步寫出
- `TFoundList` lazy-load 結果（1024 entry cache + AVL tree 位址搜尋）

---

## 6. 執行緒模型

### First Scan (首次掃描)

```
TScanController
  ├── TScanner[0]: 區段 0 ~ N/4
  ├── TScanner[1]: 區段 N/4 ~ N/2
  ├── TScanner[2]: 區段 N/2 ~ 3N/4
  └── TScanner[3]: 區段 3N/4 ~ N
```

- 執行緒數 = `threadcount`（預設 = CPU 核心數）
- 記憶體區段平均分配給各執行緒
- 各執行緒獨立讀取、掃描、寫出結果
- 主執行緒 `WaitTillDone(25)` 輪詢等待 + GUI 更新

### Next Scan (重掃描)

- 從上次結果的位址清單掃描
- 按 **4K 頁面邊界** 分批讀取（減少 `ReadProcessMemory` 次數）
- 同樣多執行緒，但每執行緒處理一段位址範圍

### 執行緒同步

```pascal
// TScanController 等待所有 TScanner 完成
for i := 0 to threadcount - 1 do
begin
  while not (terminated or scanners[i].isdone or scanners[i].Finished) do
  begin
    scanners[i].WaitTillDone(25);  // 25ms 輪詢
    if progressbar <> nil then
      synchronize(updategui);
  end;
  inc(OwningMemScan.found, scanners[i].totalfound);
end;
```

---

## 7. 提前終止

### OnlyOne 模式

```pascal
// 在 firstscanmem 掃描迴圈中
if (OnlyOne and (found > 0)) then exit;
```

`aobscan` / `aobscanmodule` 只需要找到一個匹配即可停止（但預設不啟用 — AutoAssembler 通常檢查是否恰好 1 個匹配）。

### MaxResults 限制

AOBMaker 的 `WindowsMemoryScanner` 實作了 `MaxResults` 提前終止：

```csharp
if (maxResults > 0 && results.Count >= maxResults)
{
    Interlocked.Exchange(ref earlyStop, 1);
    loopState.Stop();
    return;
}
```

**對 Mode D 的意義**：判斷唯一性只需 `MaxResults = 2`，一旦找到第 2 筆即可停止。

---

## 8. SimpleAOBScanner (`simpleaobscanner.pas`)

AutoAssembler 中的 `aobscan` / `aobscanmodule` 使用獨立的輕量掃描器：

```pascal
type
  TSimpleAOBScanner = class
    function scan(pattern: string; moduleBase, moduleSize: QWORD): TQWordArray;
  end;
```

API 函數：

| 函數 | 用途 | `OnlyOne` |
|------|------|-----------|
| `findaobInModule(pattern, module)` | AA `aobscanmodule` | Yes — 找到第一筆即停止 |
| `findaob(pattern)` | AA `aobscan` | Yes — 全行程搜尋，第一筆停止 |
| `getaoblist(pattern)` | 取得所有匹配 | No — 收集全部結果 |
| `AsyncAOBScan(pattern)` | 非同步掃描 | 依參數 |
| `FinishAOBScan(scanId)` | 等待非同步結果 | N/A |

特點：
- 封裝 `TMemScan`，設定 `onlyone := true` 後呼叫 `firstscan(soExactValue, vtByteArray, ...)`
- `findaobInModule` 透過 `symhandler.getmodulebyname()` 取得模組 base+size，限定掃描範圍
- 單執行緒（內部仍走 TMemScan 多執行緒管線，但結果處理是單一呼叫端）
- 無結果暫存到磁碟
- 適用於小範圍掃描（單一模組）

---

## 9. Fast Scan 對齊

```pascal
// TScanner 屬性
fastscanalignsize: integer;   // 對齊大小 (1, 2, 4, 8, 16)
fastscanmethod: TFastScanMethod;
```

| 方法 | 說明 |
|------|------|
| `fsmNotAligned` | 逐 byte 掃描 (align=1) |
| `fsmAligned` | 每隔 `alignsize` bytes 掃描 (AOB 預設 align=1) |
| `fsmLastDigits` | 根據值的末幾位決定對齊（數值掃描用） |

AOB 掃描通常使用 `align=1`（不對齊），因為機器碼不保證對齊。

---

## 10. 效能特性

### CE 掃描速度參考

| 場景 | 典型耗時 |
|------|----------|
| 50,000 筆匹配 (aobscan 全模組) | < 1 秒 |
| 首次掃描 2GB 行程空間 | 2-5 秒 |
| `aobscanmodule` 單一 100MB 模組 | < 0.5 秒 |

### 效能瓶頸分析

| 瓶頸 | CE 作法 | AOBMaker 作法 |
|------|---------|---------------|
| `ReadProcessMemory` syscall | 4MB chunk 批次讀取 | 64MB chunk (`MaxRegionSize`) |
| Pattern matching | 線性掃描 + 首位退出 | 線性掃描 + 首位退出 |
| 多執行緒 | CPU 核心數個 TScanner | `Parallel.ForEach` (Max=CPU cores) |
| 結果數量 | 全部收集 + 磁碟暫存 | `MaxResults` 提前終止 |
| 區段過濾 | Module 範圍限定 | `ExecutableOnly` + 區段類型過濾 |

### 0-match 問題

若 pattern 在記憶體中不存在（如位址錯誤），掃描必須遍歷所有區段才能確認 0 筆。這是最慢的情境：

- CE: 同樣需要全掃，但多執行緒 + 4MB buffer 讓延遲可控
- AOBMaker: `MaxResults=2` 無法提前終止 → 完整遍歷所有區段

---

## 11. AOBMaker 與 CE 的差異

| 面向 | CE | AOBMaker |
|------|-----|---------|
| 語言 | Delphi (Object Pascal) | C# (.NET) |
| Pattern 來源 | 使用者輸入 hex string | Iced.NET 指令分析 + Register masking |
| Wildcard | `??` (full) + nibble (`?X`) | `??` (full) only |
| 掃描範圍 | 模組限定或全行程 | `ExecutableOnly` 區段過濾 |
| 結果暫存 | 磁碟檔案 (支援數百萬筆) | 記憶體 `ConcurrentBag<ulong>` |
| SIMD 加速 | 無 | 無 |
| 首位最佳化 | `abs_arraytofind[0]` 直接比對 | `mask[j] && buffer[i+j] != pattern[j]` |
| Buffer 大小 | 4MB | 64MB |
| Mode D 最佳化 | N/A | 預計算 token array + byte-by-byte 擴展 |

---

## 12. 可改進方向

基於 CE 分析的啟示，AOBMaker 的 `WindowsMemoryScanner` 可考慮以下最佳化：

### 12.1 首位跳過 (First-Byte Skip)

如果 pattern 的第一個 byte 不是 wildcard，可以先用 `IndexOf` 或類似快速搜尋找到候選位置：

```csharp
// 快速跳過不匹配的首位 byte
byte firstByte = pattern[0];
while (i <= maxIndex)
{
    if (buffer[i] != firstByte) { i++; continue; }
    // 接著比對完整 pattern...
}
```

### 12.2 SIMD 加速

.NET 的 `System.Runtime.Intrinsics` 可用 SSE2/AVX2 一次比對 16/32 bytes：

```csharp
// 使用 Vector128/Vector256 進行首位 byte 快速定位
var needle = Vector256.Create(firstByte);
// 每次前進 32 bytes 找候選位置
```

### 12.3 N=2 提前終止

Mode D 判斷唯一性只需知道「是 0、1、還是 ≥2」：

```csharp
ScanOptions.MaxResults = 2;  // 找到 2 筆即停止
```

此最佳化已在目前實作中套用。

---

## 13. CE 原始碼參考檔案

| 檔案 | 行數 (約) | 內容 |
|------|-----------|------|
| `memscan.pas` | ~8000 | TMemScan, TScanController, TScanner — 完整掃描引擎 |
| `simpleaobscanner.pas` | ~200 | 輕量 AOB 掃描 (AutoAssembler 用) |
| `foundlisthelper.pas` | ~1500 | TFoundList — 結果載入、排序、搜尋 |
| `NewKernelHandler.pas` | ~3000 | VirtualQueryEx, ReadProcessMemory 封裝 |
| `autoassembler.pas` | ~5000 | `aobscan` / `aobscanmodule` 命令處理 |
| `LuaMemscan.pas` | ~800 | Lua 腳本介面：`createMemScan`, `memscan_*` |

---

## 14. CE 數值 / Value Scan 路徑 (2026-05-28 擴充)

§1–13 聚焦 AOB pattern matching。數值掃描 (`soExactValue` + `vtDWord`/`vtSingle`…) 走同一個 `TScanner` 管線，但有幾個對「快」最關鍵的細節，以下行號對應 `memscan.pas`。

### 14.1 執行緒模型 — 這是 CE 速度的主因

```pascal
threadcount := GetCPUCount;            // line 6637 — 預設 = CPU 核心數
Blocksize  := totalProcessMemorySize div threadcount;
Blocksize  := blocksize - (blocksize mod 4096);   // line 6678 — 對齊 4K 頁
```

- FirstScan 依 `Blocksize` 把**記憶體區段平均切給 N 條 `TScanner`**（line 6689–6753），各執行緒獨立讀取/掃描/寫結果，最後一條掃到尾端。
- 例外降為單執行緒：Lua formula scan（6643）、custom Lua type（6639）。
- `scannersCS` critical section 保護 scanner 陣列建立（6682–6693）。
- 主執行緒 `WaitTillDone(25)` 25ms 輪詢 + GUI 更新後 join。
- NextScan（重掃）同樣 `threadcount := GetCPUCount`（line 6459）。

### 14.2 Fast-scan 對齊 — 數值掃描跳過 3/4 位置

```pascal
case variableType of
  vtByte:   fastscanalignsize := 1;   // line 6131
  vtWord:   fastscanalignsize := 2;   // 6137
  vtDWord:  fastscanalignsize := 4;   // 6143 ← 4-byte int 預設
  vtSingle: fastscanalignsize := 4;   // 6149
  vtDouble: fastscanalignsize := 4;   // 6161（注意是 4 不是 8）
  vtQWord:  fastscanalignsize := 4;   // 6155
end;
// 內層迴圈：stepsize := fastscanalignsize; inc(p, stepsize);  // 3887 / 4011
```

`fsmAligned`（預設）以 `alignsize` 為 stride → DWord scan 只比對 1/4 的位址。`fsmNotAligned` 才 stride=1。

### 14.3 數值比對內層 — 純量、無 SIMD

```pascal
function TScanner.DWordExact(newvalue, oldvalue: pointer): boolean;
begin
  result := pdword(newvalue)^ = dword(value);   // line 3108 — 單一 load + CMP
end;
```

CE 的數值比對是**最樸素的指標解參考 + `=`**，沒有任何向量化。它的快來自「對齊 stride + 多執行緒」，不是內層指令。

### 14.4 Buffer / 讀取策略

- 結果 buffer 依型別 `buffersize * varsize`，上限 **16 MB**（line 5374），`CurrentFoundBuffer` + `SecondaryFoundBuffer` 雙緩衝（5483–5484），`Tscanfilewriter` 非同步寫磁碟。
- old-value buffer 以 `VirtualAlloc(MEM_TOP_DOWN)` 配置（5560/5581）。
- **NextScan 以 4K 頁分批**：把同一頁的位址聚成一次 `ReadProcessMemory`（4430–4562, `and $FFFFFFFFFFFFF000`）→ 大幅減少 syscall。

### 14.5 其他加速

- `VirtualQueryEx_StartCache / EndCache`（line 7095）快取區段 metadata，避免重複 OS 呼叫。
- `workingsetonly` + `QueryWorkingSet`（7099–7165）：只掃實體常駐頁，避免 page fault。
- `OnlyOne` 一找到就 `exit`（3985–3989, 4004–4008）。
- 區段過濾（7188–7242）：`MEM_COMMIT` only、排除 `PAGE_GUARD`/`PAGE_NOACCESS`、`MEM_PRIVATE`/`MEM_IMAGE`/`MEM_MAPPED` 與 writable/executable 皆可由 UI 設定。

---

## 15. AOBMaker SIMD 掃描器 (C#/.NET，2026-05-28 擴充)

`WindowsMemoryScanner.cs` 是跨行程 (RPM) 掃描，但其 **SIMD + 平行化** 設計值得借鏡。

### 15.1 平行化 + 分塊

```csharp
const int MaxRegionSize = 64 * 1024 * 1024;          // line 50 — 64 MB chunk
// 大區段先切成 64MB chunk（296–302），再：
Parallel.ForEach(regions, new ParallelOptions {
    MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount)   // line 126
}, ...);
```

- 工作單位 = `MemoryRegion`（per-region 平行）。`ArrayPool<byte>.Shared.Rent(region.Size)`（line 153）借 buffer 免 GC，`finally` 歸還。
- AntiCheatSafeMode → `maxThreads = 1` + 16 MB chunk。

### 15.2 SIMD 比對：anchor byte + Vector256/128

```csharp
// FindAnchorOffset(): 找 pattern 第一個非 wildcard byte 當 anchor（256–264）
var needle = Vector256.Create(anchorByte);                      // 173
var chunk  = Vector256.LoadUnsafe(ref buffer[0], i+anchorOffset);// 178
uint bits  = Vector256.Equals(chunk, needle).ExtractMostSignificantBits(); // 179
while (bits != 0) {
    int cand = BitOperations.TrailingZeroCount(bits);           // 183
    if (MatchPatternNibble(buffer, cand, ...)) results.Add(...);// 184 完整 nibble 驗證
    bits &= bits - 1;                                           // 194 清掉最低 set bit
}
```

- 一次比對 32 bytes 找 anchor 候選，再逐候選做 nibble-level 完整驗證（267–276，含 `0xF0/0x0F/0xFF` mask）。有 `Vector128` fallback 與純量 fallback。
- `[MethodImpl(AggressiveInlining)]` 於 matcher / anchor finder。

### 15.3 提前終止 + 結果收集

```csharp
// 原子計數，達標即停所有執行緒
if (Interlocked.Increment(ref matchCount) >= maxResults) {
    Interlocked.Exchange(ref earlyStop, 1);  loopState.Stop();  // 187–190
}
// 其他執行緒進入 region 前檢查 Volatile.Read(ref earlyStop)（130–133）
ConcurrentBag<ulong> results;                                   // 115 — lock-free
```

`SequenceScanner` 變體用 per-thread `List<ulong>[]` 累積再合併，並有 per-hit cap 防「搜 0 爆量」。

---

## 16. discrete (in-process 注入式 DLL) 掃描器 — 現況、瓶頸、加速 roadmap

> discrete 是**注入進遊戲行程**的 DLL，因此 **不付 `ReadProcessMemory` 成本**（直接讀記憶體），這是相對 CE/AOBMaker 的天生優勢。但同時我們做的事「不只是掃記憶體」：value scan 對每筆 raw 命中還要做 Unity-aware 的反查 (`WhatIsAt`)，snapshot 要走整個 GC heap 建實例樹。以下分析對應 build #741 程式碼。

### 16.1 現況架構（全部 single-threaded）

| 函式 | 檔案:行 | 做什麼 | 現狀 |
|------|---------|--------|------|
| `EnumerateCommittedRegions` | `Memory.cpp:115` | `VirtualQuery` 由低到高列舉 commit 區段 | 單次、單緒 |
| `ScanForPointer` | `Memory.cpp:298` | findInstances / listFieldValues 用，掃 8-byte == class ptr | 單緒、升冪、湊滿 limit 即 break、**Phase 29h arena 排除** |
| `ScanForValue` | `Memory.cpp:544` | 數值掃描 (int/float)，64KB chunk、每 chunk 先 `IsReadable`、template 分派的 typed 內層 | 單緒、**無 SIMD**、有 alignment stride |
| `ScanForString` | `Memory.cpp:668` | byte-sequence 掃描，每 stride 位置 `memcmp` | 單緒、**無 SIMD**、chunk overlap = needle-1 |
| `WhatIsAt` | `IL2CPPAddressFinder.cpp` | 由 addr 往回掃最多 `maxBackwardScan`(預設 16KB)、對 `classSet` 二分搜驗證 | 單緒、**每筆候選都重掃** |
| `FindAllInstances` | `IL2CPPAddressFinder.cpp:693` | snapshot：單趟走 heap，每個 8-aligned qword 二分搜 classSet，per-class cap | 單緒、單趟 |

**value scan 是兩階段**（`ExportAPI.cpp` `beginValueScan` / `scanFieldValue`）：
1. `ScanForValue`（raw，cap 50000）→ 純記憶體掃描。
2. **對每筆 raw 候選呼叫 `WhatIsAt(addr, 16384)`** → 每筆往回掃最多 2048 個 qword + 對 ~10K 的 `classSet` 二分搜，再套 noise / per-instance(=3) / diversity 過濾。

→ 最壞情況 50000 候選 × 2048 qword 反查 ≈ 1 億次讀取 + 二分搜，**這通常才是 value scan 慢的主因，不是 raw 掃描本身**。這就是「我們不是單純 scan memory」。

### 16.2 瓶頸排序（ROI 由高到低）

1. **全程 single-threaded**（raw 掃描 + snapshot heap walk）。CE 與 AOBMaker 都 fan-out 到 CPU 核心數；我們序列走區段。多 GB heap (SaGa F2R ~6–8 GB) 下這是 wall-clock 地板。
2. **value scan 每候選 `WhatIsAt` 反查**（§16.1 第 2 階段）。單緒、序列、每筆重掃 16KB。
3. **每 64KB chunk 都 `IsReadable`(=VirtualQuery)**。`EnumerateCommittedRegions` 已濾掉 `PAGE_GUARD`/`PAGE_NOACCESS`，再對每個 64KB chunk 重做一次 VirtualQuery 等於把 syscall 量放大 (regionBytes/64KB) 倍；CE 反而是「相信區段表 + VQ 快取」。
4. **內層無 SIMD**。但注意：`ScanForValue` 已有 alignment stride（int32 → stride 4，已跳過 3/4），SIMD 對 stride>1 的數值掃描增益有限；**最值得 SIMD 的是 `ScanForString`（stride=1 的 `memcmp`）與 `ScanForPointer`（8-byte target compare）**。
5. **snapshot 單趟但單緒**（同 #1）。

### 16.3 加速 roadmap（建議順序）

> **狀態（2026-05-29, build #751）**：P1 兩項皆 **已實作** — 見 §16.5。P2/P3 仍 pending。

**P1 — 拿掉 value/string scan 的 per-chunk `IsReadable`（低風險、改動小）** ✅ 已實作
相信 `EnumerateCommittedRegions` 的區段表，內層維持 `__try` SEH 保護；只在某 chunk AV 時才針對該區段降級細掃。直接省掉每 64KB 一次 VirtualQuery。CE 等價作法 = `VirtualQueryEx_StartCache` + working-set。

**P1 — 平行化區段走訪**（`ScanForValue` / `ScanForString` / `ScanForPointer` / `FindAllInstances`）✅ 已實作
固定大小 thread pool（建議 `min(cores-2, N)`，與專案 workflow 併發上限同哲學，避免和遊戲自身執行緒搶滿核心），per-thread 結果 buffer 最後合併，`limit` 改用 atomic counter + 提前停（對照 AOBMaker `Interlocked` + `loopState.Stop`）。**這是對多 GB heap 最大的 wall-clock 改善**。注意：in-process 掃描自己的 heap，平行讀是安全的（唯讀）；唯一要小心的是不要在 attach Unity domain 的執行緒外呼叫 runtime API（raw 掃描階段不碰 runtime，安全）。

**P2 — value scan 候選反查改用「快取的 heap interval-tree」**（address-finder.md Strategy B，目前 deferred）
掃描前用一次 `FindAllInstances` 式單趟 heap walk 建 `(start,end,klass)` 排序表，之後每候選 **二分搜區間 O(log N)** 取代每筆 16KB 反掃 O(2048)。或最低成本先加 **spatial-locality 快取**：相鄰候選常落在同一 instance，命中 `[lastInstance, lastInstance+size)` 就直接重用，免再反掃。亦可把 `WhatIsAt` 批次平行化。

**P2 — SIMD 內層**：優先 `ScanForString`（`Vector256` anchor-byte 定位 + `memcmp` 驗證，對照 AOBMaker §15.2）與 `ScanForPointer`（`Vector256.Equals` 一次比 4 個 qword target）。`ScanForValue` 因已有 alignment stride，增益較小，列為後段。

**P3 — VirtualQuery 快取**：若任何路徑仍保留 per-chunk 探測，導入 CE 式 region-metadata 快取。

**已是好的部分（不需動）**：in-process 直讀（無 RPM）、`ScanForValue` template 分派消除逐 byte branch、alignment stride、chunked SEH 限縮 AV 影響範圍、結果 `reserve()`、Phase 29h arena 排除已大幅降低 `WhatIsAt` 在 metadata 殘留上的浪費。

### 16.4 與 CE / AOBMaker 對照

| 面向 | CE | AOBMaker | discrete 現況 | discrete 建議 |
|------|----|----------|---------------|---------------|
| 跨/同行程 | RPM 跨行程 | RPM 跨行程 | **in-process 直讀** ✅ | 維持 |
| 平行度 | CPU 核心數 TScanner | `Parallel.ForEach` (cores) | **單緒** ❌ | P1 平行化 |
| 區段探測 | VQ 快取 + working-set | 一次列舉 | **每 64KB 重做 VQ** ❌ | P1 拿掉 |
| 對齊 stride | fsmAligned (DWord=4) | pattern stride=1 | int 已 stride=4 ✅ | 維持 |
| 內層 SIMD | 無（純量） | Vector256/128 + anchor | 無 | P2（先 string/pointer） |
| 提前終止 | OnlyOne | `Interlocked`+Stop | limit break（單緒） | P1 改 atomic |
| 結果收集 | 雙緩衝 + 非同步磁碟 | `ConcurrentBag` | `std::vector` | 平行後改 per-thread 合併 |
| Unity-aware 反查 | N/A | N/A | **每候選 16KB 反掃** ❌ | P2 interval-tree / locality cache |

**一句話結論**：我們的內層比 CE 還省（in-process 直讀），慢的根因是 **(a) 完全沒平行化**、**(b) value scan 對每筆候選做昂貴的 16KB Unity 反查**。先做 P1（平行化 + 拿掉 per-chunk VQ）拿到數倍 wall-clock，再做 P2（候選反查改 interval-tree / locality cache）解決 value-scan 的二次成本。

### 16.5 P1 實作紀錄（2026-05-29, build #751）

**`Memory.cpp` 新增平行掃描基礎建設**（anon namespace）：
- `ScanThreadCount()`（已 export 到 `Memory.h`）= `clamp(hardware_concurrency - 2, 1, 16)`，留核心給遊戲 + pipe/UI 執行緒。
- `ScanWorkItem{base, scanSize}` + `BuildScanChunks(regions, elemSize, stride)`：把已過濾區段切成 `kScanWorkChunk = 1 MB`、**stride 對齊**的 chunk worklist。每個 chunk `scanSize = min(ownLen + elemSize-1, rend-base)` 折入尾端 overlap，使跨 chunk 接縫的元素仍被「擁有其起始位置的那個 chunk」找到恰好一次（數學推導見原始碼註解；8/8 的 pointer/snapshot 情形 overlap 自然為 0）。
- `RunParallelScan<ResultT>(chunks, limit, &readFailureBytes, chunkScanFn)`：N 執行緒以 `atomic nextChunk.fetch_add` work-steal，結果寫入 `perChunk[idx]` slot（依 index 串接 → **位址升冪、免排序**）；`atomic totalMatches` 達 `limit` 即提前停（grabbed 集合恆為低 index 連續前綴 → 截斷保留最低位址，符合舊序列語意）。`nthreads<=1` 走 inline 不開執行緒。
- `FilterScanRegions(skipMapped)`：序列過濾 (RW / 非 image / 選擇性非 mapped / 非 stack)，一次填好 region-level 統計。
- **拿掉 per-chunk `IsReadable`**：region 表已濾 guard/no-access，內層 `__try` 接住罕見 AC-stripped page（只損該 1 MB chunk，計入 `readFailureBytes`）。

**三個掃描器改寫**：`ScanForPointer`（stride/elem=8，沿用 Phase 29h arena 排除，per-call local `exc` 計數→atomic 合併）、`ScanForValue`（stride=alignment 或 sizeof(T)）、`ScanForString`（elem=needle 長度，overlap 取代舊的手動 chunk overlap）皆改為 `FilterScanRegions → BuildScanChunks → RunParallelScan`。預設參數使 Mono 等其他呼叫者行為不變。

**`IL2CPPAddressFinder.cpp::FindAllInstances`（snapshot）平行化**：序列過濾 → 8 對齊 1 MB chunk → N 執行緒各自 `SehSweepRegion` 進 **per-thread `byClass` map**（熱路徑無鎖）→ 合併時每個 class 依位址排序 + 截斷到 `perClassCap`。**保留「最低位址子集」決定性**（避免跨 snapshot diff 因執行緒時序而抖動）；代價是 per-thread map 暫態記憶體 ×nthreads（多數 class 遠低於 cap，僅少數熱門 class 達 cap，整體與單緒同數量級）。log 末尾加 `threads=N`。

**尚未做（follow-up）**：Mono `MonoAddressFinder.cpp::FindAllInstances`（用 `SehSweepRegionLoad` + slot 驗證，較複雜，且目前目標為 IL2CPP）仍單緒；value/string/pointer 因走共用 `Memory.cpp` 路徑，Mono 端已連帶平行化。P2（候選反查 interval-tree / locality cache）、P2 SIMD（string/pointer 內層）、P3（VQ 快取）仍 pending。

**驗證重點**：correctness — 平行掃描的 match 集合須與舊序列版一致（位址升冪、同筆數，截斷時保留最低位址）；perf — 多 GB heap 的 value scan / find instances / snapshot wall-clock 應呈核心數倍率下降。

### 16.6 Snapshot 記憶體 watchdog（2026-05-29, build #756）

**問題**：snapshot capture 把**整個** decode 結果（每 class × 每 instance × 每 field，深度 4 + arrays）建在**遊戲行程**裡才透過 pipe 串流出去（注入式 DLL 的配置 = 遊戲行程記憶體）。大型遊戲（22157 classes / 799174 instances / 7.27 GB heap）一路長到 8 GB→ 玩家強制關閉。`FindAllInstances`（P1c 平行化）本身已完成並 log；爆掉的是**其後的 decode 階段**，且 decode 完全沒有總量上限。對照正常完成的 SaGa 2 FR：10001 classes/271998 instances→ 實際保留 2451 classes/118161 instances、~4.5 GB、~42s。

**修法**（`ExportAPI.cpp` 兩個 backend 的 `snapshotBegin`）：decode loop 加 **process-memory watchdog**。
- 預算 `budget = 開拍前 privateUsage + headroom`；`headroom` 預設 4 GB，並夾到 `0.6 × 可用實體記憶體`（`GlobalMemoryStatusEx`），可用 `memHeadroomMB` 參數覆寫。
- 每 `kSnapshotMemCheckInterval = 2048` 個 instance 取樣一次 `GetProcessMemoryInfo().PrivateUsage`（不放熱路徑）；超過預算就設 `memBudgetHit`，停止再加 instance，剩餘 class 只輸出 metadata（`"truncated":"memory_budget"`）。
- response 加 `truncatedByMemory` + `decodeStats.{memBudgetBytes,peakPrivateUsage}`；log 末尾加 `MEM_TRUNCATED=1 memBudget=… peakPriv=…`。

→ 大型遊戲不再無限長到 OOM，而是在預算處停下並回報截斷；UI 可據 `truncatedByMemory` 提示使用者「縮小 InstanceCap / 指定 assemblies / 重啟遊戲」。**後續**：snapshot 改 streaming-decode（邊 decode 邊送、不全握在記憶體）才是根治。

### 16.7 Snapshot「減少擷取量」(2026-05-29, build #761)

**動機**：受讚頌者（IL2CPP）live-smoke — find phase 僅 0.6s（P1c 良好），但 **decode phase ~11 分鐘**：665059 找到 / 263700 in-scope / decode 174079 instances，遊戲漲到 10 GB、UI 收完漲到 14.7 GB。decode 是唯一瓶頸，且 600s 與記憶體成正比於 instance 數。使用者反饋「是否抓到非必要 garbage」。

**修法**（`ExportAPI.cpp` 兩 backend）：
- **`instanceDecodeBudget`**（預設 50000，0=無限，可覆寫）：decode 累計到上限即停，剩餘 class 只輸出 metadata（`"truncated":"instance_budget"`）。直接夾住 decode **時間 + 記憶體**，不依賴 free RAM（記憶體 watchdog 是另一道）。
- **plan 依 instance 數升冪 `stable_sort`**：truncation（無論 budget 或記憶體）先砍**高 population 的雜訊 class**（Node / Entry / 小容器），保留**低 population 的遊戲狀態 class**（CharacterData / PlayerStatus…）。把「截斷」變成有意義的取捨。
- response/log 加 `truncatedByCount` / `COUNT_TRUNCATED=1` / `instBudget=`。
- UI `kDefaultInstanceCapX64` 512→256；snapshot 完成時若 `truncatedByMemory`/`truncatedByCount` 顯示 ⚠ PARTIAL 提示（縮小 assembly scope / 調 budget）。

**效果預期**：50000 instance 上限 → decode 時間/記憶體約降到原本的 50000/174079 ≈ 29%，且保留的是低 population 的有用 class。要完整抓特定 class → 縮 assembly scope 或調高 budget。**仍是 workaround**；root fix = streaming-decode + decode 平行化（per-thread attach）。

### 16.8 Phase 30a-1 — schema/value dedup wire（2026-05-29, build #766）

**已實作（IL2CPP emit + UI re-expand）**：DLL `snapshotBegin` 改成每 class 只送一次
`schema`（name/type/offset/from/is_enum/is_value_type，依 offset 排序），每 instance
只送 `v[]`（value-only：address/value/ptr/string/arrayElements… 無 schema keys）。
消除「174K× 重複 field metadata」→ **遊戲端 `session->classes` nlohmann tree 大幅縮小**
（~4.5GB→~1GB 量級）+ wire bytes 變小。UI `SnapshotChunkResult.NormalizeDeduped()`
在收 chunk 時把 `schema+v` 還原成原本的 `SnapshotFieldEntry`（填回 schema keys），所以
diff/Pivot/SPC/export/SQLite/全部 consumer + 591 個測試 **零改動**。3 個新測試覆蓋
re-expand。

**尚未做**：
- **30a-2（UI retention dedup）**：UI 改成保留 deduped 模型（`SnapshotFieldSchema` +
  per-instance value 陣列），不再 re-expand 成 12M 個 `SnapshotFieldEntry` → 才會打到
  UI 端 14.7GB。需改 diff/FieldIndex/Pivot/SPC/export/SnapshotStore + 向後相容讀舊
  snapshot。**(broad refactor，gated)**
- **Mono emit dedup**（目前 Mono 仍送舊 inline 格式；UI re-expand 兩種都吃，故相容）。
- **30b/30c streaming**（見 docs/snapshot-streaming-dedup.md）。
