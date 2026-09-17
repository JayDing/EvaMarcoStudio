using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// 顏色掃描小工具
//
// 螢幕上放一個全透明、可拖曳可縮放的方塊，定時掃描方塊內的顏色，
// 把感知上足夠接近的像素畫成高亮色塊（可閃爍），用來盯住畫面上某個顏色。
//
// 檔案結構：
//   ColorRule                一組色碼設定（使用者輸入的字串形式），與色碼解析、對比色計算
//   Perceptual               sRGB ↔ CIELAB 與感知距離 ΔE，註解裡記了為什麼比對要用它
//   ColorMatch               把 ColorRule 編譯成 Lab 座標的比對器
//   ColorScanner             純粹的像素比對，不碰 UI，可單獨測試
//   ScanOverlay              螢幕上那個方塊：兩種模式、定時掃描、繪製高亮
//   ColorBubble／ColorPicker 吸色：跟著游標的小色票，以及等待點擊的全域鉤子
//   ColorRuleRow             一列色碼組（色碼／誤差 ΔE；高亮色自動算）
//   TolerancePreview／ToleranceForm  誤差極限預覽：把誤差邊界上的顏色畫出來
//   ScanProfile／ScanSession 設定的驗證與自動保存
//   ScanStudioForm           工具視窗，把上面這些接起來
//
// 本檔案的功能測試放在 FeatureTests.cs 的 TestColorScan()，和其他測試集中在一起。
//
// 方塊的兩種模式：
//   編輯模式  藍色邊框可拖曳移動、四角可縮放
//   鎖定      方塊完全固定，且對滑鼠完全穿透，點擊與拖曳全部傳到下方程式
// 按 F7 開始掃描會自動鎖定，F8 結束掃描會回到編輯模式。
// 一組色碼設定：要找的顏色，以及這一組自己的誤差。
// 高亮色不必設定，一律由 Contrast() 從目標色算出對比色。
//
// Target 以字串保存使用者輸入，因為輸入途中會有「半成品」，不該在那一刻就報錯。
//
// Tolerance 是 Lab 空間的感知距離 ΔE，不是通道差也不是角度——理由寫在 Perceptual 的註解裡。
//
// 一組只有一個顏色，沒有「多重樣本」。真的要同時盯住好幾個顏色就開好幾組——
// 每一組有自己的誤差和自己的高亮色，比幾個樣本共用一個高亮色更好用，
// 而色碼組本來就能開到 20 組。
//
// 注意：Tolerance 的單位在改版時換過。舊設定檔裡它是「通道差」（預設 24），
// 現在會被當成 ΔE 24 讀進來 —— 那已經超過 ΔE 的破圖上限 15，會掃出一堆雜點。
// 舊檔不會壞、也不會掉資料，但升級後第一次用一定要重新調誤差值。
public class ColorRule
{
    public string Target { get; set; }
    public int Tolerance { get; set; }
    public ColorRule() { Target = "#FF0000"; Tolerance = ColorMatch.DefaultTolerance; }
    // 高亮色＝目標色的對比色：色相轉 180°、彩度拉滿，亮度往反方向拉。
    // 近灰階算不出有意義的互補色（互補後還是灰），就依明暗改用固定的亮綠或洋紅。
    // 這樣算出來的顏色和目標色在 RGB 上一定差得很遠，也順便避免掃描自我回饋。
    public static Color Contrast(Color target)
    {
        float saturation = target.GetSaturation(), brightness = target.GetBrightness();
        if (saturation < .18f) return brightness < .5f ? Color.FromArgb(57, 255, 20) : Color.FromArgb(214, 0, 132);
        return FromHsl((target.GetHue() + 180f) % 360f, 1f, brightness < .5f ? .62f : .40f);
    }
    static Color FromHsl(float hue, float saturation, float lightness)
    {
        float chroma = (1f - Math.Abs(2f * lightness - 1f)) * saturation, sector = hue / 60f;
        float second = chroma * (1f - Math.Abs(sector % 2f - 1f)), match = lightness - chroma / 2f;
        float r = 0f, g = 0f, b = 0f;
        if (sector < 1f) { r = chroma; g = second; }
        else if (sector < 2f) { r = second; g = chroma; }
        else if (sector < 3f) { g = chroma; b = second; }
        else if (sector < 4f) { g = second; b = chroma; }
        else if (sector < 5f) { r = second; b = chroma; }
        else { r = chroma; b = second; }
        return Color.FromArgb(255, Channel(r + match), Channel(g + match), Channel(b + match));
    }
    static int Channel(float value) { return Math.Max(0, Math.Min(255, (int)Math.Round(value * 255f))); }
    // 輸入途中一定會出現半成品色碼，那不是錯誤，所以提供不丟例外的版本。
    // IsValid 與色票預覽都走這裡，避免每敲一個字就丟接一次例外（也讓除錯器不會一直中斷）。
    public static bool TryParse(string text, out Color color)
    {
        try { color = Parse(text); return true; }
        catch { color = SystemColors.Control; return false; }
    }
    // 接受 #RRGGBB、#RGB、RRGGBB 與英文色名。解析不出來就丟例外，由呼叫端決定要不要當錯誤處理。
    public static Color Parse(string text)
    {
        string t = (text ?? "").Trim(); if (t.Length == 0) throw new Exception("色碼不可空白。");
        if (t[0] == '#') t = t.Substring(1);
        if (t.Length == 3 && t.All(Uri.IsHexDigit)) t = new string(new[] { t[0], t[0], t[1], t[1], t[2], t[2] });
        if (t.Length == 6 && t.All(Uri.IsHexDigit)) return Color.FromArgb(255, Convert.ToInt32(t.Substring(0, 2), 16), Convert.ToInt32(t.Substring(2, 2), 16), Convert.ToInt32(t.Substring(4, 2), 16));
        Color named; try { named = ColorTranslator.FromHtml(t); } catch { throw new Exception("無法解析色碼「" + (text ?? "") + "」，請輸入 #RRGGBB。"); }
        if (named.A == 0) throw new Exception("無法解析色碼「" + (text ?? "") + "」，請輸入 #RRGGBB。");
        return Color.FromArgb(255, named);
    }
    public static string Format(Color c) { return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }
    // Tolerance 的範圍由 ColorMatch 定義（ΔE 0～100），驗證、夾取與欄位上下限都引用同一組常數。
    public static void Validate(ColorRule rule)
    {
        if (rule == null) throw new Exception("色碼組無效。");
        Parse(rule.Target);
        if (rule.Tolerance < ColorMatch.MinTolerance || rule.Tolerance > ColorMatch.MaxTolerance)
            throw new Exception("誤差需介於 " + ColorMatch.MinTolerance + "～" + ColorMatch.MaxTolerance + "。");
    }
    public ColorRule Copy() { return new ColorRule { Target = Target, Tolerance = Tolerance }; }
}
// 感知色彩：sRGB → CIELAB，以及 Lab 空間裡的距離（ΔE）。
//
// 為什麼整個比對改用 Lab 而不是色相或 RGB 通道差：
//
//   RGB 通道差是「加法」的盒子，而遊戲的光影是「乘法」的縮放，兩者對不上。
//   色相看起來像是解法，但它在暗色與低彩度區域會嚴重誤導——實測同一件皮革的染色色碼
//   與它在畫面上的顏色色相差了 90 度，用色相衡量會判定為完全不同的顏色；
//   同一組顏色在 Lab 裡只差 ΔE 12，也就是「看得出差別但明顯協調」。
//   原因是暗色低彩度的區域裡色相根本不影響觀感，拿它當主要指標等於放大了一個不重要的維度。
//
//   Lab 是為了「數值距離對應人眼感受」而設計的，所以一個 ΔE 門檻同時管好色相、彩度與明暗
//   的取捨，不必讓使用者分開調三個數字，也讓染色色盤上的色碼可以直接當掃描目標。
//
// ΔE 用的是 CIEDE2000。一開始為了省計算選了 CIE76，那是錯的——理由寫在 Squared() 上面。
public static class Perceptual
{
    // sRGB 的 gamma 展開。每個通道只有 256 種可能值，所以這張表是「完全精確」的查表，
    // 不是近似。真正需要每個像素重算的只剩三個立方根。
    static readonly float[] Expand = BuildExpand();
    static float[] BuildExpand()
    {
        var table = new float[256];
        for (int i = 0; i < 256; i++)
        {
            double u = i / 255.0;
            table[i] = (float)(u <= 0.04045 ? u / 12.92 : Math.Pow((u + 0.055) / 1.055, 2.4));
        }
        return table;
    }
    // D65 白點。
    const double Xn = 0.95047, Yn = 1.0, Zn = 1.08883;
    // 刻意不做「把 RGB 量化成 32×32×32 再查 Lab」這種加速：實測那樣的量化誤差平均 ΔE 2.2、
    // 最大 ΔE 7.7（暗色更差，最大 8.0），而預設門檻只有 12——誤差和門檻同一個數量級，
    // 等於讓比對結果變成隨機。64×64×64 也還有最大 ΔE 3.8。所以 Lab 一律精確計算。
    public static void ToLab(int argb, out float L, out float a, out float b)
    {
        float r = Expand[(argb >> 16) & 255], g = Expand[(argb >> 8) & 255], bl = Expand[argb & 255];
        double X = r * 0.4124 + g * 0.3576 + bl * 0.1805;
        double Y = r * 0.2126 + g * 0.7152 + bl * 0.0722;
        double Z = r * 0.0193 + g * 0.1192 + bl * 0.9505;
        double fx = Pivot(X / Xn), fy = Pivot(Y / Yn), fz = Pivot(Z / Zn);
        L = (float)(116.0 * fy - 16.0);
        a = (float)(500.0 * (fx - fy));
        b = (float)(200.0 * (fy - fz));
    }
    static double Pivot(double t) { return t > 0.008856 ? Math.Pow(t, 1.0 / 3.0) : 7.787 * t + 16.0 / 116.0; }
    static double Unpivot(double t) { double cube = t * t * t; return cube > 0.008856 ? cube : (t - 16.0 / 116.0) / 7.787; }
    public static void ToLab(Color colour, out float L, out float a, out float b) { ToLab(colour.ToArgb(), out L, out a, out b); }
    // Lab → sRGB。給誤差預覽用：要畫出「剛好在 ΔE 門檻上」的顏色就得反算回來。
    // 超出 sRGB 能表現的範圍時直接夾住，夾過的顏色距離會改變，所以預覽那邊仍然要實測距離。
    public static Color FromLab(float L, float a, float b)
    {
        double fy = (L + 16.0) / 116.0, fx = fy + a / 500.0, fz = fy - b / 200.0;
        double X = Unpivot(fx) * Xn, Y = Unpivot(fy) * Yn, Z = Unpivot(fz) * Zn;
        double r = X * 3.2406 + Y * -1.5372 + Z * -0.4986;
        double g = X * -0.9689 + Y * 1.8758 + Z * 0.0415;
        double bl = X * 0.0557 + Y * -0.2040 + Z * 1.0570;
        return Color.FromArgb(255, Compress(r), Compress(g), Compress(bl));
    }
    static int Compress(double channel)
    {
        double value = channel <= 0.0031308 ? channel * 12.92 : 1.055 * Math.Pow(Math.Max(0.0, channel), 1.0 / 2.4) - 0.055;
        return Math.Max(0, Math.Min(255, (int)Math.Round(value * 255.0)));
    }
    // CIEDE2000（ΔE00）。
    //
    // 一開始用的是 CIE76（Lab 的直線距離），註解裡還寫著「在這個工具關心的距離範圍兩者判斷
    // 一致」——那是沒驗證過的假設，而且是錯的。實測布的染色色碼到它的畫面顏色，CIE76 算 7.8、
    // CIEDE2000 只有 4.7，差了四成。這個差距直接決定預設值能不能用：4.7 讓預設 6 成立，
    // 7.8 則會讓「填色碼掃自己那件東西」在預設值下失敗。
    //
    // CIEDE2000 存在的理由正是修正 CIE76 在藍色與暗色區域的偏差，而遊戲材質幾乎都落在那裡，
    // 所以這裡沒有選便宜那個的空間。
    //
    // 回傳距離的平方交給 Hit 比較（省掉一次開根號），要實際數值的呼叫端用 Distance()。
    static float Squared(float L1, float a1, float b1, float L2, float a2, float b2)
    {
        double C1 = Math.Sqrt(a1 * a1 + b1 * b1), C2 = Math.Sqrt(a2 * a2 + b2 * b2);
        double Cb = (C1 + C2) / 2.0;
        double Cb7 = Cb * Cb * Cb; Cb7 = Cb7 * Cb7 * Cb;          // Cb 的七次方，不用 Math.Pow
        double G = Cb > 0 ? 0.5 * (1.0 - Math.Sqrt(Cb7 / (Cb7 + Pow25_7))) : 0.0;
        double a1p = (1.0 + G) * a1, a2p = (1.0 + G) * a2;
        double C1p = Math.Sqrt(a1p * a1p + b1 * b1), C2p = Math.Sqrt(a2p * a2p + b2 * b2);
        double h1 = Angle(b1, a1p), h2 = Angle(b2, a2p);
        double dLp = L2 - L1, dCp = C2p - C1p;
        double dhp;
        if (C1p * C2p == 0.0) dhp = 0.0;
        else if (Math.Abs(h2 - h1) <= 180.0) dhp = h2 - h1;
        else if (h2 - h1 > 180.0) dhp = h2 - h1 - 360.0;
        else dhp = h2 - h1 + 360.0;
        double dHp = 2.0 * Math.Sqrt(C1p * C2p) * Math.Sin(ToRadians(dhp) / 2.0);
        double Lbp = (L1 + L2) / 2.0, Cbp = (C1p + C2p) / 2.0;
        double hbp;
        if (C1p * C2p == 0.0) hbp = h1 + h2;
        else if (Math.Abs(h1 - h2) <= 180.0) hbp = (h1 + h2) / 2.0;
        else if (h1 + h2 < 360.0) hbp = (h1 + h2 + 360.0) / 2.0;
        else hbp = (h1 + h2 - 360.0) / 2.0;
        double T = 1.0
            - 0.17 * Math.Cos(ToRadians(hbp - 30.0))
            + 0.24 * Math.Cos(ToRadians(2.0 * hbp))
            + 0.32 * Math.Cos(ToRadians(3.0 * hbp + 6.0))
            - 0.20 * Math.Cos(ToRadians(4.0 * hbp - 63.0));
        double offset = (hbp - 275.0) / 25.0;
        double dTheta = 30.0 * Math.Exp(-(offset * offset));
        double Cbp7 = Cbp * Cbp * Cbp; Cbp7 = Cbp7 * Cbp7 * Cbp;
        double Rc = Cbp > 0 ? 2.0 * Math.Sqrt(Cbp7 / (Cbp7 + Pow25_7)) : 0.0;
        double light = Lbp - 50.0;
        double Sl = 1.0 + (0.015 * light * light) / Math.Sqrt(20.0 + light * light);
        double Sc = 1.0 + 0.045 * Cbp, Sh = 1.0 + 0.015 * Cbp * T;
        double Rt = -Math.Sin(ToRadians(2.0 * dTheta)) * Rc;
        double l = dLp / Sl, c = dCp / Sc, h = dHp / Sh;
        return (float)(l * l + c * c + h * h + Rt * c * h);
    }
    const double Pow25_7 = 6103515625.0;   // 25^7
    static double ToRadians(double degrees) { return degrees * Math.PI / 180.0; }
    static double Angle(double y, double x)
    {
        if (x == 0.0 && y == 0.0) return 0.0;
        double degrees = Math.Atan2(y, x) * 180.0 / Math.PI;
        return degrees < 0.0 ? degrees + 360.0 : degrees;
    }
    // 明度差本身就是 ΔE00 的下界：ΔE00 ≥ |ΔL| / Sl，而 Sl 最大約 1.75（明度在 0 或 100 時）。
    // 所以 |ΔL| 超過 1.75 倍門檻的像素一定不命中，可以在算完整公式之前就丟掉。
    // 這是精確的提前退出，不是近似——不會漏掉任何該命中的像素。
    public const float LightnessBound = 1.75f;
    public static float DistanceSquared(float L1, float a1, float b1, float L2, float a2, float b2)
    {
        return Squared(L1, a1, b1, L2, a2, b2);
    }
    public static float Distance(float L1, float a1, float b1, float L2, float a2, float b2)
    {
        return (float)Math.Sqrt(Math.Max(0f, Squared(L1, a1, b1, L2, a2, b2)));
    }
    public static float Distance(Color one, Color other)
    {
        float L1, a1, b1, L2, a2, b2;
        ToLab(one, out L1, out a1, out b1); ToLab(other, out L2, out a2, out b2);
        return Distance(L1, a1, b1, L2, a2, b2);
    }
}
// 把 ColorRule（字串）編譯成比對器（Lab 座標）。掃描迴圈裡不該再解析色碼或重算 Lab，
// 所以樣本的 Lab 在建構時就算完。
//
// 一組一個顏色配一個 ΔE 門檻。實測染色色盤上的色碼到它在畫面上的顏色最多 ΔE 5.6，
// 所以填色碼就能直接掃到東西，不必先吸色。
public class ColorMatch
{
    // 誤差的上下限。ΔE 100 已經是「幾乎任何顏色都算命中」，再往上沒有意義。
    public const int MinTolerance = 0, MaxTolerance = 100;
    // 預設誤差。這個工具的主要用法是「填一個色碼，找畫面上那一件東西」，所以預設值要
    // 剛好夠蓋住同一件東西在畫面上的深淺，不要更寬。
    //
    // 實測色碼到它自己畫面顏色的距離：布 4.71、皮革 5.57、金屬 2.05 —— 所以 6。
    // 不要設 5：那樣皮革會差 0.57 漏掉自己那件東西。
    //
    // 上面有個天花板，比一般人猜的低很多：以布為中心，一個跟它毫無關係的中性深灰
    // #474747 只有 15.7，暗處的金屬 17.4。也就是說誤差開到 16 就會開始命中介面、
    // 陰影、地面那種灰。誤差不是越大越安全，往上開的空間很窄。
    //
    // 曾經預設 12，是為了讓一個色碼同時掃到別種材質上顏色相近的裝備（布的 #2E3045
    // 到皮革的 #002A4B 是 10.0）。那個用法拿掉了：它只在這一對材質上成立，金屬離
    // 另外兩個 38 以上、遠超天花板，怎麼調都跨不過去，留著只會讓人誤以為誤差開大就能配色。
    public const int DefaultTolerance = 6;
    readonly float targetL, targetA, targetB;
    readonly float tolerance;
    public readonly Color Target;
    // 高亮色跟著比對器走，不另外存一條平行清單——兩份資料就沒有不同步的可能。
    public readonly Color Highlight;
    public static ColorMatch Compile(ColorRule rule)
    {
        if (rule == null) throw new Exception("色碼組無效。");
        return new ColorMatch(ColorRule.Parse(rule.Target), rule.Tolerance);
    }
    public ColorMatch(Color target, int tolerance)
    {
        Target = target;
        Highlight = ColorRule.Contrast(target);
        this.tolerance = Math.Max(MinTolerance, Math.Min(MaxTolerance, tolerance));
        Perceptual.ToLab(target, out targetL, out targetA, out targetB);
    }
    public float Tolerance { get { return tolerance; } }
    // 每一個取樣像素都會走到這裡。Lab 由呼叫端算好傳進來，一個像素只算一次，不管有幾組規則。
    //
    // 先用明度差做精確的提前退出（見 Perceptual.LightnessBound），絕大多數像素在這一行就
    // 被擋掉，不必跑完整的 CIEDE2000。比的是距離的平方，再省掉一次開根號。
    public bool Hit(float L, float a, float b)
    {
        if (Math.Abs(L - targetL) > tolerance * Perceptual.LightnessBound) return false;
        return Perceptual.DistanceSquared(L, a, b, targetL, targetA, targetB) <= tolerance * tolerance;
    }
    // 單點測試用（預覽、規則重疊檢查、測試），自己算 Lab。
    public bool Hit(Color colour)
    {
        float L, a, b; Perceptual.ToLab(colour, out L, out a, out b);
        return Hit(L, a, b);
    }
    // 這個顏色離目標色多遠。預覽與診斷用，比「命中／不命中」多了程度資訊。
    public float DistanceTo(Color colour)
    {
        float L, a, b; Perceptual.ToLab(colour, out L, out a, out b);
        return Perceptual.Distance(L, a, b, targetL, targetA, targetB);
    }
    internal void Lab(out float L, out float a, out float b) { L = targetL; a = targetA; b = targetB; }
}
// 一塊要畫的高亮：Bounds 是相對掃描範圍左上角的座標，Rule 是命中的色碼組序號。
public class ScanHit
{
    public Rectangle Bounds; public int Rule;
}
// 一次掃描的結果。Counts／Centers 以色碼組為索引，Centers 是命中格子的重心（相對掃描範圍）。
public class ScanResult
{
    public List<ScanHit> Hits = new List<ScanHit>(); public int[] Counts = new int[0]; public Point[] Centers = new Point[0];
    public int Total { get { return Counts.Sum(); } }
}
// 純粹的像素比對，完全不碰 UI 也不碰螢幕，所以可以直接餵陣列做單元測試。
public static class ColorScanner
{
    // 取 R／G／B 三個通道中最大的差值（Chebyshev 距離）。
    //
    // 比對已經不用這個了（Lab 距離取代了它）。留著只為一件事：自動對比色除了要感知上顯眼，
    // 還要在 RGB 通道上和目標色差得夠遠，因為掃描讀的是原始像素值——高亮若在通道上太接近
    // 目標色，掃描就會把自己畫的東西當成命中。那是一個關於「像素值」的條件，不是關於觀感的，
    // 所以用通道差表達才對。
    public static int Difference(int argb, Color target) { int r = (argb >> 16) & 255, g = (argb >> 8) & 255, b = argb & 255; return Math.Max(Math.Abs(r - target.R), Math.Max(Math.Abs(g - target.G), Math.Abs(b - target.B))); }
    // 以 sample 為間隔走訪像素，命中的取樣點歸進 block 網格，
    // 再把同一列相鄰的格子併成一個矩形，大幅減少要畫的矩形數量。
    //
    // 一個格子只認第一個命中的色碼組（由上往下），結果才穩定。
    // 副作用是：上方色碼組的誤差若已涵蓋下方那組的樣本色，下方那組就永遠不會有命中。
    // 介面上的提示文字有說明，誤差預覽視窗還會把實際重疊的組別算出來講明。
    //
    // 收 ColorMatch 清單而不是「目標色清單＋誤差清單」：兩條平行清單長度可能不一致，
    // 那是隨時可能發生的執行期錯誤；併成一條之後那種錯誤在結構上就不存在了。
    public static ScanResult Scan(int[] pixels, int width, int height, IList<ColorMatch> matches, int sample, int block)
    {
        if (pixels == null || matches == null) throw new Exception("掃描參數不完整。");
        if (width <= 0 || height <= 0 || pixels.Length < width * height) throw new Exception("掃描區域資料不完整。");
        if (sample < 1) sample = 1; if (block < 1) block = 1;
        int rules = matches.Count, cols = (width + block - 1) / block, rows = (height + block - 1) / block;
        var cells = new int[cols * rows]; for (int i = 0; i < cells.Length; i++) cells[i] = -1;
        var result = new ScanResult { Counts = new int[rules], Centers = new Point[rules] };
        if (rules == 0) return result;
        for (int r = 0; r < rules; r++) if (matches[r] == null) throw new Exception("色碼組無效。");
        // Lab 一個像素只算一次，不管有幾組規則——那是這個迴圈裡唯一貴的動作（三個立方根）。
        // 而且只有真的要比對的取樣點才算：已經被別的規則佔走的格子直接跳過。
        for (int y = 0; y < height; y += sample)
        {
            int row = y * width, cellRow = (y / block) * cols;
            for (int x = 0; x < width; x += sample)
            {
                int index = cellRow + x / block; if (cells[index] >= 0) continue;
                float L, a, b; Perceptual.ToLab(pixels[row + x], out L, out a, out b);
                for (int r = 0; r < rules; r++) if (matches[r].Hit(L, a, b)) { cells[index] = r; break; }
            }
        }
        var sumX = new long[rules]; var sumY = new long[rules];
        for (int r = 0; r < rows; r++)
        {
            int c = 0;
            while (c < cols)
            {
                int rule = cells[r * cols + c]; if (rule < 0) { c++; continue; }
                int end = c; while (end + 1 < cols && cells[r * cols + end + 1] == rule) end++;
                int left = c * block, top = r * block, right = Math.Min(width, (end + 1) * block), bottom = Math.Min(height, (r + 1) * block);
                result.Hits.Add(new ScanHit { Rule = rule, Bounds = new Rectangle(left, top, right - left, bottom - top) });
                for (int k = c; k <= end; k++) { result.Counts[rule]++; sumX[rule] += k * block + block / 2; sumY[rule] += r * block + block / 2; }
                c = end + 1;
            }
        }
        for (int r = 0; r < rules; r++) result.Centers[r] = result.Counts[r] == 0 ? Point.Empty : new Point((int)(sumX[r] / result.Counts[r]), (int)(sumY[r] / result.Counts[r]));
        return result;
    }
}
// 螢幕上那個方塊。
//
// 內部全透明（TransparencyKey 是 Magenta），只畫兩樣東西：掃描範圍內的高亮色塊，
// 以及掃描範圍外那一圈邊框與把手。視窗樣式、不搶焦點、滑鼠穿透都由 OverlayForm 處理。
//
// 成員順序：設定 → 結果 → 幾何 → 兩種模式 → 亮暗相位機（掃描核心）→ 拖曳縮放 → 繪製。
public class ScanOverlay : OverlayForm
{
    // ───────────────────────── 設定：由工具視窗填入 ─────────────────────────

    // 編譯好的比對器，每組一個。高亮色從比對器自己身上取，順手快取成繪製用的色盤——
    // 以前是兩條平行清單，那種結構隨時可能長度不一致；現在不可能。
    List<ColorMatch> matches = new List<ColorMatch>();
    List<Color> highlights = new List<Color>();
    public List<ColorMatch> Matches
    {
        get { return matches; }
        set
        {
            matches = value ?? new List<ColorMatch>();
            highlights = matches.Select(m => m.Highlight).ToList();
        }
    }
    // Sample：每隔幾個像素取樣一次，越大越省 CPU 但越容易漏掉細小色塊。
    // Block：命中像素歸進多大的網格，也就是畫出來的高亮色塊尺寸。
    public int Sample = DefaultSample, Block = DefaultBlock;
    // 取樣間隔 1＝每個像素都看，不漏任何細小色塊；色塊大小 3 是「看得到」與「畫得準」的折衷：
    // 1 px 的高亮在畫面上幾乎看不見，太大則會蓋過目標本身的輪廓。
    public const int DefaultSample = 1, DefaultBlock = 3;
    // 掃描間隔的預設值，同時也是下限 —— 欄位下限、Interval 的夾取、ScanProfile 的驗證
    // 全部引用這一個常數。
    //
    // 為什麼有下限：閃爍開啟時亮暗各佔一個間隔，所以閃爍頻率是 1 /（2 × 間隔），
    // 500 ms 對應 1 Hz。再低下去閃爍會快到看不舒服，暗相位也可能短到 DWM 來不及重新合成
    // （掃描就會讀到自己的殘影）。關掉閃爍改走排除擷取，沒有暗相位，掃描頻率就是間隔本身。
    public const int DefaultInterval = 500;

    // ───────────────────────── 結果：回報給工具視窗 ─────────────────────────

    // 只有 Read 與 Scanning 會換掉這份清單，對外唯讀。
    List<ScanHit> hits = new List<ScanHit>();
    public List<ScanHit> Hits { get { return hits; } private set { hits = value; } }
    public event Action<ScanResult> Scanned;   // 每次掃描完成
    public event Action<string> Failed;        // 擷取或比對失敗，掃描已自行停止
    public event Action<Rectangle> AreaChanged;// 使用者拖曳或縮放結束

    // ───────────────────────── 內部狀態與建構 ─────────────────────────

    // Band：方塊四周預留給邊框與把手的寬度。掃描範圍是 Bounds 內縮 Band，
    // 所以我們畫的邊框、把手、尺寸文字全部落在掃描範圍之外，不會被自己掃到。
    // MinSide：掃描範圍的最小邊長。Grip：四角縮放把手的邊長。
    public const int Band = 22, MinSide = 24, Grip = 16;
    static readonly Color Chrome = Color.FromArgb(0, 120, 215);
    static readonly IList<Color> NoColours = new List<Color>();
    readonly Timer timer = new Timer { Interval = DefaultInterval };
    bool locked = true, blink = true, blank;
    int interval = DefaultInterval;
    int mode; Point anchor; Rectangle startBounds;   // 拖曳狀態
    Bitmap buffer; int[] pixels;                     // 擷取用的重複使用緩衝
    public ScanOverlay()
    {
        BackColor = Color.Magenta; TransparencyKey = Color.Magenta; Opacity = .85; Text = "顏色掃描小工具";
        MinimumSize = new Size(MinSide + Band * 2, MinSide + Band * 2);
        timer.Tick += (s, e) => Step();
    }

    // ───────────────────────── 幾何 ─────────────────────────

    // 對外一律用「掃描範圍」溝通；視窗實際大小是掃描範圍再外擴 Band。
    public Rectangle ScanArea
    {
        get { return Rectangle.Inflate(Bounds, -Band, -Band); }
        set { Bounds = Rectangle.Inflate(value, Band, Band); }
    }

    // ───────────────────────── 兩種模式：鎖定／編輯 ─────────────────────────

    // 唯一和其他覆蓋層不同的地方：只有鎖定時才穿透，編輯模式要抓得到邊框。
    protected override bool ClickThrough { get { return locked; } }
    public bool Locked
    {
        get { return locked; }
        set { if (locked == value) return; locked = value; mode = 0; Cursor = Cursors.Default; SyncPassthrough(); Invalidate(); }
    }

    // ───────────────────────── 亮暗相位機：掃描的核心 ─────────────────────────
    //
    // 高亮色塊正好蓋在它偵測到的那些像素上，所以「掃描讀不到自己畫的東西」是必要條件。
    // 若讀得到，下一次就會讀到高亮色而不是目標色，命中隨即消失、再下一次又出現——
    // 色塊會抽動，回報的命中數也會在真實值和 0 之間跳。
    //
    // 有兩種辦法達成這件事，而它們的取捨正好相反，所以「高亮閃爍」這個選項就是在兩者之間選：
    //
    //   閃爍開啟   靠「那一刻真的沒畫」。一個間隔分成亮、暗兩段，只在暗相位掃描。
    //              對人來說亮／暗交替就是閃爍；好處是使用者自己截圖拍得到高亮。
    //
    //   閃爍關閉   靠 WDA_EXCLUDEFROMCAPTURE 請系統把這個視窗排除在螢幕擷取之外。
    //              掃描讀不到自己，就完全不必閃暗相位——畫面全程穩定，掃描頻率也不必打折。
    //              代價是那個旗標分不出「誰在擷取」，使用者自己截圖也拍不到高亮。
    //
    // 為什麼一定要這樣分：先前兩種模式都走暗相位，只是長度不同（閃爍開＝各半個週期、
    // 閃爍關＝只留 40ms）。結果是關掉閃爍反而「閃得更快」——暗相位變短同時也讓週期變短，
    // 於是 1 Hz 變成 2 Hz，還配上一個 40ms 的黑閃，比開著閃爍更刺眼。
    // 那個選項的名字和行為完全對不上，所以現在關閉就是真的不閃。
    //
    // 排除擷取失敗時（Win10 2004 之前的系統）會自動退回暗相位那條路並回報，
    // 因為「讀到自己」是不能默默發生的錯誤。
    //
    // 上一輪沒有任何命中時畫面本來就是乾淨的，那一輪直接跳過暗相位，
    // 所以「什麼都沒找到」的情況完全沒有閃爍也沒有頻率損失。

    // 暗相位至少要這麼長，才夠 DWM 把「不畫」重新合成完畢。
    public const int BlankWindow = 40;
    // 排除擷取生效時不需要暗相位。這個欄位記的是「系統真的接受了」，不是「我們想要」。
    bool excluded;
    // 掃描間隔。
    public int Interval
    {
        get { return interval; }
        set { interval = Math.Max(DefaultInterval, value); if (!blank) timer.Interval = LitLength; }
    }
    // 閃爍開＝走暗相位（截圖拍得到）；閃爍關＝排除擷取（完全不閃）。
    public bool Blink
    {
        get { return blink; }
        set
        {
            if (blink == value) return;
            blink = value;
            excluded = SyncCapture();
            if (blank && !NeedsBlanking) { blank = false; }
            if (!blank) timer.Interval = LitLength;
            Invalidate();
        }
    }
    protected override bool ExcludeFromCapture { get { return !blink; } }
    // 視窗剛建立時 OverlayForm 已經套過一次排除擷取，但只有它知道系統接不接受，
    // 所以這裡再問一次把結果記下來——不然第一次掃描會用到錯的 excluded 值。
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        excluded = SyncCapture();
    }
    // 系統沒接受排除擷取時，只能退回暗相位——否則掃描會讀到自己。
    internal bool NeedsBlanking { get { return blink || !excluded; } }
    // 排除擷取生效時完全不進暗相位，所以這兩個長度只在需要暗相位時才有意義。
    internal int BlankLength { get { return blink ? interval : BlankWindow; } }
    internal int LitLength { get { return !NeedsBlanking ? interval : (blink ? interval : Math.Max(20, interval - BlankWindow)); } }
    // 當下該用哪組顏色畫：暗相位回傳空色盤，OnPaint 就什麼都不畫。
    public IList<Color> Phase { get { return blank ? NoColours : highlights; } }
    internal bool Blanked { get { return blank; } }
    // 相位判斷抽成純函式，方便測試釘住「有命中就必須先清空才掃描」這個性質。
    // needsBlanking 為假（排除擷取生效）時永遠不必清空。
    internal static bool NeedsBlank(int hitCount, bool alreadyBlank, bool needsBlanking)
    {
        return needsBlanking && !alreadyBlank && hitCount > 0;
    }
    // 測試用：直接擺一組命中結果進來，好驗證相位機的行為，不必真的去擷取螢幕。
    internal void SeedHits(params ScanHit[] value) { Hits = new List<ScanHit>(value); }
    // 測試用：假裝系統接受了排除擷取。
    // 沒有這個鉤子就測不到「完全不閃」那條路——測試裡的方塊從來沒 Show() 過，
    // 沒有視窗句柄，SetWindowDisplayAffinity 一定失敗，於是永遠只走得到備援路徑。
    internal void SimulateCaptureExcluded(bool value) { excluded = value; if (!blank) timer.Interval = LitLength; }
    // ApplyRules() 每次按鍵都會設定 Scanning，所以沒變就直接返回，
    // 否則沒在掃描時每敲一個字都白跑一次清空與重繪。
    public bool Scanning
    {
        get { return timer.Enabled; }
        set
        {
            if (value == timer.Enabled) return;
            if (value) { excluded = SyncCapture(); blank = false; timer.Interval = LitLength; timer.Start(); }
            else { timer.Stop(); blank = false; Hits = new List<ScanHit>(); Invalidate(); }
        }
    }
    // 計時器的每一拍：該進暗相位就先清空畫面，已經在暗相位就真的掃描。
    internal void Step()
    {
        if (NeedsBlank(Hits.Count, blank, NeedsBlanking))
        {
            // 先把「不畫」送上畫面，接下來這段空窗留給 DWM 重新合成。
            blank = true; timer.Interval = BlankLength; Invalidate(); if (IsHandleCreated) Update(); return;
        }
        blank = false; timer.Interval = LitLength;
        Read();
        Invalidate();
    }
    // 手動掃描一次（「立即掃描一次」按鈕）。畫面上可能正畫著上一輪的色塊，
    // 所以先清掉、讓它真的上畫面、等合成追上，再讀。
    // 排除擷取生效時讀不到自己，這一段可以整個跳過。
    public void ScanOnce()
    {
        if (NeedsBlanking && Hits.Count > 0 && IsHandleCreated)
        {
            blank = true; Invalidate(); Update();
            System.Threading.Thread.Sleep(BlankWindow);
            blank = false;
        }
        Read();
        Invalidate();
    }
    // 擷取掃描範圍 → 取出像素 → 交給 ColorScanner 比對 → 更新 Hits 並回報。
    // 任何一步失敗就停止掃描並回報，不讓計時器每個週期重複同一個錯誤。
    void Read()
    {
        var area = ScanArea;
        if (area.Width < 1 || area.Height < 1 || Matches.Count == 0) { if (Hits.Count > 0) { Hits = new List<ScanHit>(); Invalidate(); } return; }
        try
        {
            if (buffer == null || buffer.Width != area.Width || buffer.Height != area.Height) { if (buffer != null) buffer.Dispose(); buffer = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb); pixels = new int[area.Width * area.Height]; }
            using (var g = Graphics.FromImage(buffer)) g.CopyFromScreen(area.X, area.Y, 0, 0, area.Size, CopyPixelOperation.SourceCopy);
            var data = buffer.LockBits(new Rectangle(0, 0, area.Width, area.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try { for (int y = 0; y < area.Height; y++) Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), pixels, y * area.Width, area.Width); }
            finally { buffer.UnlockBits(data); }
            var result = ColorScanner.Scan(pixels, area.Width, area.Height, Matches, Sample, Block);
            Hits = result.Hits; Invalidate(); if (Scanned != null) Scanned(result);
        }
        catch (Exception ex) { Scanning = false; if (Failed != null) Failed(ex.Message); }
    }

    // ───────────────────────── 拖曳與縮放（只在編輯模式）─────────────────────────

    // 四個縮放把手，順序為左上、右上、左下、右下。
    public Rectangle[] Grips() { int w = ClientSize.Width, h = ClientSize.Height; return new[] { new Rectangle(0, 0, Grip, Grip), new Rectangle(w - Grip, 0, Grip, Grip), new Rectangle(0, h - Grip, Grip, Grip), new Rectangle(w - Grip, h - Grip, Grip, Grip) }; }
    // 回傳拖曳區域代號：1～4 是上述四個把手（縮放），5 是其餘邊框（整塊移動）。
    public int ZoneAt(Point p) { var grips = Grips(); for (int i = 0; i < grips.Length; i++) if (grips[i].Contains(p)) return i + 1; return 5; }
    // 依區域代號把位移套到視窗邊界上，並保證掃描範圍不小於 MinSide。純函式，方便測試。
    public static Rectangle Transform(Rectangle bounds, int mode, Size delta)
    {
        int min = MinSide + Band * 2;
        if (mode == 5) return new Rectangle(bounds.X + delta.Width, bounds.Y + delta.Height, bounds.Width, bounds.Height);
        int left = bounds.Left, top = bounds.Top, right = bounds.Right, bottom = bounds.Bottom;
        if (mode == 1 || mode == 3) left = Math.Min(left + delta.Width, right - min);
        if (mode == 2 || mode == 4) right = Math.Max(right + delta.Width, left + min);
        if (mode == 1 || mode == 2) top = Math.Min(top + delta.Height, bottom - min);
        if (mode == 3 || mode == 4) bottom = Math.Max(bottom + delta.Height, top + min);
        return new Rectangle(left, top, right - left, bottom - top);
    }
    // 鎖定時方塊完全不接受拖曳（滑鼠事件根本不會進來），這三個方法讓該行為可被測試驗證。
    internal bool BeginDrag(Point client) { if (locked) { mode = 0; return false; } mode = ZoneAt(client); startBounds = Bounds; return true; }
    internal bool DragTo(Size delta) { if (locked || mode == 0) return false; Bounds = Transform(startBounds, mode, delta); Invalidate(); return true; }
    internal void EndDrag() { if (mode == 0) return; mode = 0; if (AreaChanged != null) AreaChanged(ScanArea); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button != MouseButtons.Left) return; if (BeginDrag(e.Location)) anchor = Cursor.Position; }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); if (locked) { Cursor = Cursors.Default; return; }
        if (mode == 0) { int zone = ZoneAt(e.Location); Cursor = zone == 1 || zone == 4 ? Cursors.SizeNWSE : zone == 2 || zone == 3 ? Cursors.SizeNESW : Cursors.SizeAll; return; }
        DragTo(new Size(Cursor.Position.X - anchor.X, Cursor.Position.Y - anchor.Y));
    }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); EndDrag(); }

    // ───────────────────────── 繪製 ─────────────────────────

    public const int StrongAlpha = 215;
    // 底色是 Magenta，也就是 TransparencyKey，所以沒畫到的地方都是全透明且滑鼠穿透。
    // 高亮色塊畫在掃描範圍內；邊框、把手、尺寸文字一律畫在 Band 那圈裡，不侵入掃描範圍。
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var g = e.Graphics; int w = ClientSize.Width, h = ClientSize.Height;
        var inner = new Rectangle(Band, Band, Math.Max(0, w - Band * 2), Math.Max(0, h - Band * 2));
        PaintHits(g, inner.Location, Hits, Phase);
        // 鎖定：只留一圈虛線細框，畫在掃描範圍外側。
        if (locked) { using (var pen = new Pen(Chrome, 2)) { pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash; g.DrawRectangle(pen, Band - 3, Band - 3, inner.Width + 5, inner.Height + 5); } return; }
        // 編輯模式：整圈塗實色才抓得到（透明處會穿透），四角再放白色把手。
        using (var brush = new SolidBrush(Chrome))
        {
            g.FillRectangle(brush, 0, 0, w, Band); g.FillRectangle(brush, 0, h - Band, w, Band);
            g.FillRectangle(brush, 0, Band, Band, h - Band * 2); g.FillRectangle(brush, w - Band, Band, Band, h - Band * 2);
        }
        foreach (var grip in Grips()) g.FillRectangle(Brushes.White, grip);
        using (var font = new Font("Microsoft JhengHei UI", 9, FontStyle.Bold)) g.DrawString(inner.Width + " × " + inner.Height + "　拖曳邊框移動 · 四角縮放", font, Brushes.White, Grip + 4, 3);
    }
    // 把命中色塊畫出來。傳進來的 palette 就是當下相位要用的顏色（暗相位是空的）。
    // 抽成靜態方法，測試才能直接畫到 Bitmap 上比對。
    public static void PaintHits(Graphics g, Point origin, IEnumerable<ScanHit> hits, IList<Color> palette, int alpha = StrongAlpha)
    {
        if (hits == null || palette == null || palette.Count == 0) return;
        int solid = Math.Max(10, Math.Min(255, alpha));
        var brushes = palette.Select(c => new SolidBrush(Color.FromArgb(solid, c))).ToArray();
        try
        {
            foreach (var hit in hits)
            {
                if (hit.Rule < 0 || hit.Rule >= brushes.Length) continue;
                g.FillRectangle(brushes[hit.Rule], hit.Bounds.X + origin.X, hit.Bounds.Y + origin.Y, hit.Bounds.Width, hit.Bounds.Height);
            }
        }
        finally { foreach (var brush in brushes) brush.Dispose(); }
    }
    protected override void Dispose(bool disposing) { if (disposing) { timer.Stop(); timer.Dispose(); if (buffer != null) { buffer.Dispose(); buffer = null; } } base.Dispose(disposing); }
}
// 吸色時跟著游標的小色票。全程滑鼠穿透、不搶焦點，
// 而且刻意和游標錯開，所以永遠不會蓋住正在取樣的那一個像素。
public class ColorBubble : OverlayForm
{
    public static readonly Size Preferred = new Size(206, 56);
    const int Gap = 24;
    Color value = Color.Black;
    public ColorBubble() { Size = Preferred; BackColor = Color.FromArgb(20, 29, 45); Text = "吸色"; }
    // 預設放在游標右下，靠近螢幕邊緣就翻到另一側，最後再夾回螢幕範圍內。
    public static Point Place(Rectangle screen, Point cursor, Size size)
    {
        int x = cursor.X + Gap, y = cursor.Y + Gap;
        if (x + size.Width > screen.Right) x = cursor.X - Gap - size.Width;
        if (y + size.Height > screen.Bottom) y = cursor.Y - Gap - size.Height;
        return new Point(Math.Max(screen.Left, Math.Min(x, screen.Right - size.Width)), Math.Max(screen.Top, Math.Min(y, screen.Bottom - size.Height)));
    }
    public void Track(Point cursor, Color color)
    {
        value = color; Location = Place(Screen.FromPoint(cursor).WorkingArea, cursor, Size);
        if (!Visible) Show();
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var g = e.Graphics;
        using (var brush = new SolidBrush(value)) g.FillRectangle(brush, 12, 12, 32, 32);
        g.DrawRectangle(Pens.White, 12, 12, 32, 32);
        using (var mono = new Font("Consolas", 12, FontStyle.Bold)) g.DrawString(ColorRule.Format(value), mono, Brushes.White, 56, 10);
        using (var hint = new Font("Microsoft JhengHei UI", 8.5f)) g.DrawString("點擊取色 · Esc 或右鍵取消", hint, Brushes.Gainsboro, 56, 32);
    }
}
// 吸色：不蓋任何全螢幕視窗、也不拍快照，改用全域滑鼠鉤子等使用者按下左鍵，
// 按下的那一刻才讀游標下的那一個像素。
//
// 之前試過的兩種做法都有問題：
//   近乎透明的全螢幕接收層 → DWM 合成下 CopyFromScreen 會把那層一起抓進去，顏色偏掉
//   先拍快照再鋪滿全螢幕   → 自己的視窗剛隱藏、下層 app 還沒重畫完就被拍進去，留下殘影
// 現在畫面上唯一屬於我們的東西只有跟著游標、刻意錯開的小色票，
// 取樣點永遠沒有任何覆蓋物，所以不必凍結畫面也不必等待重畫，兩個問題都不存在。
public class ColorPicker : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookCallback fn, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wparam, IntPtr lparam);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)] static extern IntPtr GetModuleHandle(string name);
    delegate IntPtr HookCallback(int code, IntPtr wparam, IntPtr lparam);
    [StructLayout(LayoutKind.Sequential)] struct MousePoint { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] struct KeyStroke { public uint Key, Scan, Flags, Time; public UIntPtr Extra; }
    const int LowLevelMouse = 14, LowLevelKeyboard = 13;
    readonly HookCallback mouseProc, keyProc;
    readonly ColorBubble bubble = new ColorBubble();
    readonly Control host;
    IntPtr mouseHook = IntPtr.Zero, keyHook = IntPtr.Zero;
    Color pending = Color.Black; bool armed, done;
    public event Action<Color?> Finished;
    // host 只用來把結果切回 UI 執行緒，等鉤子回呼結束後才動 UI。
    public ColorPicker(Control host) { this.host = host; mouseProc = OnMouse; keyProc = OnKey; }
    public bool Running { get { return mouseHook != IntPtr.Zero; } }
    // 1×1 的擷取緩衝重複使用。滑鼠移動每秒可達數百次，每次都新建 Bitmap 與 Graphics
    // 會在鉤子回呼裡配置兩個 GDI+ 物件；低階鉤子的回呼超時會被 Windows 靜默移除，
    // 所以這裡要盡量輕。
    Bitmap dot; Graphics dotCanvas;
    public Color Sample(Point p)
    {
        if (dot == null) { dot = new Bitmap(1, 1, PixelFormat.Format32bppArgb); dotCanvas = Graphics.FromImage(dot); }
        dotCanvas.CopyFromScreen(p.X, p.Y, 0, 0, new Size(1, 1), CopyPixelOperation.SourceCopy);
        return Color.FromArgb(255, dot.GetPixel(0, 0));
    }
    // 一次性取色用的版本，不值得為它留著快取。
    public static Color PixelAt(Point p)
    {
        using (var bitmap = new Bitmap(1, 1, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bitmap)) g.CopyFromScreen(p.X, p.Y, 0, 0, new Size(1, 1), CopyPixelOperation.SourceCopy);
            return Color.FromArgb(255, bitmap.GetPixel(0, 0));
        }
    }
    public void Start()
    {
        if (mouseHook != IntPtr.Zero) return;
        var module = GetModuleHandle(null);
        mouseHook = SetWindowsHookEx(LowLevelMouse, mouseProc, module, 0);
        if (mouseHook == IntPtr.Zero) throw new Exception("無法啟動吸色（Windows 錯誤碼 " + Marshal.GetLastWin32Error() + "），請以相同權限層級重試。");
        keyHook = SetWindowsHookEx(LowLevelKeyboard, keyProc, module, 0);   // 只為了 Esc，失敗就仍可用右鍵取消
        Preview(Cursor.Position);
    }
    public void Stop()
    {
        if (mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; }
        if (keyHook != IntPtr.Zero) { UnhookWindowsHookEx(keyHook); keyHook = IntPtr.Zero; }
        if (!bubble.IsDisposed && bubble.Visible) bubble.Hide();
    }
    public void Dispose()
    {
        done = true; Stop(); bubble.Dispose();
        if (dotCanvas != null) { dotCanvas.Dispose(); dotCanvas = null; }
        if (dot != null) { dot.Dispose(); dot = null; }
    }
    void Preview(Point p) { try { bubble.Track(p, Sample(p)); } catch { } }
    // 吸色期間吃掉所有滑鼠按鍵，免得順手在目標程式上點到東西。
    // 左鍵按下就記下顏色、放開才收工，這樣不會漏一個 MouseUp 給目標程式。
    IntPtr OnMouse(int code, IntPtr wparam, IntPtr lparam)
    {
        if (code >= 0 && !done)
        {
            int message = wparam.ToInt32();
            if (message == 0x200) { var p = PointFrom(lparam); if (p.HasValue) Preview(p.Value); }
            else if (message == 0x201) { var p = PointFrom(lparam); armed = p.HasValue; if (armed) { try { pending = Sample(p.Value); } catch { armed = false; } } return new IntPtr(1); }
            else if (message == 0x202) { if (armed) { armed = false; Finish(pending); } return new IntPtr(1); }
            else if (message == 0x205) { Finish(null); return new IntPtr(1); }
            else if (message == 0x204 || message == 0x207 || message == 0x208) return new IntPtr(1);
        }
        return CallNextHookEx(mouseHook, code, wparam, lparam);
    }
    IntPtr OnKey(int code, IntPtr wparam, IntPtr lparam)
    {
        if (code >= 0 && !done && wparam.ToInt32() == 0x100)
        {
            try { var stroke = (KeyStroke)Marshal.PtrToStructure(lparam, typeof(KeyStroke)); if (stroke.Key == 0x1B) { Finish(null); return new IntPtr(1); } }
            catch { }
        }
        return CallNextHookEx(keyHook, code, wparam, lparam);
    }
    static Point? PointFrom(IntPtr lparam)
    {
        try { var data = (MousePoint)Marshal.PtrToStructure(lparam, typeof(MousePoint)); return new Point(data.X, data.Y); }
        catch { return null; }
    }
    void Finish(Color? chosen)
    {
        if (done) return;
        done = true; Stop();
        var handler = Finished;
        if (handler == null) return;
        if (host != null && host.IsHandleCreated) { try { host.BeginInvoke(new Action(() => handler(chosen))); return; } catch { } }
        handler(chosen);
    }
}
// 一列色碼組：樣本色碼與誤差，外加吸色、＋樣本與移除。
// 「＋ 新增色碼組」每按一次就多一列。高亮色不必輸入，色票直接顯示自動算出的對比色。
// 繼承 BufferedEditorPanel（本身就是設好雙緩衝的 TableLayoutPanel），色票與按鈕重繪不閃動。
//
// 一列色碼組：一個色碼與一個誤差，外加吸色與移除。
// 誤差是 Lab 空間的感知距離 ΔE，一個數字同時管好色相、彩度與明暗的取捨。
public class ColorRuleRow : BufferedEditorPanel
{
    public readonly TextBox Target = new TextBox { Width = 120 };
    public readonly NumericUpDown Tolerance = new NumericUpDown
    {
        Minimum = ColorMatch.MinTolerance, Maximum = ColorMatch.MaxTolerance, Value = ColorMatch.DefaultTolerance, Width = 62
    };
    readonly Panel targetSwatch = Swatch(), highlightSwatch = Swatch();
    readonly Label index = new Label { AutoSize = true, ForeColor = Color.DimGray };
    public event Action Changed; public event Action<ColorRuleRow> Removed; public event Action<ColorRuleRow> PickRequested;
    bool syncing;
    static Panel Swatch() { return new Panel { Width = 26, Height = 26, BorderStyle = BorderStyle.FixedSingle }; }
    // 按鈕一律 GrowAndShrink＋內距＋最小寬度。只給 AutoSize 的話，偏好大小會先用預設字型
    // （8.25pt 英文字型）算好，等這一列被加進工具視窗換成 10pt 中文字型後不再放大，文字就被擠壓。
    static Button Small(string text)
    {
        return new Button
        {
            Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 4, 10, 4), MinimumSize = new Size(72, 28)
        };
    }
    // 整列用 TableLayoutPanel，每個格子都只 Anchor 左側，版面就會自動把高矮不同的
    // 標籤、輸入框、色票、按鈕垂直居中；每格的右邊距則決定欄與欄之間的距離。
    public ColorRuleRow(ColorRule rule)
    {
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; RowCount = 1; ColumnCount = 10;
        for (int i = 0; i < ColumnCount; i++) ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Margin = new Padding(0, 0, 0, 6); Padding = new Padding(10, 5, 10, 5); BackColor = Color.White;
        int column = 0;
        Place(index, ref column, 10);
        Place(Cap("色碼"), ref column, 8);
        Place(Target, ref column, 6);
        Place(targetSwatch, ref column, 12);
        var pick = Small("吸色"); pick.Click += (s, e) => { if (PickRequested != null) PickRequested(this); };
        Place(pick, ref column, 18);
        Place(Cap("高亮"), ref column, 8);
        Place(highlightSwatch, ref column, 18);
        Place(Cap("誤差 ΔE"), ref column, 8);
        Place(Tolerance, ref column, 18);
        var remove = Small("移除"); remove.Click += (s, e) => { if (Removed != null) Removed(this); };
        Place(remove, ref column, 0);
        Target.TextChanged += (s, e) => { if (!syncing) Sync(true); };
        Tolerance.ValueChanged += (s, e) => { if (!syncing) Sync(true); };
        Value = rule ?? new ColorRule();
    }
    void Place(Control child, ref int column, int gap)
    {
        child.Anchor = AnchorStyles.Left; child.Margin = new Padding(0, 0, gap, 0);
        Controls.Add(child, column++, 0);
    }
    // 換字型後（被加進工具視窗時就會發生）重算一次，AutoSize 的偏好大小才會跟著長大。
    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        foreach (Control child in Controls) child.PerformLayout();
        PerformLayout();
    }
    static Label Cap(string text) { return new Label { Text = text, AutoSize = true }; }
    static int Clamp(int value, int low, int high) { return Math.Max(low, Math.Min(high, value)); }
    public void SetIndex(int number) { index.Text = "#" + number; }
    public ColorRule Value
    {
        get { return new ColorRule { Target = Target.Text, Tolerance = (int)Tolerance.Value }; }
        set
        {
            var rule = value ?? new ColorRule();
            syncing = true;
            try
            {
                Target.Text = rule.Target;
                Tolerance.Value = Clamp(rule.Tolerance, ColorMatch.MinTolerance, ColorMatch.MaxTolerance);
            }
            finally { syncing = false; }
            Sync(false);
        }
    }
    // 解析一次就好，好幾個地方共用結果。誤差有 NumericUpDown 限制範圍，不必再驗。
    bool Resolve(out Color target) { return ColorRule.TryParse(Target.Text, out target); }
    public bool IsValid { get { Color ignored; return Resolve(out ignored); } }
    // 目標色解析不出來時回中性色而不是丟例外——屬性 getter 不該丟例外。
    public Color Highlight
    {
        get { Color target; return Resolve(out target) ? ColorRule.Contrast(target) : SystemColors.Control; }
    }
    // 編譯好的比對器，解析不出來就回 null（呼叫端本來就只送 IsValid 的列去掃描）。
    public ColorMatch Compiled
    {
        get { try { return ColorMatch.Compile(Value); } catch { return null; } }
    }
    void Sync(bool notify)
    {
        Color target;
        bool valid = Resolve(out target);
        targetSwatch.BackColor = valid ? target : SystemColors.Control;
        highlightSwatch.BackColor = valid ? ColorRule.Contrast(target) : SystemColors.Control;
        if (notify && Changed != null) Changed();
    }
}
// 誤差邊界上的一個顏色。
// Inside 是拿這個顏色真的去問過比對器的結果——sRGB 只覆蓋 Lab 空間的一小塊，推到邊界的
// 座標常常落在色域外而被夾回來，夾過的距離就變了。與其去猜，不如問一次然後照實標示。
//
// 刻意沒有「最糟的角落」這種標記：Lab 是歐氏空間，八個角和六個軸向離目標色的距離完全相同
//（測試釘住了這件事），所以沒有哪一格比別格更糟。
public class ToleranceSample
{
    public Color Colour; public string Label; public bool Inside;
}
// 一排極限色，配一句說明這排在示範什麼。
public class ToleranceGroup
{
    public string Caption; public List<ToleranceSample> Samples = new List<ToleranceSample>();
}
// 誤差極限預覽：把「還會被判定為命中」的最極端顏色列出來。
//
// 為什麼需要這個工具：誤差是一個數字，但它圈出來的範圍是三維的。光看「12」完全想像不出
// 邊界長什麼樣，只能一邊掃一邊猜。把邊界上最極端的幾個點直接畫出來，色偏有多嚴重就一目了然
// ——畫出來的每一個顏色都會被判定為命中。
//
// 每個色票中央再畫上這組實際會用的高亮色，順便驗證另一件事：
// 高亮色必須在整個誤差範圍內都看得出來，否則命中了也看不到。
//
// 產生極限色與繪製都是靜態且不碰視窗的，所以自我測試可以直接把整頁畫成 PNG 檢查。
public static class TolerancePreview
{
    const int Pad = 16, CellWidth = 78, CellHeight = 96, SwatchWidth = 72, SwatchHeight = 56, Inner = 20;
    public static List<ToleranceGroup> Extremes(ColorRule rule)
    {
        var groups = new List<ToleranceGroup>();
        ColorMatch match;
        try { match = ColorMatch.Compile(rule); } catch { return groups; }
        float L, a, b;
        match.Lab(out L, out a, out b);
        float step = match.Tolerance;
        // Lab 的三個軸就是人眼感受的三個方向，所以極限色沿著這三個軸推出去最有解釋力：
        //   L 明暗、a 綠↔紅、b 藍↔黃
        // 誤差是這三個方向合成的距離，所以單軸推滿是「只有一個方向偏掉」的極限，
        // 而三軸各推 1/√3 是「三個方向同時偏」的極限——後者才是最糟的情況。
        var axes = new ToleranceGroup { Caption = "單一方向推到邊界（Lab 三軸）——每個色票都是該方向上還會命中的最極端顏色" };
        Add(axes, match, match.Target, "目標");
        Add(axes, match, Edge(match, L, a, b, -step, 0f, 0f), "更暗 L−" + Round(step));
        Add(axes, match, Edge(match, L, a, b, step, 0f, 0f), "更亮 L+" + Round(step));
        Add(axes, match, Edge(match, L, a, b, 0f, -step, 0f), "偏綠 a−" + Round(step));
        Add(axes, match, Edge(match, L, a, b, 0f, step, 0f), "偏紅 a+" + Round(step));
        Add(axes, match, Edge(match, L, a, b, 0f, 0f, -step), "偏藍 b−" + Round(step));
        Add(axes, match, Edge(match, L, a, b, 0f, 0f, step), "偏黃 b+" + Round(step));
        groups.Add(axes);
        float diagonal = step / (float)Math.Sqrt(3.0);
        var corners = new ToleranceGroup { Caption = "三個方向同時偏（Lab 的八個角）——離目標色的距離和上面一排完全相同，只是偏的方向不同" };
        foreach (int i in new[] { -1, 1 })
            foreach (int j in new[] { -1, 1 })
                foreach (int k in new[] { -1, 1 })
                    Add(corners, match, Edge(match, L, a, b, i * diagonal, j * diagonal, k * diagonal),
                        Sign(i) + Sign(j) + Sign(k));
        groups.Add(corners);
        return groups;
    }
    static string Round(float value) { return value.ToString("0.#"); }
    // 同一排裡重複的顏色沒有資訊量（誤差 0、或目標色本來就貼著色域邊界時就會撞在一起）。
    // Inside 一律拿真正的比對器問過，預覽才不會宣稱一個其實不命中的顏色會命中。
    static void Add(ToleranceGroup group, ColorMatch match, Color colour, string label)
    {
        int argb = colour.ToArgb();
        if (group.Samples.Any(s => s.Colour.ToArgb() == argb)) return;
        group.Samples.Add(new ToleranceSample { Colour = colour, Label = label, Inside = match.Hit(colour) });
    }
    // 找出某個 Lab 方向上「還會命中的最極端顏色」。
    //
    // 為什麼不直接用邊界值：Lab 的座標不是每一點都畫得出來。sRGB 只覆蓋 Lab 空間的一小塊，
    // 推出去的座標常常落在色域外，反算回 RGB 時會被夾住，而夾過的顏色距離就變了
    //（通常變得更遠，於是那個色票其實不命中）。暗色特別嚴重，因為它們貼著色域的角落。
    // 所以不去猜一個安全的內縮比例，而是從邊界往目標色退，取第一個真的命中的。
    // 退到最後一階就是目標色本身，所以正常情況一定會找到。
    const int EdgeSteps = 24;
    static Color Edge(ColorMatch match, float L, float a, float b, float dL, float da, float db)
    {
        for (int i = 0; i <= EdgeSteps; i++)
        {
            float factor = 1f - i / (float)EdgeSteps;
            var candidate = Perceptual.FromLab(L + dL * factor, a + da * factor, b + db * factor);
            if (match.Hit(candidate)) return candidate;
        }
        return Perceptual.FromLab(L, a, b);
    }
    static string Sign(int value) { return value > 0 ? "+" : "−"; }
    // 由上往下比對，所以上面的規則若已經吃下下面那組的樣本色，下面那組永遠不會有命中。
    // 這是真的會讓人踩到的坑（感知上協調的材質距離很近，很容易互相吃掉），所以直接算出來講明，
    // 不要只在說明文字裡提一句。
    public static List<string> Overlaps(IList<ColorRule> rules)
    {
        var warnings = new List<string>();
        if (rules == null) return warnings;
        var compiled = new List<ColorMatch>();
        foreach (var rule in rules)
        {
            ColorMatch match = null;
            try { match = ColorMatch.Compile(rule); } catch { match = null; }
            compiled.Add(match);
        }
        for (int lower = 0; lower < compiled.Count; lower++)
        {
            if (compiled[lower] == null) continue;
            for (int upper = 0; upper < lower; upper++)
            {
                if (compiled[upper] == null) continue;
                if (!compiled[upper].Hit(compiled[lower].Target)) continue;
                warnings.Add("#" + (lower + 1) + " 的色碼落在 #" + (upper + 1) + " 的誤差內（相距 ΔE "
                    + compiled[upper].DistanceTo(compiled[lower].Target).ToString("0.#")
                    + "），#" + (lower + 1) + " 永遠不會有命中。把它移到 #" + (upper + 1) + " 上面，或縮小 #" + (upper + 1) + " 的誤差。");
            }
        }
        return warnings;
    }
    // 一句話交代這組規則的比對設定，色票上方那行就是它。
    public static string Describe(ColorRule rule)
    {
        if (rule == null) return "";
        Color ignored;
        if (!ColorRule.TryParse(rule.Target, out ignored)) return "色碼還沒填好";
        return "感知距離 ΔE ≤ " + rule.Tolerance;
    }
    // g 傳 null 就只算高度，不畫東西——同一份版面計算同時服務捲動範圍與實際繪製，
    // 兩邊不會算出不一樣的結果。
    public static int Layout(int width, IList<ColorRule> rules, Graphics g)
    {
        int y = Pad, right = Math.Max(CellWidth + Pad * 2, width) - Pad;
        using (var head = new Font("Microsoft JhengHei UI", 10.5f, FontStyle.Bold))
        using (var body = new Font("Microsoft JhengHei UI", 9))
        using (var tiny = new Font("Microsoft JhengHei UI", 8))
        using (var mono = new Font("Consolas", 8))
        using (var warn = new SolidBrush(Color.FromArgb(176, 60, 0)))
        {
            if (g != null) g.Clear(Color.White);
            var warnings = Overlaps(rules);
            if (warnings.Count > 0)
            {
                Text(g, "規則互相重疊", head, warn, Pad, y); y += 24;
                foreach (string line in warnings) { Text(g, "• " + line, body, warn, Pad, y); y += 20; }
                y += 8;
            }
            if (rules == null || rules.Count == 0)
            {
                Text(g, "還沒有色碼組。先在工具視窗按「＋ 新增色碼組」填一個顏色。", body, Brushes.DimGray, Pad, y);
                return y + 30 + Pad;
            }
            for (int i = 0; i < rules.Count; i++)
            {
                var rule = rules[i];
                Text(g, "#" + (i + 1) + "　" + (rule == null ? "" : (rule.Target ?? "").Trim()), head, Brushes.Black, Pad, y); y += 24;
                Text(g, Describe(rule), body, Brushes.DimGray, Pad, y); y += 24;
                // 下面整段都會碰 rule.Target，所以空的那一列在這裡就收掉，不要靠「Extremes 剛好回空清單」擋著。
                if (rule == null) { y += 10; continue; }
                foreach (var group in Extremes(rule))
                {
                    Text(g, group.Caption, tiny, Brushes.DimGray, Pad, y); y += 18;
                    int x = Pad;
                    Color parsed;
                    var highlight = ColorRule.TryParse(rule.Target, out parsed) ? ColorRule.Contrast(parsed) : Color.Black;
                    foreach (var one in group.Samples)
                    {
                        if (x + CellWidth > right && x > Pad) { x = Pad; y += CellHeight; }
                        var box = new Rectangle(x, y, SwatchWidth, SwatchHeight);
                        Fill(g, one.Colour, box);
                        // 中央那一小塊就是實際會畫上去的高亮色：在整個誤差範圍內都該看得出來。
                        Fill(g, Color.FromArgb(ScanOverlay.StrongAlpha, highlight), new Rectangle(box.X + (SwatchWidth - Inner) / 2, box.Y + (SwatchHeight - Inner) / 2, Inner, Inner));
                        // 邊界外的色票用虛線細框，而且標籤會說出來——預覽不該宣稱一個其實不命中的顏色會命中。
                        if (g != null)
                            using (var pen = new Pen(one.Inside ? Color.Silver : Color.Gray, 1))
                            {
                                if (!one.Inside) pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                                g.DrawRectangle(pen, box);
                            }
                        Text(g, ColorRule.Format(one.Colour), mono, one.Inside ? Brushes.Black : Brushes.Gray, x, y + SwatchHeight + 3);
                        Text(g, one.Label + (one.Inside ? "" : "（界外）"), tiny, Brushes.DimGray, x, y + SwatchHeight + 19);
                        x += CellWidth;
                    }
                    y += CellHeight + 6;
                }
                y += 10;
            }
            Text(g, "虛線框＝已經在誤差外，不會命中（那個方向出了 sRGB 色域）。中央小方塊是實際會畫上去的高亮色。", tiny, Brushes.DimGray, Pad, y);
            y += 24;
        }
        return y + Pad;
    }
    static void Text(Graphics g, string value, Font font, Brush brush, int x, int y)
    {
        if (g != null && !string.IsNullOrEmpty(value)) g.DrawString(value, font, brush, x, y);
    }
    static void Fill(Graphics g, Color colour, Rectangle box)
    {
        if (g == null) return;
        using (var brush = new SolidBrush(colour)) g.FillRectangle(brush, box);
    }
}
// 誤差預覽視窗。整頁自繪而不是擺幾十個控制項：色票的數量隨規則與樣本數變動，
// 自繪省掉不斷建立與回收控制項的麻煩，也讓同一份繪製程式可以直接輸出成 PNG 供自我測試檢查。
public class ToleranceForm : Form
{
    readonly Sheet sheet = new Sheet();
    bool laying;
    class Sheet : Panel
    {
        public List<ColorRule> Rules = new List<ColorRule>();
        public Sheet() { Dock = DockStyle.Top; DoubleBuffered = true; BackColor = Color.White; }
        protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); TolerancePreview.Layout(ClientSize.Width, Rules, e.Graphics); }
    }
    public ToleranceForm(IList<ColorRule> rules)
    {
        Icon = AppIdentity.Icon; Text = "誤差極限預覽"; Size = new Size(920, 620); MinimumSize = new Size(560, 360);
        // CenterParent 只在 ShowDialog 生效，這個視窗是非模態的（要能一邊看一邊調誤差），所以用 CenterScreen。
        Font = new Font("Microsoft JhengHei UI", 10); StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.White; AutoScroll = true;
        sheet.Rules = rules == null ? new List<ColorRule>() : rules.ToList();
        Controls.Add(sheet);
        // 高度由版面計算決定，寬度靠 Dock.Top 跟著視窗。只在值真的變了才設，否則會和版面事件互相觸發。
        ClientSizeChanged += (s, e) => Relayout();
        Relayout();
    }
    // 改 sheet 的高度會讓 AutoScroll 的捲軸出現或消失，客戶區寬度跟著變，於是又回到這裡。
    // 沒有這個旗標，換寬度與換高度會互相觸發個不停。
    void Relayout()
    {
        if (laying) return;
        laying = true;
        try
        {
            int width = sheet.ClientSize.Width > 0 ? sheet.ClientSize.Width : ClientSize.Width;
            int wanted = TolerancePreview.Layout(width, sheet.Rules, null);
            if (sheet.Height != wanted) sheet.Height = wanted;
            sheet.Invalidate();
        }
        finally { laying = false; }
    }
}
// 會被寫進 JSON 的完整設定。所有欄位都用屬性，JavaScriptSerializer 才讀寫得到；
// 舊檔案沒有的欄位會保留建構子的預設值，多出來的欄位則被忽略，所以新增欄位不會弄壞舊檔。
public class ScanProfile
{
    public string Kind { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Interval { get; set; }
    public int Sample { get; set; }
    public int Block { get; set; }
    public bool Blink { get; set; }
    public List<ColorRule> Rules { get; set; }
    // 預設值直接引用方塊那邊的常數，避免同一個數字有兩個來源。
    public ScanProfile()
    {
        Kind = "MacroColorScan"; Width = 320; Height = 240;
        Interval = ScanOverlay.DefaultInterval; Sample = ScanOverlay.DefaultSample; Block = ScanOverlay.DefaultBlock;
        Blink = true; Rules = new List<ColorRule>();
    }
    public static ScanProfile Centered()
    {
        var profile = new ScanProfile(); var screen = Screen.PrimaryScreen.WorkingArea;
        profile.X = screen.Left + (screen.Width - profile.Width) / 2; profile.Y = screen.Top + (screen.Height - profile.Height) / 2;
        profile.Rules.Add(new ColorRule()); return profile;
    }
    public Rectangle Area { get { return new Rectangle(X, Y, Width, Height); } }
    public static void Validate(ScanProfile p)
    {
        if (p == null || p.Rules == null) throw new Exception("掃描設定無效。");
        if (p.Rules.Count > 20) throw new Exception("色碼組最多 20 組。");
        if (Math.Abs((long)p.X) > 100000 || Math.Abs((long)p.Y) > 100000) throw new Exception("掃描區域座標超出允許範圍。");
        if (p.Width < ScanOverlay.MinSide || p.Height < ScanOverlay.MinSide || p.Width > 8000 || p.Height > 8000) throw new Exception("掃描區域大小需介於 " + ScanOverlay.MinSide + "～8000 像素。");
        if (p.Interval < ScanOverlay.DefaultInterval || p.Interval > 10000) throw new Exception("掃描間隔需介於 " + ScanOverlay.DefaultInterval + "～10000 毫秒。");
        if (p.Sample < 1 || p.Sample > 32) throw new Exception("取樣間隔需介於 1～32 像素。");
        if (p.Block < 1 || p.Block > 64) throw new Exception("色塊大小需介於 1～64 像素。");
        foreach (var rule in p.Rules) ColorRule.Validate(rule);
    }
}
// 設定的自動保存。序列化走 Json、寫檔走 JsonFile（暫存檔 → 置換），
// 沿用 EvaMacroStudio 資料夾，和動作範本的自動保存放在一起。
public static class ScanSession
{
    public static string DefaultPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EvaMacroStudio", "scan-profile.json"); } }
    public static ScanProfile Load(string path)
    {
        if (!File.Exists(path)) return ScanProfile.Centered();
        var profile = Json.Read<ScanProfile>(File.ReadAllText(path));
        if (profile == null || profile.Kind != "MacroColorScan") throw new Exception("掃描設定檔格式不符。");
        ScanProfile.Validate(profile); return profile;
    }
    public static void Save(string path, ScanProfile profile)
    {
        ScanProfile.Validate(profile);
        JsonFile.Write(path, profile, createFolder: true);
    }
}
// 工具視窗：把方塊、色碼組、掃描參數接起來，並負責 F7／F8 熱鍵與設定的載入保存。
// 非模態，可與動作範本編輯同時使用；建構子傳入 profile 時不讀寫磁碟（測試用）。
public class ScanStudioForm : Form
{
    internal readonly ScanOverlay overlay = new ScanOverlay();
    internal readonly FlowLayoutPanel rules = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.FromArgb(238, 238, 238), Padding = new Padding(6) };
    internal readonly NumericUpDown areaX = Num(-100000, 100000, 0), areaY = Num(-100000, 100000, 0), areaW = Num(ScanOverlay.MinSide, 8000, 320), areaH = Num(ScanOverlay.MinSide, 8000, 240);
    // 下限與預設都引用方塊那邊的 DefaultInterval，避免欄位收得進去、方塊那邊又默默夾掉。
    internal readonly NumericUpDown interval = Num(ScanOverlay.DefaultInterval, 10000, ScanOverlay.DefaultInterval), sample = Num(1, 32, ScanOverlay.DefaultSample), block = Num(1, 64, ScanOverlay.DefaultBlock);
    internal readonly Button scanButton = Flat("開始掃描 (F7)"), lockButton = Flat("鎖定掃描範圍"), addRule = Flat("＋ 新增色碼組"), centerButton = Flat("置中於主螢幕"), previewButton = Flat("誤差預覽");
    internal readonly CheckBox blink = new CheckBox { Text = "高亮閃爍（截圖拍得到）", AutoSize = true, Checked = true, Margin = new Padding(16, 9, 0, 0) };
    internal readonly Label status = new Label { AutoSize = true, Margin = new Padding(2, 8, 2, 4), Text = "就緒｜新增色碼組後按「開始掃描 (F7)」" };
    public const int HotStart = 7, HotStop = 8, KeyStart = 118, KeyStop = 119;
    // 所有按鈕統一最小高度，FlowLayoutPanel 是靠上對齊，高度一致才看起來整齊。
    const int ButtonHeight = 30;
    readonly bool persist; bool syncing, picking, overlayShown, scanning, hotkeysReady, ownerMinimized;
    FormWindowState ownerState = FormWindowState.Normal;
    // 吸色進行中的狀態：哪一列在等顏色、當時把哪些視窗讓開了、以及讓開前的視窗狀態。
    ColorPicker picker; ColorRuleRow pickRow; bool pickOverlay, pickSelf, pickParent;
    FormWindowState pickSelfState = FormWindowState.Normal, pickParentState = FormWindowState.Normal;
    // 保險絲：低階鉤子若被系統靜默移除，吸色就永遠不會結束，靠這個逾時把狀態收回來。
    readonly Timer pickGuard = new Timer { Interval = 30000 };
    static NumericUpDown Num(int min, int max, int value) { return new NumericUpDown { Minimum = min, Maximum = max, Value = value, Width = 92 }; }
    static Button Flat(string text)
    {
        return new Button
        {
            Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12, 4, 12, 4), MinimumSize = new Size(0, ButtonHeight), Margin = new Padding(0, 2, 10, 4),
            BackColor = Color.White, UseVisualStyleBackColor = false, FlatStyle = FlatStyle.Flat
        };
    }
    static Label Hint(string text) { return new Label { Text = text, AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(10, 4, 0, 0) }; }
    public ScanStudioForm() : this(null) { }
    public ScanStudioForm(ScanProfile profile)
    {
        persist = profile == null;
        Icon = AppIdentity.Icon; Text = "顏色掃描小工具"; Size = new Size(980, 560); MinimumSize = new Size(820, 460); BackColor = Color.FromArgb(245, 247, 250);
        Font = new Font("Microsoft JhengHei UI", 10); StartPosition = FormStartPosition.CenterScreen; DoubleBuffered = true;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(14, 10, 14, 6) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 6; i++) layout.RowStyles.Add(new RowStyle(i == 4 ? SizeType.Percent : SizeType.AutoSize, i == 4 ? 100 : 0));
        Controls.Add(layout);
        var toolbar = Row(); layout.Controls.Add(toolbar, 0, 0);
        // 只加粗不放大，否則這顆會比同列其他按鈕高一截。
        scanButton.Font = new Font(Font, FontStyle.Bold); scanButton.BackColor = Color.LightGreen;
        toolbar.Controls.Add(scanButton); toolbar.Controls.Add(lockButton);
        var once = Flat("立即掃描一次"); once.Click += (s, e) => Guard(() => { overlay.ScanOnce(); status.Text = "已掃描一次"; }); toolbar.Controls.Add(once);
        toolbar.Controls.Add(blink);
        var geometry = Row(); layout.Controls.Add(geometry, 0, 1);
        Field(geometry, "區域 X", areaX); Field(geometry, "區域 Y", areaY); Field(geometry, "寬", areaW); Field(geometry, "高", areaH);
        centerButton.Click += (s, e) => Guard(Center); geometry.Controls.Add(centerButton);
        var timing = Row(); layout.Controls.Add(timing, 0, 2);
        Field(timing, "掃描間隔（ms）", interval); Field(timing, "取樣間隔（px）", sample); Field(timing, "色塊大小（px）", block);
        var adder = Row(); layout.Controls.Add(adder, 0, 3);
        adder.Controls.Add(addRule);
        adder.Controls.Add(previewButton);
        // 說明文字只講使用者看得到的因果：怎麼調、調過頭長什麼樣。
        // 不解釋 ΔE 是什麼公式，也不教材質分類——那會讓人以為誤差是拿來配色的。
        adder.Controls.Add(Hint(
            "預設 6 夠涵蓋同一件東西在畫面上的深淺色域\n" +
            "掃不到就往上加一點；用吸色取出的顏色可以調到 3\n" +
            "上限 15：再往上會命中無關的深灰，整塊的色塊碎成散落各處的雜點——看到雜點就調小\n" +
            "由上往下比對：上方色碼組吃掉的顏色，下方那組就不會有命中"));
        layout.Controls.Add(rules, 0, 4); layout.Controls.Add(status, 0, 5);
        addRule.Click += (s, e) => Guard(() => { AddRule(new ColorRule()); status.Text = "已新增色碼組 #" + rules.Controls.Count + "｜預設誤差 " + ColorMatch.DefaultTolerance + "，掃不到往上加、看到雜點往下調（上限 15）"; });
        previewButton.Click += (s, e) => Guard(ShowTolerancePreview);
        scanButton.Click += (s, e) => Guard(() => SetScanning(!scanning));
        lockButton.Click += (s, e) => Guard(() => SetLocked(!overlay.Locked));
        // 關掉閃爍要靠排除擷取才能真的不閃。系統不支援時 overlay 會自己退回暗相位，
        // 那件事使用者看得到（畫面還是在閃），所以狀態列必須說出來而不是假裝成功。
        blink.CheckedChanged += (s, e) =>
        {
            overlay.Blink = blink.Checked;
            if (syncing) return;
            if (blink.Checked) status.Text = "高亮改為閃爍（亮暗各半，1 秒一次）｜你自己截圖拍得到高亮。";
            else if (overlay.NeedsBlanking) status.Text = "這台電腦不支援把視窗排除在螢幕擷取之外（需要 Win10 2004 以後），高亮仍會短暫閃爍。";
            else status.Text = "高亮改為持續顯示，完全不閃｜代價是你自己截圖時拍不到高亮，要截圖請勾回閃爍。";
        };
        foreach (var field in new[] { areaX, areaY, areaW, areaH }) field.ValueChanged += (s, e) => { if (!syncing) Guard(ApplyArea); };
        interval.ValueChanged += (s, e) => { if (!syncing) overlay.Interval = (int)interval.Value; };
        sample.ValueChanged += (s, e) => { if (!syncing) overlay.Sample = (int)sample.Value; };
        block.ValueChanged += (s, e) => { if (!syncing) overlay.Block = (int)block.Value; };
        overlay.AreaChanged += area => SyncArea(area);
        overlay.Scanned += Report;
        overlay.Failed += message => { status.Text = "掃描已暫停：" + message; };
        string error = null;
        if (persist) { try { profile = ScanSession.Load(ScanSession.DefaultPath); } catch (Exception ex) { error = ex.Message; profile = ScanProfile.Centered(); } }
        Apply(profile);
        ShowOverlay(true); SetLocked(false);
        if (error != null) status.Text = "無法載入上次的掃描設定（已改用預設值）：" + error;
        // 鉤子被系統移除時吸色不會自己結束，逾時就把視窗與狀態收回來。
        // 訊息裡的秒數直接從 Interval 推導，改了間隔不會和文字說法對不上。
        pickGuard.Tick += (s, e) => { pickGuard.Stop(); if (!picking) return; PickDone(null); status.Text = "吸色逾時已取消（" + pickGuard.Interval / 1000 + " 秒內未取色）。"; };
        // 關閉時若還在吸色，先把鉤子收掉再走；不要擋住關閉，否則主視窗會關不掉。
        FormClosing += (s, e) => { if (picking) PickDone(null); scanning = false; overlay.Scanning = false; RestoreOwner(); Persist(); };
    }
    // F7 開始掃描、F8 結束掃描；工具視窗縮小時仍收得到，關閉視窗即釋放。
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        bool start = Native.RegisterHotKey(Handle, HotStart, 0x4000, KeyStart), stop = Native.RegisterHotKey(Handle, HotStop, 0x4000, KeyStop);
        hotkeysReady = start && stop;
        if (!hotkeysReady) status.Text = "F7／F8 已被其他程式占用，請改用按鈕操作掃描。";
    }
    protected override void OnHandleDestroyed(EventArgs e) { Native.UnregisterHotKey(Handle, HotStart); Native.UnregisterHotKey(Handle, HotStop); base.OnHandleDestroyed(e); }
    protected override void WndProc(ref Message m)
    {
        // 吸色期間會 DoEvents，熱鍵必須忽略，否則會在取色中途跑去開關掃描。
        if (m.Msg == 0x312 && !picking) { int id = m.WParam.ToInt32(); if (id == HotStart && !scanning) Guard(() => SetScanning(true)); else if (id == HotStop && scanning) Guard(() => SetScanning(false)); }
        base.WndProc(ref m);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            pickGuard.Stop(); pickGuard.Dispose();
            if (picker != null) { picker.Finished -= PickDone; picker.Dispose(); picker = null; }
            if (overlay != null && !overlay.IsDisposed) { overlay.Scanning = false; overlay.Dispose(); }
        }
        base.Dispose(disposing);
    }
    protected override void OnShown(EventArgs e) { base.OnShown(e); ShowOverlay(true); }
    static FlowLayoutPanel Row() { return new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = new Padding(0, 0, 0, 8) }; }
    // 標籤與輸入框放進 TableLayoutPanel 的兩個格子，兩邊都只 Anchor 左側，
    // 沒有 Top／Bottom 的格子會由版面自動垂直居中，換字型或換 DPI 都不必手調 Margin。
    static void Field(FlowLayoutPanel parent, string label, Control control)
    {
        var field = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 2, 18, 4) };
        field.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        field.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        field.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var caption = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 8, 0) };
        control.Margin = Padding.Empty; control.Anchor = AnchorStyles.Left;
        field.Controls.Add(caption, 0, 0); field.Controls.Add(control, 1, 0);
        parent.Controls.Add(field);
    }
    void Guard(Action action) { try { action(); } catch (Exception ex) { status.Text = ex.Message; } }
    public void Apply(ScanProfile profile)
    {
        if (profile == null) profile = ScanProfile.Centered();
        ScanProfile.Validate(profile);
        syncing = true;
        try
        {
            foreach (var row in Rows().ToArray()) { rules.Controls.Remove(row); row.Dispose(); }
            areaX.Value = profile.X; areaY.Value = profile.Y; areaW.Value = Math.Max(areaW.Minimum, profile.Width); areaH.Value = Math.Max(areaH.Minimum, profile.Height);
            interval.Value = profile.Interval; sample.Value = profile.Sample; block.Value = profile.Block; blink.Checked = profile.Blink;
        }
        finally { syncing = false; }
        overlay.Interval = profile.Interval; overlay.Sample = profile.Sample; overlay.Block = profile.Block; overlay.Blink = profile.Blink;
        foreach (var rule in profile.Rules) AddRule(rule.Copy());
        ApplyArea();
    }
    public ScanProfile Current()
    {
        var area = overlay.ScanArea;
        return new ScanProfile { X = area.X, Y = area.Y, Width = area.Width, Height = area.Height, Interval = (int)interval.Value, Sample = (int)sample.Value, Block = (int)block.Value, Blink = blink.Checked, Rules = Rows().Select(r => r.Value).Where(r => { try { ColorRule.Validate(r); return true; } catch { return false; } }).ToList() };
    }
    void Persist() { if (!persist) return; try { ScanSession.Save(ScanSession.DefaultPath, Current()); } catch { } }
    internal IEnumerable<ColorRuleRow> Rows() { return rules.Controls.OfType<ColorRuleRow>(); }
    public ColorRuleRow AddRule(ColorRule rule)
    {
        if (rules.Controls.Count >= 20) throw new Exception("色碼組最多 20 組。");
        var row = new ColorRuleRow(rule);
        row.Changed += ApplyRules;
        row.Removed += RemoveRow;
        row.PickRequested += Pick;
        rules.Controls.Add(row); Renumber(); ApplyRules(); return row;
    }
    void Renumber() { int n = 0; foreach (var row in Rows()) row.SetIndex(++n); }
    // 移除一列。抽成具名方法而不是留在 Removed 的 lambda 裡，測試才能直接呼叫——
    // 用 Button.PerformClick() 是不行的：表單沒有 Show() 過，整條父鏈的 Visible 都是 false，
    // Button.CanSelect 因此為 false，PerformClick() 會靜默不做事。
    internal void RemoveRow(ColorRuleRow row)
    {
        if (row == null || !rules.Controls.Contains(row)) return;
        rules.Controls.Remove(row); row.Dispose();
        Renumber(); ApplyRules(); status.Text = "已移除色碼組";
    }
    // 只把能解析的色碼送進掃描，輸入途中的半成品不會中斷掃描。
    void ApplyRules()
    {
        var matches = new List<ColorMatch>();
        foreach (var row in Rows())
        {
            var match = row.IsValid ? row.Compiled : null;
            if (match != null) matches.Add(match);
        }
        overlay.Matches = matches;
        int total = rules.Controls.Count;
        overlay.Scanning = scanning && overlayShown && overlay.Visible && matches.Count > 0;
        if (total == 0) status.Text = "請先按「＋ 新增色碼組」輸入要找的顏色。";
        else if (matches.Count < total) status.Text = "有 " + (total - matches.Count) + " 組色碼還沒填好，" + (scanning ? "先用其餘 " + matches.Count + " 組掃描。" : "填好後可開始掃描。");
        overlay.Invalidate();
    }
    void ApplyArea()
    {
        var area = new Rectangle((int)areaX.Value, (int)areaY.Value, (int)areaW.Value, (int)areaH.Value);
        if (overlay.ScanArea != area) overlay.ScanArea = area;
    }
    void SyncArea(Rectangle area) { syncing = true; try { areaX.Value = Clamp(areaX, area.X); areaY.Value = Clamp(areaY, area.Y); areaW.Value = Clamp(areaW, area.Width); areaH.Value = Clamp(areaH, area.Height); } finally { syncing = false; } }
    static decimal Clamp(NumericUpDown field, int value) { return Math.Max(field.Minimum, Math.Min(field.Maximum, value)); }
    void Center() { var screen = Screen.PrimaryScreen.WorkingArea; syncing = true; try { areaX.Value = screen.Left + (screen.Width - (int)areaW.Value) / 2; areaY.Value = screen.Top + (screen.Height - (int)areaH.Value) / 2; } finally { syncing = false; } ApplyArea(); status.Text = "掃描方塊已置中"; }
    internal void ShowOverlay(bool visible)
    {
        overlayShown = visible;
        if (visible) { if (!overlay.Visible && IsHandleCreated && !DesignMode) overlay.Show(); } else if (overlay.Visible) overlay.Hide();
        ApplyRules();
    }
    internal bool IsScanning { get { return scanning; } }
    // 開始掃描：自動鎖定穿透，工具視窗與主視窗一起縮小；結束掃描：兩個視窗一起彈回並回到編輯模式。
    internal void SetScanning(bool on)
    {
        if (on)
        {
            int valid = Rows().Count(r => r.IsValid);
            if (valid == 0) throw new Exception("請先新增至少一組填妥的色碼組，再開始掃描。");
            scanning = true; ShowOverlay(true); SetLocked(true);
            scanButton.Text = "結束掃描 (F8)"; scanButton.BackColor = Color.MistyRose;
            status.Text = "掃描中（" + valid + " 組色碼）｜" + (hotkeysReady ? "按 F8 結束" : "請用按鈕結束");
            if (IsHandleCreated) WindowState = FormWindowState.Minimized;
            MinimizeOwner();
        }
        else
        {
            scanning = false; overlay.Scanning = false; SetLocked(false); ShowOverlay(true);
            scanButton.Text = "開始掃描 (F7)"; scanButton.BackColor = Color.LightGreen;
            RestoreOwner();
            if (IsHandleCreated)
            {
                if (!Visible) Show();
                if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
                Activate(); BringToFront();
            }
            status.Text = "已結束掃描，回到編輯模式，可拖曳邊框重新定位。";
        }
    }
    // 掃描時把主視窗也收掉，畫面才不會被擋住；只還原「自己收起來的」那一次，使用者本來就最小化的不動。
    void MinimizeOwner()
    {
        var parent = Owner;
        if (parent == null || !parent.IsHandleCreated || parent.WindowState == FormWindowState.Minimized) return;
        ownerState = parent.WindowState; ownerMinimized = true; parent.WindowState = FormWindowState.Minimized;
    }
    void RestoreOwner()
    {
        var parent = Owner;
        if (!ownerMinimized || parent == null || !parent.IsHandleCreated) { ownerMinimized = false; return; }
        ownerMinimized = false; parent.WindowState = ownerState;
    }
    internal NumericUpDown[] GeometryFields() { return new[] { areaX, areaY, areaW, areaH }; }
    // 誤差預覽：拿當下所有列（含還沒填好的，預覽視窗自己會標示）開一個非模態視窗。
    // 非模態才能一邊看極限色一邊調誤差，改完再按一次看新的範圍。
    internal List<ColorRule> PreviewRules() { return Rows().Select(r => r.Value).ToList(); }
    void ShowTolerancePreview()
    {
        var window = new ToleranceForm(PreviewRules());
        window.Owner = this;
        window.Show();
        status.Text = "誤差預覽已開啟：畫出來的每個顏色都會被判定為命中。改完誤差再按一次可以看新的範圍。";
    }
    // 鎖定＝方塊完全固定＋全穿透，滑鼠事件一律傳到下方程式；編輯模式＝可拖曳邊框移動與四角縮放。
    internal void SetLocked(bool locked)
    {
        overlay.Locked = locked; lockButton.Text = locked ? "編輯掃描範圍" : "鎖定掃描範圍";
        lockButton.BackColor = locked ? Color.FromArgb(235, 235, 235) : Color.White;
        foreach (var field in GeometryFields()) field.Enabled = !locked;
        centerButton.Enabled = !locked;
        // 掃描中鎖定是強制的：若讓使用者在掃描途中解鎖，方塊會變成可拖曳又不穿透，
        // 但掃描還在跑。要重新定位請先按 F8 結束掃描。
        lockButton.Enabled = !scanning;
        if (!scanning) status.Text = locked ? "已鎖定：方塊固定，滑鼠完全穿透到下方程式。" : "編輯模式：拖曳藍色邊框移動、四角縮放。";
    }
    void Report(ScanResult result)
    {
        var rows = Rows().Where(r => r.IsValid).ToArray();
        if (rows.Length == 0 || result.Counts.Length == 0) { status.Text = "尚無可掃描的色碼組。"; return; }
        var parts = new List<string>();
        for (int i = 0; i < result.Counts.Length && i < rows.Length; i++)
        {
            var area = overlay.ScanArea;
            parts.Add("#" + (i + 1) + " " + rows[i].Target.Text.Trim() + "：" + result.Counts[i] + " 格" + (result.Counts[i] == 0 ? "" : "（中心 " + (area.X + result.Centers[i].X) + ", " + (area.Y + result.Centers[i].Y) + "）"));
        }
        status.Text = string.Join("　", parts);
    }
    // 吸色是非模態的：把自己的視窗讓開後啟動鉤子等使用者點擊，結果由 PickDone 收。
    // 讓開的原因是按下「吸色」時本工具視窗必然在最前面，會擋住要吸的目標；
    // 掃描方塊也一起收起來，免得吸到自己畫的高亮色。
    //
    // 用最小化而不是 Hide()：Hide() 會讓視窗從工作列與 Alt+Tab 一起消失，
    // 萬一鉤子中途被系統移除，使用者就完全找不回這個程式。最小化同樣不擋住目標，
    // 但永遠點得回來，再加上 pickGuard 逾時，兩層都不會把人卡死。
    void Pick(ColorRuleRow row)
    {
        if (picking) return;
        var parent = Owner;
        picking = true; pickRow = row; pickOverlay = overlayShown;
        pickSelf = IsHandleCreated && WindowState != FormWindowState.Minimized;
        pickSelfState = WindowState;
        pickParent = parent != null && parent.IsHandleCreated && parent.WindowState != FormWindowState.Minimized;
        pickParentState = pickParent ? parent.WindowState : FormWindowState.Normal;
        try
        {
            if (pickOverlay) ShowOverlay(false);
            if (pickSelf) WindowState = FormWindowState.Minimized;
            if (pickParent) parent.WindowState = FormWindowState.Minimized;
            picker = new ColorPicker(this);
            picker.Finished += PickDone;
            picker.Start();
            pickGuard.Start();
        }
        catch (Exception ex) { PickDone(null); status.Text = "無法開始吸色：" + ex.Message; }
    }
    void PickDone(Color? chosen)
    {
        pickGuard.Stop();
        if (picker != null) { picker.Finished -= PickDone; picker.Dispose(); picker = null; }
        var parent = Owner;
        if (pickParent && parent != null && !parent.IsDisposed && parent.IsHandleCreated) parent.WindowState = pickParentState;
        if (pickSelf && !IsDisposed && IsHandleCreated)
        {
            if (!Visible) Show();
            WindowState = pickSelfState; Activate(); BringToFront();
        }
        if (pickOverlay) ShowOverlay(true);
        picking = false;
        var row = pickRow; pickRow = null;
        if (!chosen.HasValue || row == null || row.IsDisposed) { status.Text = "已取消吸色"; return; }
        row.Target.Text = ColorRule.Format(chosen.Value);
        status.Text = "已擷取顏色 " + row.Target.Text;
    }
}
