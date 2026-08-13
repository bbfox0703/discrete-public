# Audit #4 實機 smoke 檢查表(2026-08-13)

> [audit-2026-08-13.md](audit-2026-08-13.md) 九批修正的實機驗證清單。
> 全部靜態驗證與自動化都已通過(build #1402 AOT publish clean、1130 UI tests green);
> **這份清單是唯一剩下的未驗證面**。
>
> **依「你面前需要有什麼」分組**,不是依批次 —— 一次遊戲 session 可以一口氣掃完一整組。
> 每項都寫了 **PASS 長什麼樣**與 **FAIL 長什麼樣**,因為這輪修的缺陷多半是**靜默**的
> (錯的欄位名、少一半的陣列元素、回繞的數值),不看徵兆會以為「沒事」。

**優先順序**:G1 → G2 → G6 是最高價值(涵蓋最嚴重的修正 + 最可能回歸的路徑);
G3/G4/G7 需要特定目標,有機會再補;G5/G8 屬確認性質。

---

## G1 — 任一 Unity 遊戲(UI 注入)· 最高優先

只要能注入的遊戲都行,x64 Mono 或 IL2CPP 皆可。**一次 session 可驗 8 項**。

- [ ] **P2 · ScanCancel 陳舊旗標**
  跑一次 value scan → **中途按 Cancel** → 接著跑 **Scan field value**。
  **PASS**:Scan field value 回正常結果。
  **FAIL**:回空白或明顯錯誤的結果(修正前:取消旗標殘留 → reverse-lookup index 在第一個 region 就中止,
  卻仍被標成 valid 發佈進全域快取)。

- [ ] **E-4 / E-5 · 掃描中關閉 UI**
  開始一個長掃描 → **直接關掉 DiscreteUI**(不要按 Cancel)。
  **PASS**:遊戲行程的 CPU 回落;工作管理員看不到單一核心 100%。
  **FAIL**:某個核心維持 100% 直到掃描自然結束(修正前:`Stop()` 關閉 `g_stopEvent` 後,
  monitor 執行緒的 `WaitForSingleObject(nullptr)` 立即返回 → 無 sleep 的緊密空轉)。

- [ ] **E-3 · session 滯留**
  跑一次大型 snapshot → 關閉 UI → 看遊戲行程的記憶體。
  **PASS**:記憶體回落(最後一個 client 離開時 session 被釋放)。
  **FAIL**:數十 MB 一直留著不還。

- [ ] **F-1 · BulkGate lease**(使用者立即有感)
  Inspector 跑 **Find instances**,**在它還在跑的時候**按 **Recall bookmark**。
  應看到「Another bulk operation is running」提示。等 Find 結束後,**再跑一次任何 bulk 指令**。
  **PASS**:後續 bulk 指令正常可用。
  **FAIL**:所有 bulk 指令從此失效,直到重開 app(修正前:失敗的 `TryEnter` 把 Find 的 lease 覆寫成 null,
  gate 永不釋放)。

- [ ] **F-2 · AobMaker 並發**
  快速連續觸發兩個 AobMaker 命令(例如 `Copy: aobscan` 後立刻按連線列的 ↻)。
  **PASS**:兩者各自完成或乾淨失敗。
  **FAIL**:`ObjectDisposedException`、回應內容交錯、或 chip 狀態亂跳。

- [ ] **I-1 · `ulong` 欄位寫入**(需要一個 `ulong` / `UInt64` 欄位)
  找一個 `ulong` 欄位,寫入 **`18446744073709551615`**(或任何 > `9223372036854775807` 的值),回讀。
  **PASS**:讀回你寫的值。
  **FAIL**:讀回負數或完全不同的值(修正前:`(long)` cast 讓超過 `long.MaxValue` 的值回繞成負數送出)。
  *找不到 ulong 欄位的話這項可跳過 —— 已有 4 個單元測試覆蓋,實機只是確認端到端。*

- [ ] **H-1 · AOB pattern 品質**
  對一個方法用 **`Copy: aobscan`**,把產出的 pattern 貼回 CE 掃描。
  **PASS**:掃到,且結果唯一(或符合預期數量)。
  **FAIL**:掃不到,或掃到錯的位置。
  *目標函式附近有 `test` 指令時最容易顯現 —— 那正是修正的 group 3(F6/F7)immediate。*

- [ ] **I-2 / I-3 · 掃描記憶體用量**
  大型 Mono 遊戲跑 `findInstances`,同時觀察遊戲行程記憶體。
  **PASS**:用量平穩。
  **FAIL**:掃描期間暴衝數百 MB 以上(I-2 memo 無上限 / I-3 預留 2× region)。

---

## G2 — CE table / CE Lua bridge 路徑 · 最高優先

**這組最值得先跑** —— P1 影響整條 bridge,而 CE-Lua 那批修正動到每一次 DLL 呼叫。
需要 Cheat Engine + 一個 .CT。

- [ ] **P1 · `U.callDLL` 位址正確**
  從 CE 走 bridge 路徑注入並初始化(`unity_dump.lua` 的 `inject()`)。
  **PASS**:注入成功、pipe 起來、後續 RPC 正常。
  **FAIL**:CE 當掉、遊戲 CTD、或「call failed」(修正前:export 位址被當成 timeout、
  第一個參數被當成程式碼位址跳過去)。

- [ ] **CE-Lua · 有限 timeout(不再凍結 CE)**
  在 bridge 連線狀態下,讓一次 DLL 呼叫遇到麻煩 —— 最直接的做法是**用 CE 暫停遊戲行程**後再觸發一次
  bridge 動作。
  **PASS**:最多 10 秒後回錯誤訊息,**CE 視窗全程可操作**。
  **FAIL**:CE 整個凍結且無法恢復(修正前:`timeout=nil` = 無限等待,而該等待在 CE 的 GUI 執行緒上且不 pump 訊息)。

- [ ] **CE-Lua · retry loop 不再凍結 CE**
  **先不要注入 DLL**,直接跑 `unity_symbols.lua` 的 `registerClassSymbols(...)`
  或 `unity_invoke.lua` 的呼叫 —— 讓它走 pipe-open retry 的完整 15 × 200ms。
  **PASS**:那 3 秒內 CE 視窗仍會重繪、不被 Windows 標成「沒有回應」,之後正常回報錯誤。
  **FAIL**:CE 整個僵住 3 秒。

- [ ] **CE-Lua · 錯誤原因**
  上一項失敗時看訊息。
  **PASS**:訊息包含 CE 自己給的原因(如 `Execution timeout` / `Failure launching thread`)。
  **FAIL**:只有一句通用的猜測。

- [ ] **C-2 · Mode-B .CT bootstrap**
  用嵌入檔的 .CT 跑 `bootstrap()`。
  **PASS**:嵌入檔正確解出到 `%TEMP%\Discrete_Dumper\<table-id>\`,大小與來源相符。
  **FAIL**:第一個檔就報 `bad argument #1 to 'write' (string expected, got table)`
  (修正前:CE 的 `Stream:read` 回 bytetable 不是 string)。

---

## G3 — 32-bit Mono 遊戲

沒有這種目標就整組跳過。**這組驗的全是靜默錯誤資料**,所以請務必對照 CE 核對數值。

- [ ] **P3 · x86 DLL 選擇**
  從 CE bridge 對 32-bit Mono 遊戲注入。
  **PASS**:選到 `Discrete-Mono-x86.dll`(參 [ce-bridge-integration.md](ce-bridge-integration.md) 的手動改名步驟)。
  **FAIL**:選到 x64 版 → 注入失敗或載入錯誤位元的 DLL(修正前:`is32bitProcess` 不存在,x86 分支恆為 false)。

- [ ] **D-1 · `whatIsAt` 欄位歸屬**
  挑一個已知 layout 的 class,對某欄位**邊界後 4 bytes** 的位址跑 `whatIsAt`。
  **PASS**:報**下一個**欄位。
  **FAIL**:仍報前一個欄位(修正前:未解析欄位的大小 fallback 寫死 8,x86 上寬 4 bytes → 吞掉下一個欄位開頭)。

- [ ] **D-2 · `IntPtr[]` 陣列展開**
  展開一個 `IntPtr[]` 或 `UIntPtr[]` 欄位,**逐一對照 CE 的記憶體檢視**。
  **PASS**:元素數與每個值都相符。
  **FAIL**:只看到一半的元素、且值對不上(修正前:x86 上用 8-byte stride 走 4-byte 陣列 → 跳過一半並讀出界)。

---

## G4 — IL2CPP 遊戲

- [ ] **G-3 · offset 0 的 static field**
  找一個 class 的**第一個** static field(offset 0),跑 chain detection / `findFieldReferences`。
  **PASS**:有命中。
  **FAIL**:零命中,但其他 offset 的 static field 正常(修正前:`disp.size == 0` 守衛在比較前就擋掉 ——
  x86 對 offset 0 不編碼 displacement)。

- [ ] **G-4 · `findFieldReferences` 命中數**
  對一個已知有 N 條參照的欄位跑,和修正前的結果比。
  **PASS**:沒有重複列;數量合理。
  **FAIL**:出現成對的重複列(REX 去重失效),或明顯少於預期(舊的 `buf[p-1]` 猜測誤刪約 1/16)。

---

## G5 — Unity 2022+ Mono 遊戲

- [ ] **C-1 · image fallback**
  對一個位於**模組化 UnityEngine DLL**(CoreModule / PhysicsModule 等)的 class 跑
  `discrete_mono_registerSymbol`,讓 CE 的快取 image 清單解不出來而走 fallback。
  **PASS**:fallback 找到 class 並註冊成功。
  **FAIL**:拋 `attempt to call a nil value`(修正前:`mono_enumImages` 是 callback-only 且不回傳值,
  我們卻當成回傳 list 使用 —— 一找到 image 就爆)。

---

## G6 — Proxy 安裝(不需注入)· 高優先

只需要一個遊戲資料夾。**B-1 是整輪最嚴重的修正之一**。

- [ ] **B-1 · winmm proxy 現在能啟動遊戲**
  對任一 Unity 遊戲安裝 **winmm** proxy(不是 version),啟動遊戲。
  **PASS**:遊戲正常啟動,DLL 載入。
  **FAIL**:出現「無法定位程序輸入點 `waveInOpen` …」之類的錯誤框(修正前:proxy 缺 13 個
  `UnityPlayer.dll` 靜態匯入的符號 → **每一款 Unity 遊戲都開不起來**)。
  *這條路徑先前是 100% 壞的,所以這項等於首次驗證,不是回歸測試。*

- [ ] **B-4 · ProxyInstaller 不碰他人的 DLL**
  找(或造)一個已經有**第三方** `version.dll` 的遊戲資料夾(ReShade / ENB / SpecialK / mod loader)。
  對它安裝 version proxy。
  **PASS**:**拒絕**安裝並給出明確訊息(提到那不是 Discrete 裝的)。
  **FAIL**:直接覆寫 → 使用者的 mod 無聲消失(遊戲照常啟動、行為正常,所以不會有人察覺)。
  再測解除安裝:**PASS**:留下他人的 DLL 並回報略過了哪些。

- [ ] **B-4b · 我們自己的檔案仍可正常覆寫/移除**
  對已裝 Discrete proxy 的資料夾重裝、再解除。
  **PASS**:正常覆寫、正常移除乾淨。
  **FAIL**:誤把自己的檔案當成他人的而拒絕(那表示 `ProductName="Discrete"` 的辨識失效)。

---

## G7 — 非 ASCII 路徑 + 32-bit 注入

- [ ] **B-5 · `wmain` 路徑編碼**
  把 Discrete 放到含中文的路徑(例:`D:\遊戲\Discrete\`),對 **32-bit** 遊戲用 UI 的 Pick & Inject。
  **PASS**:注入成功。
  **FAIL**:回報「DLL not found」但檔案明明存在(修正前:narrow argv 是 CP950 卻用 `CP_UTF8` 解碼,
  且無 `MB_ERR_INVALID_CHARS` → 靜默變成 U+FFFD)。

---

## G8 — Keep-foreground

- [ ] **A-3 · shutdown → init 循環後仍有效**
  勾選 keep-foreground → 觸發 `Discrete_Shutdown` → 再 `Discrete_Init` → **再勾一次** → alt-tab 離開遊戲。
  **PASS**:遊戲持續執行。
  **FAIL**:UI 顯示已啟用但 alt-tab 後遊戲暫停(修正前:`Shutdown` 沒重置 `g_hookInstalled`,
  再次啟用時整個安裝區塊被跳過 → detour 仍是 disabled)。

- [ ] **A-1 · overlay 共存**
  開著會 subclass 視窗的 overlay(Steam overlay / Discord / RTSS),勾選 keep-foreground 後再
  `Discrete_Shutdown`。
  **PASS**:遊戲視窗仍正常回應輸入、正常繪製、可正常關閉。
  **FAIL**:輸入失效 / 畫面異常 / 視窗關不掉(修正前:無法還原 WNDPROC 時仍移除 prop →
  `SubclassProc` 落到 `DefWindowProcW`,遊戲真正的 WndProc 被永久繞過)。

---

## 完成後

全部通過的話,把 [plan.md](plan.md) 的 audit #4 條目標成 live-verified,並把本檔標為 done。
任何一項 FAIL:對照 [audit-2026-08-13.md](audit-2026-08-13.md) 該批的說明,那裡有完整的機制分析與修法理由。

---

## 這次 audit **之外**的既有 smoke backlog

以下在 audit #4 之前就待驗,與本清單無關,但如果剛好開著遊戲可以順手一起做 ——
詳見 [plan.md](plan.md) 開頭的「待 live-smoke」區塊:

- 🎮 **UE5-port wave**(2026-07-12)— Keep-Foreground、通用變速 / 低重力
- 🧭 **Teleport wave 1**(2026-07-02)+ **Auto-detect player** wave A/B
- 🔌 **AOBMaker app 唯一-AOB 整合** — 需同時開 `AOBMaker.exe`
- 📸 Phase 29h / 29f-5、Mono 端 Dictionary 展開、Phase 31-5 reverse-lookup 命名 component

> 注意 G8 的 keep-foreground 與 UE5-port wave 的 Keep-Foreground 是**不同層面**:
> 這裡驗的是 audit 修的生命週期缺陷(A-1/A-3),那邊驗的是功能本身。同一次 session 可一起做。
