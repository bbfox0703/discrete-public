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

## 2026-08-13 第一輪 headless 執行結果(build #1408/#1409)

> 用 **[tools/smoke/](../tools/smoke/)** 的 headless harness 跑(直接對 `\\.\pipe\DiscreteBfx`
> 下 RPC,不經 DiscreteUI、不經 CE)。目標:**Core Keeper**(Mono x64、Unity 6000.0.59f2、
> 4 GB heap)與 **活俠傳 LegendOfMortal**(Mono **x86**、Unity 2020.3.49f1)。
>
> ⚠️ **先修了兩件會讓這份清單失效的事**:
> 1. **`dev` HEAD 當時無法編譯** — batch I 只在 `FindAllInstances` 定義了 `memoNegative`
>    lambda,但 `ReverseLookupIndex::Build` 也呼叫它 → `MonoAddressFinder.cpp(1230): error
>    C3861`。2026-08-13 17:34 的 `build.cmd dll` 就是這樣失敗的(見 `build_log.txt`)。
>    已修:`kMaxMemoEntries` 上提到 namespace scope,雙walker 各自帶 lambda。
> 2. **`dist/` 的 DLL 落後兩個批次** — `dist/Discrete-Mono.dll` 是 16:54 的 build **1396**,
>    而 batch H 是 16:56、batch I 是 17:11 的 commit。所以 publish 出來的 DLL **不含 H/I**;
>    重建後為 build 1408(x64)/1409(x86)才開始測。**每次 smoke 先看 `handshake.build`。**

| 項 | 結果 | 怎麼驗的 |
|---|---|---|
| **P2** ScanCancel 陳舊旗標 | ✅ **PASS(附 A/B 對照)** | 取消一次 value scan 後再跑 `scanFieldValue`:**修好的 1408** = 掃完整個 3.93 GB、200 筆命中、與取消前的候選集 **200/200 完全一致**;**修前的 1158** = 同一步驟回 **count=0** 卻仍報「掃了 3.75 GB、無錯誤」—— 正是 audit 描述的靜默截斷,直接重現 |
| **I-2 / I-3** 掃描記憶體 | ✅ **PASS(A/B)** | 全heap walk 期間取樣遊戲 private bytes:**1408** 峰值 +562 MB / +833 MB;**1158** 峰值 **+2888 MB**(1999→4887 MB)、第二輪衝到 6110 MB。約 **3.5–4×** 差距,32-bit 上修前必爆 |
| **E-4 / E-5** 掃描中關掉 client | ✅ **PASS** | 在 `beginValueScan` 起跑 2 秒時**強制 kill** client:CPU 在 kill 後 **1 秒**回到 idle(37%);對照組(讓它跑完)是一直吃滿一顆核到 t=+34s。pipe 事後立即回應 `ping`(0.0s),無空轉 |
| **E-3** session 滯留 | ✅ PASS | 留一個 `retainCap=200000` 的 session 不 end 就斷線:private 3814 → 3798 MB(不再成長) |
| **H-1** AOB pattern 品質 | ✅ **PASS(Mono)** | 抽 **150 個 method**,每個都拿 `getMethodAobPattern` 再自己掃遊戲的可執行記憶體:**150/150 pattern 在自己的 method 位址命中、literal byte 逐一相符**(解碼器若在 group 3 desync,wildcard 會落錯位置、pattern 就對不上自己)。其中 **4 個視窗含 F6/F7 opcode,同樣全中**。IL2CPP(真 AOT、長函式)尚未跑 |
| **B-1** winmm proxy 符號 | ✅ **PASS(靜態,全 15 套遊戲)** | 解析每套遊戲 `UnityPlayer.dll` 的 winmm import ↔ 我方 proxy export:每套各 import 23 個、我方 export 48 個,**15/15 全涵蓋、0 缺漏、x64+x86 皆是、全部 by-name(無 ordinal import)**。實機啟動確認仍待做 |
| **B-4 / B-4b** ProxyInstaller 所有權 | ✅ **PASS 26/26** | 用真的 `WindowsProxyInstaller` 對合成資料夾跑(絕不碰真遊戲):第三方 `version.dll` → 拒裝且訊息點名原因、檔案 hash 不變;uninstall 保留他人檔案並回報略過;自己的檔案可重裝/換 kind(mutex 刪除只刪自己的)/清乾淨;**另加 audit 沒列的一格**:他人 DLL 佔**另一個** slot 時照裝、且不動它,反向裝進那個 slot 則拒絕 |
| **D-1 / D-2**(x86,活俠傳 build 1409) | ✅ **兩項都 PASS(第四次才驗對)** | **D-2**:在 x86 上 `sizeof(IntPtr)==sizeof(Int32)==4`,所以拿一個**真的 4-byte 陣列**走兩次 —— 宣告 `System.Int32`(對照組)與宣告 `System.IntPtr`(修正的路徑)—— 必須元素數相同、每個值相同,且都等於我自己 4-byte stride 的 `ReadProcessMemory`。結果:`element_size=4`(不是 8)、2/2 元素一致、值 byte-for-byte 相同。殘留 stride-8 會讓 IntPtr 那次只拿到一半元素且值對不上。**這個做法不需要遊戲裡真的有 `IntPtr[]`**(活俠傳沒有可用的)。 | **D-1**:改成**完全不依賴 metadata offset** —— 掃描位址、只用 `whatIsAt` 自己的回答互相對照。`FogSettings` 與 `OutlineSettings` 各 15 個連續 4-byte 探測,**每一個的 `reportedOffset` 都等於探測 offset(delta 0)、沒有任何欄位吃掉下一個**(修正前:未解析欄位大小 fallback 寫死 8,x86 上每個 4-byte 欄位都會吞掉下一個的開頭)|
| **31-5**(bonus) | ✅ **PASS** | 同一次掃描順手驗到:`OutlineSettings` 的 `edgeColor`(Color,4 個 float)在 +12/+16/+20/+24 分別報 `edgeColor` / `edgeColor.g` / `edgeColor.b` / `edgeColor.a` —— reverse-lookup 會**逐一命名 struct component**,不是只報外層欄位 |

### 第二輪:CE Lua 那組(2026-08-13,CE 7.7)

> 一次手動步驟:在 CE 的 Lua Engine 執行 `dofile[[…\tools\smoke\ce\dispatcher.lua]]`,
> 之後每個測試都是 `dofile jobN.lua` + 讀 `ce_log.txt`。
> **CE 的責任性(不凍結)改用 [tools/smoke/ce_watchdog.py](../tools/smoke/ce_watchdog.py)
> 從行程外量**:`SendMessageTimeout(WM_NULL)` 由對方的訊息迴圈回應,超時=GUI 執行緒被卡住。
> 比「用眼睛看視窗」客觀,也不受 CE 內部 timer 影響(CE 會把 Lua Engine 建的 timer 收掉)。

| 項 | 結果 | 證據 |
|---|---|---|
| **P1** `U.callDLL` 位址正確 | ✅ **PASS** | `callDLL("Discrete_GetBuildNumber","uint32")` 回 **1408**,正是該 DLL 的 build。位址若被當成 timeout、跳到第一個 vararg,不可能回出正確 build。`Discrete_StartPipe` 回 0,pipe handshake 正常。**x86 目標(活俠傳)另回 1409** —— 32-bit 上 cdecl/stdcall 差異真的會咬人,所以這條同時驗到了 callmethod=1 |
| **CE-Lua retry loop 不凍結** | ✅ **PASS** | 不注入 DLL 直接 `registerClassSymbols` → 走完 15×200 ms(實測 **3078 ms**),同時段 watchdog **163 次探測、0 次無回應、最長阻塞 0.00 s** |
| **CE-Lua 錯誤原因** | ✅ PASS | 訊息帶出實際原因:`pipe open: \\.\pipe\DiscreteBfx: No such file or directory` |
| **C-2** Mode-B .CT bootstrap | ❌ **修前仍是壞的 → 已修 → ✅ PASS** | **audit 的修法沒解到真因**:`stream:read(size)` 在 CE 7.7 回 **nil**(不是 bytetable),所以第一個檔就死在 `unexpected Stream:read result type: nil`。**根因是 colon vs dot** —— CE 的 class method 是 bound closure、不吃 self,`s:read(16)` → nil,`s.read(16)` → 16 bytes 的 bytetable(實測)。改成 `tf.saveToFile()`(主)+ dot-syntax 分塊讀(備援)後:三個嵌入檔(含 1 MB DLL)**byte-exact**,且**存成真的 .CT 再 loadTable 回來重抽也 PASS** |
| **C-1** image fallback(G5) | ✅ **PASS** | 先 `LaunchMonoDataCollector()`(關鍵:沒叫它時 `mono_enumImages` 只列出 **0** 個 image,fallback 迴圈根本不會跑、舊 bug 也不會現形)→ 列出 **333 個 image**,per-image `mono_image_findClass` 找到 `UnityEngine.Camera` / `Transform` / `Physics`(全在模組化 UnityEngine DLL)。callback 形式不再拋 nil-call |
| **P3** x86 DLL 選擇(G3) | ✅ **PASS** | 32-bit 目標上 `targetIs64Bit()`=**false** → `U.dllPath('Mono')` = `…\Discrete-Mono-x86.dll`。同時證明 `targetIsX86()` 不能用:job0 在**完全沒開行程**時它就回 true。x86 DLL 的 export 呼叫回 **1409**(x86 build)|
| **CE-Lua 有限 timeout** | ⚠️ **無法在此重現失敗條件** | 檢查表建議「用 CE 暫停遊戲行程」→ 實測**不會**造成 timeout:`NtSuspendProcess` 只凍住既有執行緒,`executeCodeEx` 新建的 remote thread 照跑,呼叫 0 ms 就回 1408。程式碼側可確認 `executeCodeEx(1, kDllCallTimeoutMs=10000, fn, …)` 是有限值且位址在第三位;要真的量到 10 s 上限需要另一種讓 remote thread 卡住的方法。**檢查表那句 repro 需要修正** |

**D-1 這條走了四次才驗對 —— 三個坑都在 harness,值得記下來**

1. `whatIsAt` 把答案放在 **`nearestField`**(帶 `offset` / `interiorOffset` / `kind`),
   `field` 在 interior 命中時是 **null**。只讀 `field` 會把每個正確答案都判成 miss。
2. 「walkInstance 的值 == `ReadProcessMemory(base+offset)`」**不能**用來驗證 instance 身分 ——
   兩邊讀同一個 base,inline struct 也會通過。要用 `whatIsAt(base)` 回報
   「正是這個 class 且 `fieldOffset==0`」當閘門。
3. `getClassInfo` 的 field offset 與 `whatIsAt` 的 offset **在某些 class 上差一個常數 8**
   (x86 object header)。所以拿 metadata offset 去算探測位址、再跟 `whatIsAt` 的 offset 比,
   會看到「整批 misattribution」的假象。**改成只掃位址、看 `whatIsAt` 自己的回答是否自洽**
   (delta 是常數就只是 frame 差,不是歸屬錯誤;真正的 bug 訊號是「連續兩個 4-byte 探測
   落在同一個 4-byte 欄位」)。
4. `findInstances` 只給 `validate:true` 會回到**已被重用的舊 slot**:`FlatKitFog` 的候選位址
   上實際住著別的物件(`whatIsAt` 報 `m_Used` / `<moveDir>k__BackingField`,delta 恆為 −8)。
   做資料正確性比對時要加 `sane:true` / `aliveOnly`。

### 第三輪:G4(IL2CPP,Utawarerumono / Unity 6000.0.67f1 / metadata v31 / build 1408)

| 項 | 結果 | 證據 |
|---|---|---|
| **G-3** offset 0 的 static field | ✅ **PASS(配對法)** | 先用「offset-0 一組 vs 非零 offset 一組」比較是**錯的做法** —— 很多 static field 本來就沒有參照(第一次抽到的 offset-0 樣本幾乎都是沒在用的美術資源包 `ME_`/`RFX1_`/`RFX4_`),17% vs 58% 是抽樣偏差不是缺陷。audit 描述的 FAIL 徵兆是**同一個 class 內**「第一個 static field 零命中、其他 offset 的正常」,所以改成**同 class 配對**:14 組配對、其中 10 組 sibling 有命中,**bug 形狀 0 組**。offset-0 常常比 sibling 命中更多:`ExternalParameterManager.m_Instance` **108** vs `clothesMap` 4、`CrossSave.m_instance` 14 vs 4、`PlayMakerControl.IsPlayPlaymakerEvent_` 14 vs 5。4 組雙方都 0 的,DLL 自己回了說明(「使用這個欄位的程式碼還沒執行過」),與 offset 無關 |
| **G-4** `findFieldReferences` 命中數 | ✅ **PASS** | 兩批查詢共收 52 + 配對批的 rows,**`(address, hitAddress)` 重複列 0 筆**。舊的 REX 去重用 `buf[p-1]` 猜前一個 byte 是不是本指令的 prefix,會誤刪約 1/16 並可能留下成對重複;現在改用結果層 `(target, hitIP)` 去重 |
| **H-1**(補 IL2CPP) | ✅ **PASS** | 先前只在 Mono JIT stub 上驗過(wildcard 平均 0.1 個,幾乎沒有 immediate)。這次在**真的 AOT 程式碼**上抽 **120 個 method**:**120/120 pattern 在自己的位址 byte-exact 命中**,wildcard 平均 **4.9**/32 bytes(最多 13)—— 解碼器真的在標 immediate 而不是整段照抄;其中 4 個視窗含 F6/F7,同樣全中 |

### 第四輪:B-5 + B-1(2026-08-13)

| 項 | 結果 | 證據 |
|---|---|---|
| **B-5** `wmain` 路徑編碼 | ✅ **PASS** | 把 `dist` 放到 `D:\遊戲\Discrete\`,對 32-bit 活俠傳跑 `inject-x86.exe <pid> "D:\遊戲\Discrete\Discrete-Mono.dll"`。**先把該遊戲的 proxy 停掉**,所以注入前 pipe 確認是 down —— 只有我這次注入能讓它起來。結果:`remote_alloc size=66`(= 32 wchar + NUL,路徑沒被截斷)、`LoadLibraryW` 回非零 module base、pipe 起來、handshake `build=1409 ptr=4`,DLL 自己的 init log 也寫下 `attached 0.1.0.1409 … pid=53744`。修正前 narrow argv 會把 CP950 bytes 當 UTF-8 解 → U+FFFD → 找不到檔案。(這條 CJK 路徑特別毒:`遊` 的 CP950 是 `¹C`,第二個 byte 剛好是 ASCII `C`,而 `¹C` 不是合法 UTF-8。)**不需要 UI** —— 這條就是 `wmain` 的原始碼路徑 |
| **B-1** winmm proxy 實機啟動 | ✅ **PASS** | 對 BongoCat(乾淨、原本沒有任何 proxy)只裝 **winmm** proxy + payload,經 **Steam** 啟動:2 秒內行程起來、**pipe 經 winmm proxy 回應**、handshake `backend=Mono build=1408`。決定性證據在 DLL 自己的 init log:`module: WINMM.dll` → `module: Discrete-Mono.dll` → `ExportAPI: Discrete_Init` ——而 BongoCat **沒有 version.dll**、我也沒對它注入,所以唯一可能的載入者就是 winmm proxy。沒有任何「無法定位程序輸入點」對話框。測完已把三個檔移除,資料夾回到乾淨狀態 |

> ⚠️ **B-1 過程中我自己誤判過一次,值得記下來**:直接跑 `BongoCat.exe` 會看到行程立刻消失,> 我一度以為是 proxy 讓遊戲開不起來。真相是 Steamworks 的 `RestartAppIfNecessary` ——> 遊戲會透過 Steam 重新啟動自己,原行程必然結束。Unity 的 `Player.log` 停在> `Initialising for app 3419430` 就是那一刻。而且那個「失敗」的直接啟動,> **payload 其實也載入成功了**(另一個 pid 的 init log 有 `attached 0.1.0.1408`)。
> 順手做的靜態複驗:掃過 BongoCat 資料夾**全部 80 個 PE**(不只 UnityPlayer.dll ——> 還有 `SDL2.dll` 20 個 winmm import、`mono-2.0-bdwgc.dll` 5 個、`discord_partner_sdk.dll` 5 個),> **未滿足的 import 0 個**。所以 batch B 的 export 清單對這款遊戲的整個模組圖都夠。

**CE 那組的額外發現**

- 🔴 **`injectDLL` 失敗被吞掉 → bridge 謊報成功**(已修)。celua.txt:581 說 `injectDLL` 成功回 true,失敗回 **false**;`unity_dump.lua` 卻只檢查 `ok == nil`。實測 `injectDLL("<不存在>.dll")` 回 **false(boolean)**,於是 main() 一路印出 `DLL injected` + **`Pipe ready`** + `Launch DiscreteUI.exe and click Connect`,而實際上**什麼都沒注入**(module 沒 mapped、pipe 沒開)。唯一線索是 DEBUG-gated 的 WARN,預設看不到。這正好是 P3 同一個病:沒查 CE API 的回傳約定。修法:改 `if not ok`,並把找過的路徑印出來。
- 🟡 **`U.dllPath` 在這台實際解到不存在的路徑** — 回 `C:\Program Files\Cheat Engine\discrete\Discrete-Mono.dll`(該檔不存在;`dist/` 的 DLL 從沒複製進去)。「.CT 旁邊優先」那段依賴 CE 的 dialog FileName / TrainerOrigin,實測沒被填時就直接掉到 CE 安裝目錄。配合上一條,使用者會看到「成功」訊息但完全沒 bridge。
- 🟡 **文件與程式不符**:本檢查表與 `ce_table_bootstrap.lua` 都寫「`unity_dump.lua` 的 `inject()`」,但該檔**沒有** `inject()` —— 它在 load 時就 `U.guard(main)` 跑完。照文件呼叫 `inject()` 會得到 `attempt to call a nil value`。
- 🟡 **從 Lua Engine 執行的腳本關掉 Lua Engine 會讓 CE 拋例外** — clean-run 時 `_closeLuaEngineIfQuiet()` 呼叫 `getLuaEngine().Close()`,CE 7.7 跳 `[TCustomForm.SetFocus] frmLuaEngine … Can not focus (EInvalidOperation)` 並自動存出 `ExceptionAutoSave_*.ct`。正式路徑(AA `{$lua}` / table Lua script)不從 Lua Engine 執行,所以可能只影響開發時的手動執行 —— 但值得知道。

**新發現(不在 audit #4 清單內)**

- 🟠 **`scanFieldValue` 完全不可取消** — 它沒有開 `ScanCancel::Scope`
  ([ExportAPI.cpp:5506](../dll/src/shared/ExportAPI.cpp) 附近),而 `RequestCancel()` gate 在
  `g_scanActive`,所以 **Cancel 按鈕與 client 斷線都殺不掉它**。實測:client 在第 2 秒被
  強制 kill,掃描仍跑完整 33 秒(Core Keeper 4 GB heap)。`ScanCancel.h` 的註解已經寫到
  「`scanFieldValue` 沒有 Scope」,但只談了旗標污染,沒談「因此無法取消」。
  同一個 handler 正是 P2 的現場,值得一起處理。
- 🟡 **`getMethodAobPattern` 會越過函式尾端** — 要 48 bytes 就給 48 bytes,即使函式本體只有
  24 bytes(`...C3` 之後全是 padding/零)。結果 pattern 帶著一串零 → 在 308 MB 可執行記憶體
  裡 **223 個命中**。決定唯一性的是「視窗長度 vs 函式長度」,不是解碼器;唯一 AOB 本來就
  由 AOBMaker app 那條路負責,但 UI 直接用這個 pattern 時值得提醒。
- 🟢 `TableFile` 有 `saveToFile(filename)`(celua.txt:2248)—— `ce_table_bootstrap.lua`
  的 `_extractOne` 手工 `stream:read` + 分塊 `string.char` 可以整段換成一行,順便免掉
  bytetable 那個坑。

**這一輪沒能跑的**(工具面受限,非產品問題)

- ~~**G2 / G5 / G3-P3(CE Lua 路徑)**~~ — ✅ **第二輪已完成**(見上面的 CE 表)。
  一次手動 `dofile` 掛上 dispatcher 後就全部 file-driven。唯一沒結論的是「有限 timeout」,
  因為檢查表建議的 repro 造不出失敗條件。
- **F-1 / F-2 / I-1 / G8-A-3** — 純 UI 行為,要驅動 DiscreteUI 視窗;同樣因 computer-use
  被拒而未做。`I-1` 的 DLL 端已可用 `setInstanceField` 驗,但 bug 在 C# 的 `(long)` cast,
  只有走 UI 才算驗到。
- **G6-B-1 實機啟動、G7-B-5(CJK 路徑注入)** — 未做;B-5 其實不需要 UI,`inject-x86.exe`
  直接從含中文的路徑注入即可。

**遊戲資料夾現狀**:Core Keeper 與 LegendOfMortal 的 Discrete proxy + payload 已更新成
現行 build(原本是 2026-06-06 的舊檔,備份在 session scratchpad)。其餘遊戲未改動。

---

## G1 — 任一 Unity 遊戲(UI 注入)· 最高優先

只要能注入的遊戲都行,x64 Mono 或 IL2CPP 皆可。**一次 session 可驗 8 項**。

- [x] **P2 · ScanCancel 陳舊旗標**  ✅ *headless RPC + A/B vs pre-fix DLL (2026-08-13)*
  跑一次 value scan → **中途按 Cancel** → 接著跑 **Scan field value**。
  **PASS**:Scan field value 回正常結果。
  **FAIL**:回空白或明顯錯誤的結果(修正前:取消旗標殘留 → reverse-lookup index 在第一個 region 就中止,
  卻仍被標成 valid 發佈進全域快取)。

- [x] **E-4 / E-5 · 掃描中關閉 UI**  ✅ *headless: client force-killed 2s into beginValueScan, CPU idle 1s later*
  開始一個長掃描 → **直接關掉 DiscreteUI**(不要按 Cancel)。
  **PASS**:遊戲行程的 CPU 回落;工作管理員看不到單一核心 100%。
  **FAIL**:某個核心維持 100% 直到掃描自然結束(修正前:`Stop()` 關閉 `g_stopEvent` 後,
  monitor 執行緒的 `WaitForSingleObject(nullptr)` 立即返回 → 無 sleep 的緊密空轉)。

- [x] **E-3 · session 滯留**  ✅ *headless: session held then disconnect, private bytes flat*
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

- [x] **H-1 · AOB pattern 品質**  ✅ *Mono 150/150 + IL2CPP AOT 120/120 byte-exact at their own address; F6/F7 subsets clean*
  對一個方法用 **`Copy: aobscan`**,把產出的 pattern 貼回 CE 掃描。
  **PASS**:掃到,且結果唯一(或符合預期數量)。
  **FAIL**:掃不到,或掃到錯的位置。
  *目標函式附近有 `test` 指令時最容易顯現 —— 那正是修正的 group 3(F6/F7)immediate。*

- [x] **I-2 / I-3 · 掃描記憶體用量**  ✅ *headless A/B: +562 MB fixed vs +2888 MB pre-fix*
  大型 Mono 遊戲跑 `findInstances`,同時觀察遊戲行程記憶體。
  **PASS**:用量平穩。
  **FAIL**:掃描期間暴衝數百 MB 以上(I-2 memo 無上限 / I-3 預留 2× region)。

---

## G2 — CE table / CE Lua bridge 路徑 · 最高優先

**這組最值得先跑** —— P1 影響整條 bridge,而 CE-Lua 那批修正動到每一次 DLL 呼叫。
需要 Cheat Engine + 一個 .CT。

- [x] **P1 · `U.callDLL` 位址正確**  ✅ *CE 7.7: callDLL returned the real build (1408 x64 / 1409 x86)*
  從 CE 走 bridge 路徑注入並初始化(`unity_dump.lua` 的 `inject()`)。
  **PASS**:注入成功、pipe 起來、後續 RPC 正常。
  **FAIL**:CE 當掉、遊戲 CTD、或「call failed」(修正前:export 位址被當成 timeout、
  第一個參數被當成程式碼位址跳過去)。

- [~] **CE-Lua · 有限 timeout(不再凍結 CE)**  ⚠️ *檢查表建議的 repro(暫停遊戲行程)無效 —— 新建的 remote thread 照跑,呼叫 0 ms 就成功回值。需要另一種讓 remote thread 卡住的方法*
  在 bridge 連線狀態下,讓一次 DLL 呼叫遇到麻煩 —— 最直接的做法是**用 CE 暫停遊戲行程**後再觸發一次
  bridge 動作。
  **PASS**:最多 10 秒後回錯誤訊息,**CE 視窗全程可操作**。
  **FAIL**:CE 整個凍結且無法恢復(修正前:`timeout=nil` = 無限等待,而該等待在 CE 的 GUI 執行緒上且不 pump 訊息)。

- [x] **CE-Lua · retry loop 不再凍結 CE**  ✅ *3078 ms budget spent; watchdog 163 probes / 0 unresponsive*
  **先不要注入 DLL**,直接跑 `unity_symbols.lua` 的 `registerClassSymbols(...)`
  或 `unity_invoke.lua` 的呼叫 —— 讓它走 pipe-open retry 的完整 15 × 200ms。
  **PASS**:那 3 秒內 CE 視窗仍會重繪、不被 Windows 標成「沒有回應」,之後正常回報錯誤。
  **FAIL**:CE 整個僵住 3 秒。

- [x] **CE-Lua · 錯誤原因**  ✅ *message carries the real reason (pipe open: … No such file)*
  上一項失敗時看訊息。
  **PASS**:訊息包含 CE 自己給的原因(如 `Execution timeout` / `Failure launching thread`)。
  **FAIL**:只有一句通用的猜測。

- [x] **C-2 · Mode-B .CT bootstrap**  ✅ *was still broken (colon-vs-dot) → fixed → byte-exact incl. .CT round-trip*
  用嵌入檔的 .CT 跑 `bootstrap()`。
  **PASS**:嵌入檔正確解出到 `%TEMP%\Discrete_Dumper\<table-id>\`,大小與來源相符。
  **FAIL**:第一個檔就報 `bad argument #1 to 'write' (string expected, got table)`
  (修正前:CE 的 `Stream:read` 回 bytetable 不是 string)。

---

## G3 — 32-bit Mono 遊戲

沒有這種目標就整組跳過。**這組驗的全是靜默錯誤資料**,所以請務必對照 CE 核對數值。

- [x] **P3 · x86 DLL 選擇**  ✅ *targetIs64Bit()=false → Discrete-Mono-x86.dll; x86 export call returned 1409*
  從 CE bridge 對 32-bit Mono 遊戲注入。
  **PASS**:選到 `Discrete-Mono-x86.dll`(參 [ce-bridge-integration.md](ce-bridge-integration.md) 的手動改名步驟)。
  **FAIL**:選到 x64 版 → 注入失敗或載入錯誤位元的 DLL(修正前:`is32bitProcess` 不存在,x86 分支恆為 false)。

- [x] **D-1 · `whatIsAt` 欄位歸屬**  ✅ *15 consecutive 4-byte probes on two classes, reportedOffset == probeOffset throughout, zero swallowing*  ⚠️ *2026-08-13 嘗試過但 harness 有誤(見本檔開頭),結論不成立,需重跑*
  挑一個已知 layout 的 class,對某欄位**邊界後 4 bytes** 的位址跑 `whatIsAt`。
  **PASS**:報**下一個**欄位。
  **FAIL**:仍報前一個欄位(修正前:未解析欄位的大小 fallback 寫死 8,x86 上寬 4 bytes → 吞掉下一個欄位開頭)。

- [x] **D-2 · `IntPtr[]` 陣列展開**  ✅ *Int32-control vs IntPtr walk of the same 4-byte array: element_size=4, same count, identical values*  ⚠️ *2026-08-13 嘗試過但 harness 有誤(見本檔開頭),結論不成立,需重跑*
  展開一個 `IntPtr[]` 或 `UIntPtr[]` 欄位,**逐一對照 CE 的記憶體檢視**。
  **PASS**:元素數與每個值都相符。
  **FAIL**:只看到一半的元素、且值對不上(修正前:x86 上用 8-byte stride 走 4-byte 陣列 → 跳過一半並讀出界)。

---

## G4 — IL2CPP 遊戲

- [x] **G-3 · offset 0 的 static field**  ✅ *paired within-class test: 14 pairs, 10 informative, 0 bug-shaped*
  找一個 class 的**第一個** static field(offset 0),跑 chain detection / `findFieldReferences`。
  **PASS**:有命中。
  **FAIL**:零命中,但其他 offset 的 static field 正常(修正前:`disp.size == 0` 守衛在比較前就擋掉 ——
  x86 對 offset 0 不編碼 displacement)。

- [x] **G-4 · `findFieldReferences` 命中數**  ✅ *zero duplicate (address, hitAddress) rows across both batches*
  對一個已知有 N 條參照的欄位跑,和修正前的結果比。
  **PASS**:沒有重複列;數量合理。
  **FAIL**:出現成對的重複列(REX 去重失效),或明顯少於預期(舊的 `buf[p-1]` 猜測誤刪約 1/16)。

---

## G5 — Unity 2022+ Mono 遊戲

- [x] **C-1 · image fallback**  ✅ *333 images after LaunchMonoDataCollector; per-image lookup finds CoreModule classes*
  對一個位於**模組化 UnityEngine DLL**(CoreModule / PhysicsModule 等)的 class 跑
  `discrete_mono_registerSymbol`,讓 CE 的快取 image 清單解不出來而走 fallback。
  **PASS**:fallback 找到 class 並註冊成功。
  **FAIL**:拋 `attempt to call a nil value`(修正前:`mono_enumImages` 是 callback-only 且不回傳值,
  我們卻當成回傳 list 使用 —— 一找到 image 就爆)。

---

## G6 — Proxy 安裝(不需注入)· 高優先

只需要一個遊戲資料夾。**B-1 是整輪最嚴重的修正之一**。

- [x] **B-1 · winmm proxy 現在能啟動遊戲**  ✅ *靜態:15/15 遊戲 + BongoCat 全 80 個 PE 的 winmm/version import 全涵蓋(0 缺漏);實機:只裝 winmm proxy 經 Steam 啟動,init log 顯示 WINMM.dll → Discrete-Mono.dll → Discrete_Init,pipe 回應*
  對任一 Unity 遊戲安裝 **winmm** proxy(不是 version),啟動遊戲。
  **PASS**:遊戲正常啟動,DLL 載入。
  **FAIL**:出現「無法定位程序輸入點 `waveInOpen` …」之類的錯誤框(修正前:proxy 缺 13 個
  `UnityPlayer.dll` 靜態匯入的符號 → **每一款 Unity 遊戲都開不起來**)。
  *這條路徑先前是 100% 壞的,所以這項等於首次驗證,不是回歸測試。*

- [x] **B-4 · ProxyInstaller 不碰他人的 DLL**  ✅ *tools/smoke ProxySmoke, 26/26 on synthetic folders*
  找(或造)一個已經有**第三方** `version.dll` 的遊戲資料夾(ReShade / ENB / SpecialK / mod loader)。
  對它安裝 version proxy。
  **PASS**:**拒絕**安裝並給出明確訊息(提到那不是 Discrete 裝的)。
  **FAIL**:直接覆寫 → 使用者的 mod 無聲消失(遊戲照常啟動、行為正常,所以不會有人察覺)。
  再測解除安裝:**PASS**:留下他人的 DLL 並回報略過了哪些。

- [x] **B-4b · 我們自己的檔案仍可正常覆寫/移除**  ✅ *tools/smoke ProxySmoke, 26/26*
  對已裝 Discrete proxy 的資料夾重裝、再解除。
  **PASS**:正常覆寫、正常移除乾淨。
  **FAIL**:誤把自己的檔案當成他人的而拒絕(那表示 `ProductName="Discrete"` 的辨識失效)。

---

## G7 — 非 ASCII 路徑 + 32-bit 注入

- [x] **B-5 · `wmain` 路徑編碼**  ✅ *inject-x86.exe from D:\遊戲\Discrete\ into the 32-bit game: LoadLibraryW ok, pipe up, build 1409*
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
