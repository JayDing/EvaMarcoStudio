using System;
using System.Drawing;
using System.Windows.Forms;

public class RunBadge:Form {
 string detail="";Step next;long countdown;readonly System.Diagnostics.Stopwatch clock=new System.Diagnostics.Stopwatch();readonly Timer refresh=new Timer{Interval=100};
 public RunBadge(){FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;StartPosition=FormStartPosition.Manual;Size=new Size(440,145);refresh.Tick+=(s,e)=>Invalidate();refresh.Start();var screen=Screen.PrimaryScreen.WorkingArea;Location=new Point(screen.Right-Width-16,screen.Bottom-Height-16);BackColor=Color.FromArgb(20,29,45);Opacity=.94;DoubleBuffered=true;}
 protected override bool ShowWithoutActivation{get{return true;}}
 protected override CreateParams CreateParams{get{var p=base.CreateParams;p.ExStyle|=0x08000000|0x00080000|0x00000020|0x00000080;return p;}}
 protected override void WndProc(ref Message m){if(m.Msg==0x84){m.Result=new IntPtr(-1);return;}if(m.Msg==0x21){m.Result=new IntPtr(3);return;}base.WndProc(ref m);}
 public static string Describe(string name,long remaining){return name+"｜"+(remaining<0?"持續循環":("剩餘 "+remaining+" 次"));}
 public void SetProgress(string name,long remaining){detail=Describe(name,remaining);Invalidate();}
 public static string DescribeUpcoming(Step step,long milliseconds){return step==null?"下個預計執行：無（本次即將完成）":"下個預計執行 ["+step.Type+"] (倒數 "+(Math.Ceiling(Math.Max(0,milliseconds)/100.0)/10).ToString("0.0")+" sec)";}
 public void SetUpcoming(Step step,long milliseconds){next=step;countdown=milliseconds;clock.Restart();Invalidate();}
 protected override void Dispose(bool disposing){if(disposing)refresh.Dispose();base.Dispose(disposing);}
 protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);using(var title=new Font("Microsoft JhengHei UI",19,FontStyle.Bold))using(var small=new Font("Microsoft JhengHei UI",11))using(var format=new StringFormat{Trimming=StringTrimming.EllipsisCharacter}){e.Graphics.DrawString("按鍵精靈執行中",title,Brushes.LightGreen,16,14);e.Graphics.DrawString(DescribeUpcoming(next,countdown-clock.ElapsedMilliseconds),small,Brushes.LightGray,new RectangleF(16,59,Width-32,30),format);e.Graphics.DrawString(detail,small,Brushes.White,new RectangleF(16,96,Width-32,34),format);}}
}
public static class CellEdits {
 public static bool CanEdit(Step s,int column){return column==1||column==6||column==7||(s.Type=="滑鼠點擊"&&(column==2||column==3))||(s.Type!="等待"&&(column==4||column==5));}
 public static Step Apply(Step original,int column,string value){
  if(!CanEdit(original,column))throw new Exception("此欄位不適用於這個動作。");var s=MainForm.CopyStep(original);int number;
  switch(column){
   case 1:if(value!=s.Type){s.Type=value;s.X=s.Y=0;s.Value=value=="滑鼠點擊"?"左鍵":value=="鍵盤按壓"?"Space":"";s.Hold=value=="等待"?0:50;}break;
   case 6:s.Delay=WaitUnits.Parse(value);break;case 2:case 3:case 5:if(!int.TryParse(value,out number))throw new Exception("請輸入有效的整數。");if(column==2)s.X=number;else if(column==3)s.Y=number;else if(column==5)s.Hold=number;else s.Delay=number;break;
   case 4:s.Value=value.Trim();break;case 7:s.Notes=value;break;
  }
  MainForm.ValidateStep(s);return s;
 }
}
