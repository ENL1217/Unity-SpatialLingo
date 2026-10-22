# AIEnglish：OpenRouter 免費視覺版

本分支以 Spatial Lingo 2.0.0 為基礎，使用 Unity **6000.3.27f1**。Quest 直接連 OpenRouter，無須 PC 中繼或 OpenCode。

## 實際資料流程

- Quest 本機 Sentis YOLO 偵測物件，追蹤空間位置。
- 裁切物件圖片與教材提示送到 OpenRouter 免費視覺模型，產生單字、形容詞、動詞。
- 造句、翻譯及口說轉寫文字的判定使用相同模型的文字介面。
- 麥克風音訊送 Wit.ai STT；角色語音使用原本的 Wit.ai TTS。這兩項仍需聯網。
- 這版不是端到端 omni 語音模型；OpenRouter 不接收口說原始音訊。
- 原有完整學習流程仍以英文／西文為主，繁中介面與完整繁中學英文流程尚待下一階段開發。

預設模型：`nvidia/nemotron-3.5-super-vl:free`，停用可選推理以縮短教材與判定等待。免費供應商可更換、限流或下架，請以 [OpenRouter 模型清單](https://openrouter.ai/models?max_price=0) 與 [帳戶限制](https://openrouter.ai/docs/api_reference/limits) 為準；免費不表示無限用量。

## 免費限制

1. 程式只接受明確以 `:free` 結尾的模型 ID，禁止付費模型及隱式模型路由。
2. 每次發出推論前查詢官方模型目錄，檢查影像輸入支援及價格。
3. 每次請求設定 `provider.max_price` 的 prompt、completion、request、image 全為 0，停用 provider fallback。參見 [官方價格篩選說明](https://openrouter.ai/docs/guides/routing/provider-selection#max-price)。
4. 全部模型請求依序執行，間隔至少 4 秒，每次啟動最多 35 次；重複且完全相同的請求在本次啟動中使用記憶體快取。
5. 401／402／403 至少等待 5 分鐘，其他 HTTP 失敗至少 1 分鐘，並尊重更長的 Retry-After。網路／解析錯誤等待 30 秒。課程層不在冷卻期繼續重試。

不需要綁卡、儲值或啟用自動加值。即使帳戶日後有付費餘額，本分支仍只允許零價格請求。這是程式端的限制；帳戶本身的其他應用仍受各自設定影響。

## 建置

依賴：Unity 6000.3.27f1 的 Android Build Support、Android SDK、NDK r27c `27.2.12479018`、JDK 17。SDK 與工具鏈路徑可由參數指定。

```powershell
./Tools/Build-Quest.ps1 -Unity 'C:/Users/jaja/UnityEditors/6000.3.27f1/Editor/Unity.exe' -Sdk "$env:LOCALAPPDATA/Android/Sdk" -Jdk '<JDK17路徑>'
```

此命令依序還原套件、套用下述 Sentis 匯入修補、執行 EditMode 請求契約測試，再建置 ARM64／IL2CPP APK。輸出 `Builds/AIEnglish-OpenRouter.apk`，詳細紀錄在 `Logs`。Builds、Logs、.local 與 APK 均不進 Git。

應用 ID：`com.enl1217.aienglish`；顯示名稱：AIEnglish。保留原本 Android activity 與 Quest 權限設定。

上游 PSO cache Android library 仍使用 jcenter 與 AGP 7 設定，本分支改為沿用新版 Unity 的 AGP／SDK 參數。Meta SDK 81 的 InteractionSdk、SDKTelemetry、OVRPlugin AAR 共用 manifest namespace，與 AGP 9 的新檢查衝突；建置回呼設定 `android.uniquePackageNames=false` 以相容這些現有 SDK，保留類別與資源衝突檢查，維持 Gradle 9.3.1／AGP 9.1.1。

### Sentis 2.6.1 匯入修補

Unity 升級後，Sentis 2.6.1 的編輯器匯入統計會在 YOLO 動態 rank tensor 上直接讀取 rank 而中斷匯入。修補腳本 `Tools/Patch-SentisAnalytics.ps1` 只在確認版本與原始程式片段相符後，為 PackageCache 中的統計程式加上 `!shape.Value.isRankDynamic` 判斷。

沒有改動模型圖或推論運算，也沒有降回舊版 Unity／Sentis。PackageCache 被清除後，重新執行建置腳本即可再套用。Unity 套件原始碼不納入本 fork。建置前另強制重新匯入 YOLO，並檢查模型存在且有輸出，失敗即停止建置。

## 安裝與私人設定

1. Quest 開啟開發者模式、USB 偵錯並授權此 PC。
2. 安裝 APK 並啟動一次：

```powershell
adb install -r ./Builds/AIEnglish-OpenRouter.apk
adb shell monkey -p com.enl1217.aienglish 1
```

3. 到 [OpenRouter API Keys](https://openrouter.ai/settings/keys) 建立專用 key，然後在本機 PowerShell 輸入：

```powershell
./Tools/Set-OpenRouterConfig.ps1 -WithWit -Push
```

腳本以隱藏輸入接收 key，寫入被 Git 忽略的 `.local/AIEnglish.OpenRouter.json`，推送至 Quest `/sdcard/Android/data/com.enl1217.aienglish/files/AIEnglish.OpenRouter.json`。請勿把 key 貼進聊天、截圖、Unity Inspector 或版本控制。

4. 重新啟動 AIEnglish，授權相機、麥克風與空間資料，依引導看向房間物件。缺少 key 時模型無法產生教材；可從裝置 logcat 查看不含 key 的設定狀態。

設定格式：

```json
{
  "apiKey": "<你的私人 OpenRouter key>",
  "model": "nvidia/nemotron-3.5-super-vl:free",
  "witEnglishToken": "<私人英語 Wit.ai client access token>",
  "witSpanishToken": "<私人西語 Wit.ai client access token>"
}
```

更換其他免費視覺模型只需修改設定檔並重新啟動，不用重新建置 APK。金鑰位於個人裝置的一般應用資料檔中；請自行保管裝置，並可從 OpenRouter 撤銷專用 key。

## 驗證紀錄

2026-10-23：Unity 6000.3.27f1 完成匯入與程式編譯；EditMode 測試 10/10 通過。測試涵蓋付費／隱式路由阻擋、自訂端點阻擋、圖片內容格式、JSON 跳脫與四種零價格上限、文字歷史保留、Wit.ai 私人 token 只寫入執行期副本，以及相機未就緒時的畫面更新與取消、房間缺席時的逾時備援及頭顯前方生成位置。

APK 簽章驗證通過，應用 ID、ARM64 架構與入口 activity 經 aapt 確認。已安裝至連線的 Quest 3；私人設定檔的 PC／裝置 SHA-256 校驗一致，相機、錄音與空間權限皆已授予。完整看物件與真人口說驗收仍需實際操作。

首次佩戴測試回報同意後只見 Logo／透視背景。裝置紀錄發現 CameraTaxonTracker 在相機未就緒時，於主執行緒同步遞迴呼叫帶 Thread.Sleep 的重試，阻塞相機和引導的後續更新。已改為可取消的非阻塞等待，移除 Thread.Abort；並讓 WebCamTextureManager 等待透視層初始化、停用時停止對應協程，避免過早放棄相機啟動。相機缺席的回歸測試確認主迴圈可繼續更新且能停止／取消；裝置紀錄已確認修正版建立 1280×960 相機影像並完成本機物件分類。仍只見透視背景的第二個原因，是 RoomSense 無限等待無法定位的房間。LoadingScene 與 MainScene 原本還會重複啟動 MRUK discovery，前者已停用載入，只保留 MainScene 的房間探索；RoomSense 等待 8 秒仍無房間時，會以頭顯前方約 1.1 公尺、眼睛下方 0.8 公尺的可及位置繼續角色引導。已有可定位的房間可直接沿用；重新掃描能改善房間定位，但不應成為進入引導的必要條件。這一輪修正仍需在頭顯驗收角色顯示。

OpenRouter 實際連線測試：專用 key 的 Credit limit 設為 $0，沒有新增付款資料；NVIDIA 免費模型成功產生雙語 Wordclouds 與布林 Pass／文字 Reason 判定，兩次請求各回報 cost=0。關閉可選推理後，樣本杯子圖片約 24 秒、文字判定約 2.5 秒。當時帳戶免費額度顯示每日 50 次；實際配額與上游共享限流會變動。

Gemma 4 26B 免費端點當時回報供應商共享池 429，因此本版預設改用上述 NVIDIA 模型。可執行 `Tools/Test-OpenRouter.ps1` 使用專案的杯子樣本圖片重新驗證；測試不傳送房間照片，結果寫入被忽略的 Logs。

另以 Unity 的 `AIEnglishCloudSmoke.Run` 呼叫實際 MetaModelRestApi adapter 與 AssistantAI 教材解析，圖片 Wordclouds、例句、有效口說句的判定全部通過，消耗 3 次免費請求。該次圖片請求為 5.7 秒；免費服務延遲會變動，不能保證固定等待時間。

Wit.ai 的上游資產沒有填入 client token；本分支從相同私人設定檔載入英語／西語 token，建立執行期設定副本，不修改或儲存原始 Unity 語音資產。

已在自己的 Meta 帳戶建立私人 `aienglish_en`、`aienglish_es`。英語與西語 STT 已用合成測試音訊驗證，分別正確辨識「I see a blue cup」與「Hola, esta es una taza」。Wit 的西語 app 不接受 synthesize；保留原版以英語 app 合成多語文字的方式。一般 key 不可使用原本內部 `en_US.46` 聲線，本分支改用公開 `wit$Charlie`，並使用支援的 default 樣式。
