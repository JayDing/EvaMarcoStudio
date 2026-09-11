using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

public class SequenceItem {public string Name{get;set;} public int Runs{get;set;} public Template Template{get;set;}}
public class SequencePlan {
 public string Kind{get;set;} public int Version{get;set;} public int TransitionDelay{get;set;} public List<SequenceItem> Items{get;set;}
 public SequencePlan(){Kind="MacroSequence";Version=1;TransitionDelay=1000;Items=new List<SequenceItem>();}
 public static void Validate(SequencePlan p){if(p==null||p.Kind!="MacroSequence"||p.Version!=1||p.Items==null||p.Items.Count>1000||p.TransitionDelay<0||p.TransitionDelay>86400000)throw new Exception("範本組合格式無效。");foreach(var i in p.Items){if(i==null||string.IsNullOrWhiteSpace(i.Name)||i.Runs<1||i.Runs>1000000)throw new Exception("每個範本的執行次數須為 1～1000000。");MainForm.Validate(i.Template);if(i.Template.Steps.Count==0)throw new Exception(i.Name+" 沒有動作。");}}
}
public static class MacroRunner {
 public static async Task RunTemplate(Template t,int runs,CancellationToken token,Action<string> report,Func<Step,CancellationToken,Task> perform=null,Func<int,CancellationToken,Task> wait=null,Action<long> remaining=null){
  MainForm.Validate(t);if(runs<0||runs>1000000||t.Steps.Count==0)throw new Exception("循環設定無效或範本沒有動作。");perform=perform??Perform;wait=wait??((ms,ct)=>Task.Delay(ms,ct));
  for(long cycle=1;runs==0||cycle<=runs;cycle++){if(remaining!=null)remaining(runs==0?-1:runs-cycle+1);
   for(int index=0;index<t.Steps.Count;index++){token.ThrowIfCancellationRequested();report("循環 "+cycle+(runs==0?"":" / "+runs)+" · 動作 "+(index+1)+" / "+t.Steps.Count);await perform(t.Steps[index],token);await wait(t.Steps[index].Delay,token);}
   if(remaining!=null)remaining(runs==0?-1:runs-cycle);if(runs==0||cycle<runs)await wait(t.Gap,token);await wait(1,token);
  }
 }
 public static async Task RunSequence(SequencePlan plan,CancellationToken token,Action<string> report,Func<Step,CancellationToken,Task> perform=null,Func<int,CancellationToken,Task> wait=null,Action<string,long> progress=null){
  SequencePlan.Validate(plan);if(plan.Items.Count==0)throw new Exception("請加入範本。");wait=wait??((ms,ct)=>Task.Delay(ms,ct));
  for(int i=0;i<plan.Items.Count;i++){token.ThrowIfCancellationRequested();var item=plan.Items[i];string prefix="範本 "+(i+1)+" / "+plan.Items.Count+" · "+item.Name+"｜";await RunTemplate(item.Template,item.Runs,token,s=>report(prefix+s),perform,wait,n=>{if(progress!=null)progress(item.Name,n);});if(i<plan.Items.Count-1)await wait(plan.TransitionDelay,token);}
 }
 static async Task Perform(Step s,CancellationToken token){
  if(s.Type=="滑鼠點擊"){
   if(!Screen.AllScreens.Any(sc=>sc.Bounds.Contains(s.X,s.Y)))throw new Exception("滑鼠座標不在目前螢幕範圍內。");Native.Move(s.X,s.Y);bool down=false;
   try{Native.Mouse(s.Value,false);down=true;await Task.Delay(s.Hold,token);}finally{if(down)Native.Mouse(s.Value,true);}
  }else if(s.Type=="鍵盤按壓"){
   var pressed=new List<ushort>();try{foreach(var k in MainForm.ParseKeys(s.Value)){Native.Key(k,false);pressed.Add(k);}await Task.Delay(s.Hold,token);}
   finally{Exception error=null;pressed.Reverse();foreach(var k in pressed){try{Native.Key(k,true);}catch(Exception ex){error=ex;}}if(error!=null)throw error;}
  }
 }
}
public class SequenceForm:Form {
 readonly DataGridView grid=new DataGridView{Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,MultiSelect=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,RowHeadersVisible=false,BackgroundColor=Color.White};
 readonly NumericUpDown runs=new NumericUpDown{Minimum=1,Maximum=1000000,Value=1000,Width=120},gap=new NumericUpDown{Minimum=0,Maximum=86400000,Value=1000,Width=120};
 readonly Label status=new Label{AutoSize=true,Text="就緒｜執行前倒數 3 秒；F10 停止整個組合"};
 readonly List<Control> editing=new List<Control>();readonly JavaScriptSerializer json=new JavaScriptSerializer{MaxJsonLength=64*1024*1024};readonly Func<bool> canRun;
 CancellationTokenSource cancellation;
 public SequenceForm(Func<bool> ready){
  canRun=ready;Text="範本組合";Size=new Size(980,650);MinimumSize=new Size(900,600);StartPosition=FormStartPosition.CenterParent;Font=new Font("Microsoft JhengHei UI",10);
  var layout=new TableLayoutPanel{Dock=DockStyle.Fill,RowCount=5,ColumnCount=1,Padding=new Padding(16)};Controls.Add(layout);
  foreach(int height in new[]{50,60,0,60,46})layout.RowStyles.Add(new RowStyle(height==0?SizeType.Percent:SizeType.Absolute,height==0?100:height));
  var toolbar=Row();layout.Controls.Add(toolbar);Button(toolbar,"加入範本…",AddTemplates);Button(toolbar,"載入組合…",LoadPlan);Button(toolbar,"儲存組合…",SavePlan);Button(toolbar,"上移",()=>MoveItem(-1));Button(toolbar,"下移",()=>MoveItem(1));Button(toolbar,"刪除",()=>{if(grid.SelectedRows.Count>0)grid.Rows.Remove(grid.SelectedRows[0]);Renumber();});
  layout.Controls.Add(new Label{Dock=DockStyle.Fill,Text="依序執行每列的循環次數，例如 A × 1000 → B × 1000 → C × 500。\n此處次數取代各範本原本的循環次數；動作與循環間隔沿用範本設定。"});
  foreach(string name in new[]{"順序","範本","循環次數","動作數","循環間隔（ms）"})grid.Columns.Add(name,name);grid.Columns[0].FillWeight=35;grid.Columns[1].FillWeight=200;grid.RowTemplate.Height=34;layout.Controls.Add(grid);
  grid.SelectionChanged+=(s,e)=>{if(grid.SelectedRows.Count>0&&grid.SelectedRows[0].Tag is SequenceItem)runs.Value=((SequenceItem)grid.SelectedRows[0].Tag).Runs;};
  var settings=Row();layout.Controls.Add(settings);settings.Controls.Add(new Label{Text="所選範本次數",AutoSize=true,Margin=new Padding(3,9,3,0)});settings.Controls.Add(runs);Button(settings,"套用次數",()=>{if(grid.SelectedRows.Count>0){var row=grid.SelectedRows[0];((SequenceItem)row.Tag).Runs=(int)runs.Value;Render(row);}});settings.Controls.Add(new Label{Text="範本切換等待（ms）",AutoSize=true,Margin=new Padding(15,9,3,0)});settings.Controls.Add(gap);
  var footer=Row();layout.Controls.Add(footer);Button(footer,"執行組合",async()=>await Run());var start=(Button)footer.Controls[footer.Controls.Count-1];start.Font=new Font(Font.FontFamily,14,FontStyle.Bold);start.BackColor=Color.LightGreen;start.UseVisualStyleBackColor=false;var stop=new Button{Text="停止 F10",AutoSize=true,Font=new Font(Font.FontFamily,14,FontStyle.Bold)};stop.Click+=(s,e)=>Stop();footer.Controls.Add(stop);footer.Controls.Add(status);editing.Add(grid);editing.Add(runs);editing.Add(gap);
  FormClosing+=(s,e)=>{if(cancellation!=null){e.Cancel=true;Stop();status.Text="正在停止，完成後可關閉。";}};
 }
 static FlowLayoutPanel Row(){return new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=true};}
 void Button(FlowLayoutPanel row,string text,Action action){var b=new Button{Text=text,AutoSize=true};b.Click+=(s,e)=>{try{action();}catch(Exception ex){MessageBox.Show(this,ex.Message,"範本組合");}};row.Controls.Add(b);editing.Add(b);}
 void Render(DataGridViewRow row){var i=(SequenceItem)row.Tag;row.SetValues(row.Index+1,i.Name,i.Runs,i.Template.Steps.Count,i.Template.Gap);}
 void Renumber(){foreach(DataGridViewRow row in grid.Rows)Render(row);}
 public void AddItem(SequenceItem item){int index=grid.Rows.Add();grid.Rows[index].Tag=item;Render(grid.Rows[index]);grid.ClearSelection();grid.Rows[index].Selected=true;}
 void AddTemplates(){using(var d=new OpenFileDialog{Filter="動作範本 (*.json)|*.json",Multiselect=true})if(d.ShowDialog(this)==DialogResult.OK){var items=new List<SequenceItem>();foreach(var path in d.FileNames){string data=File.ReadAllText(path);var raw=json.DeserializeObject(data) as Dictionary<string,object>;if(raw==null||raw.ContainsKey("Kind"))throw new Exception("請選擇動作範本，不能加入另一個組合。");var t=json.Deserialize<Template>(data);var item=new SequenceItem{Name=Path.GetFileNameWithoutExtension(path),Runs=(int)runs.Value,Template=t};SequencePlan.Validate(new SequencePlan{Items=new List<SequenceItem>{item}});items.Add(item);}foreach(var item in items)AddItem(item);}}
 public void SetTransition(int value){gap.Value=value;}
 public SequencePlan Current(){return new SequencePlan{TransitionDelay=(int)gap.Value,Items=grid.Rows.Cast<DataGridViewRow>().Select(r=>(SequenceItem)r.Tag).ToList()};}
 void MoveItem(int delta){if(grid.SelectedRows.Count==0)return;int a=grid.SelectedRows[0].Index,b=a+delta;if(b<0||b>=grid.Rows.Count)return;object temp=grid.Rows[a].Tag;grid.Rows[a].Tag=grid.Rows[b].Tag;grid.Rows[b].Tag=temp;Renumber();grid.ClearSelection();grid.Rows[b].Selected=true;}
 void SavePlan(){var plan=Current();SequencePlan.Validate(plan);using(var d=new SaveFileDialog{Filter="範本組合 (*.sequence.json)|*.sequence.json",FileName="我的範本組合.sequence.json"})if(d.ShowDialog(this)==DialogResult.OK){string temp=d.FileName+".tmp";File.WriteAllText(temp,json.Serialize(plan),System.Text.Encoding.UTF8);if(File.Exists(d.FileName))File.Replace(temp,d.FileName,null);else File.Move(temp,d.FileName);status.Text="組合已儲存（包含各範本內容）";}}
 void LoadPlan(){using(var d=new OpenFileDialog{Filter="範本組合 (*.sequence.json)|*.sequence.json|JSON (*.json)|*.json"})if(d.ShowDialog(this)==DialogResult.OK){string data=File.ReadAllText(d.FileName);var raw=json.DeserializeObject(data) as Dictionary<string,object>;if(raw==null||!raw.ContainsKey("Kind")||Convert.ToString(raw["Kind"])!="MacroSequence")throw new Exception("請選擇範本組合檔案。");var plan=json.Deserialize<SequencePlan>(data);SequencePlan.Validate(plan);if(grid.Rows.Count>0&&MessageBox.Show(this,"取代目前組合清單？未儲存的修改將遺失。","載入組合",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;grid.Rows.Clear();foreach(var item in plan.Items)AddItem(item);gap.Value=plan.TransitionDelay;status.Text="組合已載入";}}
 public void Stop(){if(cancellation!=null)cancellation.Cancel();}
 async Task Run(){if(cancellation!=null)return;try{if(!canRun())throw new Exception("F10 停止熱鍵不可用，請關閉占用熱鍵的程式後重新啟動。");var plan=Current();SequencePlan.Validate(plan);if(plan.Items.Count==0)throw new Exception("請先加入範本。");cancellation=new CancellationTokenSource();foreach(var c in editing)c.Enabled=false;var token=cancellation.Token;await CountdownOverlay.Run(token,s=>status.Text=s);using(var badge=new RunBadge()){badge.Show();await MacroRunner.RunSequence(plan,token,s=>status.Text=s,progress:(name,n)=>badge.SetProgress(name,n));}status.Text="全部範本執行完成";}catch(OperationCanceledException){status.Text="已停止整個組合";}catch(Exception ex){status.Text="執行中止";MessageBox.Show(this,ex.Message,"範本組合");}finally{if(cancellation!=null){cancellation.Dispose();cancellation=null;}foreach(var c in editing)c.Enabled=true;}}
}
