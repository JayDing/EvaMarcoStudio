# Eva 的按鍵精靈

Windows 桌面鍵盤／滑鼠自動化工具，使用 C#、Windows Forms 與 .NET Framework。

## 功能

- 拖曳十字設定滑鼠點位，支援鍵盤按鍵捕捉與常用按鍵。
- 動作排序、Shift 多選拖曳、同類型批次修改及備註。
- 動作後等待以秒設定，支援範本與多範本循序執行。
- Ctrl+S 儲存、未儲存變更警示、中央倒數及右下角執行提示。
- F9 開始單一範本，F10 停止。

## 建置與執行

在 Windows 上執行 `build.cmd`，使用系統的 .NET Framework C# 編譯器，產生 `EvaMacro-UI.exe`。不需要額外 NuGet 套件。

完整操作方式與使用限制見 [使用說明](使用說明.md)。

## 測試

執行編譯後的程式並帶入 `--self-test`，會執行內建的資料與模擬執行測試。測試不會送出實際桌面鍵鼠操作，會在執行檔所在目錄產生測試結果與離屏介面預覽。

## 原始碼

- `MacroStudio.cs`：主視窗、範本編排與儲存。
- `Editing.cs`：修改視窗、按鍵捕捉與十字拖曳。
- `Sequences.cs`：範本組合與循環執行。
- `Interaction.cs`：清單拖曳與中央倒數。
- `LiveUI.cs`：執行提示與儲存格編輯。
- `BatchEditing.cs`：批次修改、時間換算與類型配色。
- `FeatureTests.cs`：功能測試。