# 資源守門（AI 檢索版）

資源守門設定頁位於「系統管理 > 設定 > 資源守門」。它不是 PRTG 專屬開關，會在 NetIQ 每批查詢與 PRTG 每個歷史值請求前檢查受監看 sensor。受監看 sensor 可由 `PrtgResourceGuardSensorObjids` 覆寫；空值時依 Sentinel／PRTG 位址、Core Health 與 sensor 分類自動偵測。

判定方向：CPU 越高越緊張；可用記憶體與 Core Health 越低越緊張。非 Up、非百分比、查無 objid 或不屬於 cpu／memory／corehealth 的 sensor 會被忽略。連續超標達 `Strikes` 後進入暫停，等待 `PauseMinutes` 重檢；單趟累計超過 `MaxPauseMinutes` 後放行並警告。取消訊號必須穿透暫停，任何外部讀值等待也要受單次逾時與取消控制。

自動偵測仍需以「預覽受監看 sensor」核對。偵測失敗、API 逾時或鏡像 fallback 代表資料來源不完整，不能當成「沒有守門目標」或把守門清單清空。
