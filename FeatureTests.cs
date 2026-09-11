using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

public static class FeatureTests {
 static void Check(bool condition,string label){if(!condition)throw new Exception(label);}
 static SequenceItem Item(string name,int runs){return new SequenceItem{Name=name,Runs=runs,Template=new Template{Repeats=77,Gap=17,Steps=new List<Step>{new Step{Type="鍵盤按壓",Value=name,Hold=0,Delay=23}}}};}
 public static void Run(){TestTemplateSaving();
  TestBatchAndSeconds();
  TestLiveUI();
  TestInteraction();
  var plan=new SequencePlan{TransitionDelay=31,Items=new List<SequenceItem>{Item("A",1000),Item("B",1000),Item("C",500)}};
  var trace=new List<string>();var delays=new List<int>();Func<int,CancellationToken,Task> wait=(ms,ct)=>{ct.ThrowIfCancellationRequested();delays.Add(ms);return Task.FromResult(0);};
  MacroRunner.RunSequence(plan,CancellationToken.None,s=>{},(step,ct)=>{trace.Add(step.Value);return Task.FromResult(0);},wait).GetAwaiter().GetResult();
  Check(trace.SequenceEqual(Enumerable.Repeat("A",1000).Concat(Enumerable.Repeat("B",1000)).Concat(Enumerable.Repeat("C",500))),"A1000 B1000 C500 execution order and override");Check(delays.Count(ms=>ms==31)==2&&delays.Count(ms=>ms==17)==2497&&delays.Count(ms=>ms==23)==2500,"Sequence timing");
  using(var stop=new CancellationTokenSource()){trace.Clear();bool canceled=false;try{MacroRunner.RunSequence(plan,stop.Token,s=>{},(step,ct)=>{trace.Add(step.Value);stop.Cancel();return Task.FromResult(0);},wait).GetAwaiter().GetResult();}catch(OperationCanceledException){canceled=true;}Check(canceled&&trace.Count==1,"Cancel halts remaining templates");}
  using(var stop=new CancellationTokenSource()){trace.Clear();bool canceled=false;var shortPlan=new SequencePlan{TransitionDelay=31,Items=new List<SequenceItem>{Item("A",1),Item("B",1)}};try{MacroRunner.RunSequence(shortPlan,stop.Token,s=>{},(step,ct)=>{trace.Add(step.Value);return Task.FromResult(0);},(ms,ct)=>{if(ms==31)stop.Cancel();ct.ThrowIfCancellationRequested();return Task.FromResult(0);}).GetAwaiter().GetResult();}catch(OperationCanceledException){canceled=true;}Check(canceled&&trace.SequenceEqual(new[]{"A"}),"Cancel during transition");}
  trace.Clear();bool failed=false;try{MacroRunner.RunSequence(plan,CancellationToken.None,s=>{},(step,ct)=>{trace.Add(step.Value);throw new Exception("simulated failure");},wait).GetAwaiter().GetResult();}catch{failed=true;}Check(failed&&trace.Count==1,"Action failure aborts sequence");
  var json=new JavaScriptSerializer();var copy=json.Deserialize<SequencePlan>(json.Serialize(plan));SequencePlan.Validate(copy);plan.Items[0].Template.Steps[0].Value="Z";Check(copy.Items[0].Template.Steps[0].Value=="A"&&copy.Items[2].Runs==500,"Embedded templates roundtrip");copy.Items[0].Runs=0;bool bad=false;try{SequencePlan.Validate(copy);}catch{bad=true;}Check(bad,"No infinite stage counts");
  Check(KeyPicker.FromKeyData(Keys.A)=="A"&&KeyPicker.FromKeyData(Keys.Control|Keys.Shift|Keys.S)=="Ctrl+Shift+S"&&KeyPicker.FromKeyData(Keys.D1)=="1","Key capture mapping");
  using(var picker=new KeyPicker()){Check(picker.Value=="Space","Single-key default");Check(picker.Controls.OfType<Button>().Count()==1&&!picker.Controls.OfType<Label>().Any(),"Only capture button and default key remain");picker.Value="Ctrl+C";
   var capture=picker.Controls.OfType<Button>().First(b=>b.Text=="捕捉按鍵");var input=picker.Controls.OfType<TextBox>().First();var down=input.GetType().GetMethod("OnKeyDown",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
   capture.PerformClick();down.Invoke(input,new object[]{new KeyEventArgs(Keys.A)});Check(picker.Value=="A"&&!picker.Recording,"Capture actual single-key event");capture.PerformClick();down.Invoke(input,new object[]{new KeyEventArgs(Keys.Control|Keys.S)});Check(picker.Value=="Ctrl+S"&&!picker.Recording,"Capture actual combination event");capture.PerformClick();down.Invoke(input,new object[]{new KeyEventArgs(Keys.Escape)});Check(picker.Value=="Ctrl+S"&&!picker.Recording&&!KeyPicker.AnyRecording,"Capture cancel preserves value");
  }
  using(var main=new MainForm()){var nums=Descendants(main).OfType<NumericUpDown>();Check(nums.Count(n=>n.Maximum==1000000&&n.Value==0)==1&&nums.Count(n=>n.Maximum==86400&&n.Value==1)==2,"1000ms default delay and gap");Check(Descendants(main).OfType<CrosshairDrag>().Count()==1,"New mouse action has draggable crosshair");}
  using(var form=new SequenceForm(()=>true)){foreach(var item in plan.Items)form.AddItem(item);var panel=form.Controls[0];form.Controls.Remove(panel);panel.Size=form.ClientSize;panel.CreateControl();panel.PerformLayout();using(var bitmap=new Bitmap(panel.Width,panel.Height)){panel.DrawToBitmap(bitmap,new Rectangle(Point.Empty,panel.Size));bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"preview-sequence.png"));}panel.Dispose();}
  File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"feature-test-result.txt"),"PASS: exact A1000/B1000/C500 order; repeat override; action, cycle and transition intervals; cancellation within action and transition; failure abort; embedded snapshot roundtrip; positive repeat validation; single-key defaults; capture mapping; common-key replacement; 1000ms defaults; new crosshair control. Input execution mocked; no desktop input sent.");
 }
 static void TestTemplateSaving(){
  var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
  Func<MainForm,string,object[],object> invoke=(form,name,args)=>typeof(MainForm).GetMethod(name,flags).Invoke(form,args);
  Func<MainForm,bool> dirty=form=>(bool)invoke(form,"HasUnsavedChanges",new object[0]);
  string folder=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"save-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);string path=Path.Combine(folder,"original.json");
  using(var form=new MainForm()){
   Check(!dirty(form),"New empty template is clean");var a=new Step{Type="滑鼠點擊",Value="左鍵",X=12,Y=34,Hold=50,Delay=500,Notes="original"};invoke(form,"AddStep",new object[]{a});Check(dirty(form),"New action is unsaved");
   invoke(form,"SaveToPath",new object[]{path});Check(File.Exists(path)&&!dirty(form),"Save establishes baseline");a.Notes="changed";Check(dirty(form),"Notes change detected");
   object[] hotkey={new Message(),Keys.Control|Keys.S};Check((bool)invoke(form,"ProcessCmdKey",hotkey),"Ctrl+S handled");var saved=new JavaScriptSerializer().Deserialize<Template>(File.ReadAllText(path));Check(saved.Steps[0].Notes=="changed"&&!dirty(form),"Ctrl+S overwrites original and clears dirty state");
   a.Notes="temporary";a.Notes="changed";Check(!dirty(form),"Reverting changes restores clean state");a.Delay=1000;bool failed=false;try{invoke(form,"SaveToPath",new object[]{Path.Combine(folder,"missing","bad.json")});}catch(System.Reflection.TargetInvocationException){failed=true;}Check(failed&&dirty(form),"Failed save preserves unsaved state");
   invoke(form,"SaveCurrentTemplate",new object[0]);Check(!dirty(form)&&new JavaScriptSerializer().Deserialize<Template>(File.ReadAllText(path)).Steps[0].Delay==1000,"Failed save does not change target path");
   var grid=Descendants(form).OfType<ActionGrid>().Single();grid.Rows.Clear();Check(dirty(form),"Deleting all actions remains unsaved");
  }
  using(var loaded=new MainForm()){
   var template=new JavaScriptSerializer().Deserialize<Template>(File.ReadAllText(path));foreach(var step in template.Steps)invoke(loaded,"AddStep",new object[]{step});invoke(loaded,"MarkSaved",new object[]{path});Check(!dirty(loaded)&&(bool)invoke(loaded,"ConfirmLeave",new object[0]),"Loaded template can leave without warning");template.Steps[0].X=999;Check(dirty(loaded),"Loaded template edits detected");invoke(loaded,"SaveCurrentTemplate",new object[0]);Check(new JavaScriptSerializer().Deserialize<Template>(File.ReadAllText(path)).Steps[0].X==999,"Loaded template saves to original path");
  }
  File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"save-test-result.txt"),"PASS: clean/new/loaded states, Ctrl+S original path overwrite, notes and deletion detection, revert-to-clean, failed save retains dirty state and target, loaded template save. No desktop input sent.");
 }
 static void TestBatchAndSeconds(){
  Check(WaitUnits.ToMilliseconds(1)==1000&&WaitUnits.ToMilliseconds(.5m)==500&&WaitUnits.ToMilliseconds(.001m)==1&&WaitUnits.ToMilliseconds(0)==0,"Seconds conversion exact");
  foreach(decimal n in new[]{-.1m,.0001m,86400.001m}){bool rejected=false;try{WaitUnits.ToMilliseconds(n);}catch{rejected=true;}Check(rejected,"Seconds range and precision");}
  var a=new Step{Type="滑鼠點擊",Value="左鍵",X=10,Y=20,Hold=50,Delay=500,Notes="A"};var b=new Step{Type="滑鼠點擊",Value="右鍵",X=30,Y=40,Hold=80,Delay=1250,Notes="B"};
  var changed=new BatchPatch{Delay=2000}.Apply(new[]{a,b});Check(changed.All(s=>s.Delay==2000)&&changed[0].X==10&&changed[1].X==30&&changed[1].Hold==80&&changed[1].Value=="右鍵"&&changed[1].Notes=="B"&&b.Delay==1250,"Batch only selected fields change");
  changed=new BatchPatch{SetNotes=true,Notes=""}.Apply(new[]{a,b});Check(changed.All(s=>s.Notes=="")&&a.Notes=="A","Batch clear notes without source mutation");
  bool mixed=false;try{new BatchPatch{Delay=1000}.Apply(new[]{a,new Step{Type="等待",Delay=1000}});}catch{mixed=true;}Check(mixed,"Mixed types rejected");bool bad=false;try{new BatchPatch{Hold=-1}.Apply(new[]{a,b});}catch{bad=true;}Check(bad&&a.Hold==50&&b.Hold==80,"Invalid batch atomicity");
  Check(WaitUnits.RowColor("滑鼠點擊")!=WaitUnits.RowColor("鍵盤按壓")&&WaitUnits.RowColor("鍵盤按壓")!=WaitUnits.RowColor("等待"),"Distinct row colors");
  using(var editor=new StepEditor(a)){Check(editor.BuildResult().Delay==500,"Legacy milliseconds roundtrip in seconds editor");}
  using(var dialog=new BatchEditor(new[]{a,b})){var panel=dialog.Controls[0];dialog.Controls.Remove(panel);panel.Size=dialog.ClientSize;panel.CreateControl();using(var bitmap=new Bitmap(panel.Width,panel.Height)){panel.DrawToBitmap(bitmap,new Rectangle(Point.Empty,panel.Size));bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"preview-batch.png"));}panel.Dispose();}
  File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"batch-test-result.txt"),"PASS: seconds conversion and precision, old milliseconds roundtrip, selective batch patch, note clearing, mixed-type rejection, invalid batch atomicity, distinct row colors. No desktop input sent.");
 }
 static void TestLiveUI(){
  var estimatePlan=new SequencePlan{TransitionDelay=500,Items=new List<SequenceItem>{
   new SequenceItem{Name="A",Runs=2,Template=new Template{Gap=100,Steps=new List<Step>{new Step{Type="鍵盤按壓",Value="A",Hold=50,Delay=200},new Step{Type="等待",Hold=999,Delay=300}}}},
   new SequenceItem{Name="B",Runs=1,Template=new Template{Gap=999,Steps=new List<Step>{new Step{Type="等待",Delay=250}}}}
  }};
  Check(SequenceForm.EstimateMilliseconds(estimatePlan)==4953&&SequenceForm.FormatDuration(4953)=="5 秒","Estimate holds, waits, cycle gaps, transitions and countdown");
  Check(SequenceForm.EstimateMilliseconds(new SequencePlan())==0,"Empty sequence estimate");
  estimatePlan.Items[0].TransitionDelay=1750;estimatePlan.Items[1].TransitionDelay=9000;
  Check(SequenceForm.EstimateMilliseconds(estimatePlan)==6203,"Per-template transition estimate excludes final transition");
  var transitionWaits=new List<int>();MacroRunner.RunSequence(estimatePlan,CancellationToken.None,s=>{},(a,ct)=>Task.FromResult(0),(ms,ct)=>{transitionWaits.Add(ms);return Task.FromResult(0);}).GetAwaiter().GetResult();
  Check(transitionWaits.Contains(1750)&&!transitionWaits.Contains(9000),"Runner uses per-template transition");
  var persisted=new JavaScriptSerializer().Deserialize<SequencePlan>(new JavaScriptSerializer().Serialize(estimatePlan));Check(persisted.Items[0].TransitionDelay==1750,"Per-template transition persists");
  var original=new Step{Type="滑鼠點擊",Value="左鍵",X=20,Y=30,Hold=50,Delay=1000,Notes="before"};Check(CellEdits.Apply(original,6,"1.5").Delay==1500&&original.Delay==1000,"Cell edit clone isolation");Check(CellEdits.Apply(original,2,"-200").X==-200&&CellEdits.Apply(original,7,"測試 123").Notes=="測試 123","Cell coordinates and notes");foreach(string invalid in new[]{"-1","1.0001","abc","999999999999"}){bool rejected=false;try{CellEdits.Apply(original,6,invalid);}catch{rejected=true;}Check(rejected,"Reject invalid cell numeric input");}Check(!CellEdits.CanEdit(original,0)&&!CellEdits.CanEdit(new Step{Type="等待"},2),"Nonapplicable cells read only");
  var forecasts=new List<string>();var forecastTemplate=new Template{Gap=40,Steps=new List<Step>{new Step{Type="鍵盤按壓",Value="A",Hold=20,Delay=100},new Step{Type="等待",Delay=200}}};
  MacroRunner.RunTemplate(forecastTemplate,2,CancellationToken.None,s=>{},(a,ct)=>Task.FromResult(0),(ms,ct)=>Task.FromResult(0),upcoming:(a,ms)=>forecasts.Add((a==null?"完成":a.Type)+":"+ms)).GetAwaiter().GetResult();
  Check(forecasts.Take(4).SequenceEqual(new[]{"等待:120","等待:100","鍵盤按壓:241","鍵盤按壓:241"})&&forecasts.Last().StartsWith("完成:"),"Upcoming action includes hold, delay and cycle gap");
  forecasts.Clear();MacroRunner.RunSequence(new SequencePlan{TransitionDelay=70,Items=new List<SequenceItem>{Item("A",1),Item("B",1)}},CancellationToken.None,s=>{},(a,ct)=>Task.FromResult(0),(ms,ct)=>Task.FromResult(0),upcoming:(a,ms)=>forecasts.Add((a==null?"完成":a.Value)+":"+ms)).GetAwaiter().GetResult();
  Check(forecasts.Contains("B:94")&&forecasts.Contains("B:70")&&forecasts.Last().StartsWith("完成:"),"Upcoming action crosses template boundary");
  Check(RunBadge.DescribeUpcoming(new Step{Type="等待"},1250).Contains("1.3 sec")&&RunBadge.DescribeUpcoming(null,0).Contains("即將完成"),"Countdown formatting and final action");
  var remaining=new List<string>();var plan=new SequencePlan{Items=new List<SequenceItem>{Item("A",2),Item("B",1)}};MacroRunner.RunSequence(plan,CancellationToken.None,s=>{},(step,ct)=>Task.FromResult(0),(ms,ct)=>Task.FromResult(0),(name,n)=>remaining.Add(name+":"+n)).GetAwaiter().GetResult();Check(remaining.SequenceEqual(new[]{"A:2","A:1","A:1","A:0","B:1","B:0"}),"Template name and remaining cycles");Check(RunBadge.Describe("A",-1).Contains("持續循環"),"Unlimited progress display");
  using(var main=new MainForm()){var add=typeof(MainForm).GetMethod("AddStep",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);add.Invoke(main,new object[]{original});var grid=Descendants(main).OfType<ActionGrid>().Single();var panel=main.Controls[0];main.Controls.Remove(panel);panel.Size=main.ClientSize;panel.CreateControl();grid.CurrentCell=grid.Rows[0].Cells[6];Check(grid.BeginEdit(true),"Begin inline edit");((TextBox)grid.EditingControl).Text="2.3";Check(grid.EndEdit()&&((Step)grid.Rows[0].Tag).Delay==2300,"Inline UI edit commits to model");grid.CurrentCell=grid.Rows[0].Cells[6];grid.BeginEdit(true);((TextBox)grid.EditingControl).Text="-5";bool ended=grid.EndEdit();Check(((Step)grid.Rows[0].Tag).Delay==2300,"Invalid inline edit leaves saved value unchanged");grid.CancelEdit();panel.Dispose();}
  using(var sequence=new SequenceForm(()=>true)){
   sequence.AddItem(Item("initial",2));var panel=sequence.Controls[0];sequence.Controls.Remove(panel);panel.Size=sequence.ClientSize;panel.CreateControl();
   var grid=Descendants(panel).OfType<DataGridView>().Single();var doubleClick=typeof(ActionGrid).GetMethod("OnMouseDoubleClick",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
   int[] columns={1,2,4,5};string[] values={"renamed","17","1.25","2.5"};
   for(int i=0;i<columns.Length;i++){var rect=grid.GetCellDisplayRectangle(columns[i],0,false);doubleClick.Invoke(grid,new object[]{new MouseEventArgs(MouseButtons.Left,2,rect.Left+5,rect.Top+5,0)});Application.DoEvents();Check(grid.IsCurrentCellInEditMode,"Sequence double click starts column "+columns[i]);((TextBox)grid.EditingControl).Text=values[i];Check(grid.EndEdit(),"Sequence cell commits "+columns[i]);}
   var item=sequence.Current().Items[0];Check(item.Name=="renamed"&&item.Runs==17&&item.Template.Gap==1250&&item.TransitionDelay==2500,"All editable sequence cells update model");panel.Dispose();
  }
  using(var sequence=new SequenceForm(()=>true)){
   foreach(string name in new[]{"A","B","C","D"})sequence.AddItem(Item(name,2));
   var grid=Descendants(sequence).OfType<ActionGrid>().Single();
   Action<string,object[]> invoke=(name,args)=>typeof(SequenceForm).GetMethod(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(sequence,args);
   grid.ClearSelection();grid.Rows[0].Selected=true;grid.Rows[2].Selected=true;invoke("CopyItems",new object[0]);invoke("PasteItems",new object[0]);
   Check(string.Join(",",sequence.Current().Items.Select(i=>i.Name))=="A,B,C,A,C,D"&&grid.SelectedRows.Count==2,"Sequence multiple paste order");
   sequence.Current().Items[3].Template.Steps[0].Notes="copy";Check(sequence.Current().Items[0].Template.Steps[0].Notes!="copy","Sequence clipboard deep copy");
   invoke("DeleteItem",new object[0]);Check(string.Join(",",sequence.Current().Items.Select(i=>i.Name))=="A,B,C,D","Sequence multi delete");
   invoke("ReorderItems",new object[]{new[]{0,2},4});Check(string.Join(",",sequence.Current().Items.Select(i=>i.Name))=="B,D,A,C"&&grid.SelectedRows.Count==2,"Sequence multi reorder preserves selection");

   var cycleGap=(NumericUpDown)typeof(SequenceForm).GetField("cycleGap",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(sequence);cycleGap.Value=1.5m;
   Check(sequence.Current().Items.Skip(2).All(i=>i.Template.Gap==1500&&i.Runs==2)&&sequence.Current().Items[0].Template.Gap==17,"Selective batch edit only selected templates");
   grid.SelectAll();invoke("DeleteItem",new object[0]);Check(sequence.Current().Items.Count==0,"Delete entire sequence");
  }
  using(var badge=new RunBadge())using(var bitmap=new Bitmap(badge.Width,badge.Height)){badge.SetProgress("A 範本",999);badge.SetUpcoming(new Step{Type="鍵盤按壓"},3250);badge.DrawToBitmap(bitmap,new Rectangle(Point.Empty,badge.Size));bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"preview-running.png"));}
  File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"live-ui-test-result.txt"),"PASS: inline numeric UI commit, invalid numeric rejection without mutation, coordinate/notes cell edits, protected cells, sequence name/remaining progress, continuous-loop label. No desktop input sent.");
 }
 static void TestInteraction(){
  var list=new[]{"A","B","C","D","E"};
  Check(ActionGrid.Reorder(list,new[]{1},5).SequenceEqual(new[]{"A","C","D","E","B"}),"Drag single down");
  Check(ActionGrid.Reorder(list,new[]{3},0).SequenceEqual(new[]{"D","A","B","C","E"}),"Drag single up");
  Check(ActionGrid.Reorder(list,new[]{2,1},5).SequenceEqual(new[]{"A","D","E","B","C"}),"Multi-drag maintains original order");
  Check(ActionGrid.Reorder(list,new[]{2,3},0).SequenceEqual(new[]{"C","D","A","B","E"}),"Multi-drag up");
  Check(ActionGrid.Reorder(list,new[]{1,2},2).SequenceEqual(list),"Drop within selected block is unchanged");
  Check(ActionGrid.Reorder(list,new[]{0,2,4},5).SequenceEqual(new[]{"B","D","A","C","E"}),"Noncontiguous selection");
  var shown=new List<int>();var waits=new List<int>();CountdownOverlay.Count(CancellationToken.None,n=>shown.Add(n),(ms,ct)=>{waits.Add(ms);return Task.FromResult(0);}).GetAwaiter().GetResult();Check(shown.SequenceEqual(new[]{3,2,1})&&waits.SequenceEqual(new[]{1000,1000,1000}),"321 countdown timing");
  using(var stop=new CancellationTokenSource()){shown.Clear();bool canceled=false;try{CountdownOverlay.Count(stop.Token,n=>{shown.Add(n);if(n==2)stop.Cancel();},(ms,ct)=>{ct.ThrowIfCancellationRequested();return Task.FromResult(0);}).GetAwaiter().GetResult();}catch(OperationCanceledException){canceled=true;}Check(canceled&&shown.SequenceEqual(new[]{3,2}),"Countdown cancellation");}
  string note="測試 123 !@#\r\n第二行 😀 \"quoted\"";var step=new Step{Type="滑鼠點擊",Value="左鍵",X=12,Y=34,Delay=1000,Notes=note};var json=new JavaScriptSerializer();Check(json.Deserialize<Step>(json.Serialize(step)).Notes==note&&MainForm.CopyStep(step).Notes==note,"Notes copied and persisted exactly");
  using(var editor=new StepEditor(step)){Check(editor.BuildResult().Notes==note,"Notes loaded for editing");Descendants(editor).OfType<TextBox>().First(t=>t.Multiline).Text="修改\n456";Check(editor.BuildResult().Notes=="修改\n456"&&step.Notes==note,"Edit notes without mutating original");var panel=editor.Controls[0];editor.Controls.Remove(panel);panel.Size=editor.ClientSize;panel.CreateControl();using(var bitmap=new Bitmap(panel.Width,panel.Height)){panel.DrawToBitmap(bitmap,new Rectangle(Point.Empty,panel.Size));bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"preview-notes-edit.png"));}panel.Dispose();}
  using(var main=new MainForm()){
   var grid=Descendants(main).OfType<ActionGrid>().Single();var add=typeof(MainForm).GetMethod("AddStep",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);for(int i=0;i<5;i++)add.Invoke(main,new object[]{new Step{Type="滑鼠點擊",Value="左鍵",X=100+i,Y=200,Delay=1000,Notes="備註 "+i}});
   var reorder=typeof(MainForm).GetMethod("ReorderActions",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);reorder.Invoke(main,new object[]{new[]{1,2},5});var steps=grid.Rows.Cast<DataGridViewRow>().Select(r=>(Step)r.Tag).ToList();Check(steps.Select(s=>s.X).SequenceEqual(new[]{100,103,104,101,102})&&steps.Select(s=>s.Sequence).SequenceEqual(new[]{1,2,3,4,5}),"UI reorder renumbers");Check(grid.SelectedRows.Count==2&&steps[3].Notes=="備註 1"&&MainForm.BuildMarkers(steps)[3].Label=="4","UI reorder preserves selection, notes and marker numbering");
  }
  using(var overlay=new CountdownOverlay())using(var bitmap=new Bitmap(overlay.Width,overlay.Height)){overlay.DrawToBitmap(bitmap,new Rectangle(Point.Empty,overlay.Size));bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"preview-countdown.png"));}
  File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"interaction-test-result.txt"),"PASS: single/multiple/noncontiguous reorder, no-op drop, original relative order, grid selection retention, marker renumbering, multiline Unicode notes persistence/copy/edit isolation, 3-2-1 countdown timing and cancellation. No desktop input was sent.");
 }
 static IEnumerable<Control> Descendants(Control c){foreach(Control child in c.Controls){yield return child;foreach(var d in Descendants(child))yield return d;}}
}
